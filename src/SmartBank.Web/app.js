/* ==========================================================================
   SmartBank Core Application Logic - app.js
   Shared helpers (storage, i18n, API, dialogs, formatting), authentication, the customer
   dashboard, the support agent panel and the live market rates box.
   chat.js (loaded after this file on dashboard.html and agent.html) owns the SignalR chat.
   ========================================================================== */
"use strict";

// Framing guard. A <meta> CSP cannot carry frame-ancestors (GitHub Pages sends no headers), so a page that finds itself
// inside another site's frame hides itself: clicks on an invisible banking page would be clickjacking.
(function frameGuard() {
    try {
        if (window.top !== window.self) {
            document.documentElement.hidden = true;
            document.documentElement.style.display = "none";
        }
    } catch (err) {
        document.documentElement.hidden = true; // reading window.top threw: we are framed by another origin
    }
})();

// One constant decides which page this is (<body data-page="login|dashboard|agent">): no URL sniffing.
const PAGE = (document.body && document.body.dataset && document.body.dataset.page) || "";
const IS_AGENT_PAGE = PAGE === "agent";
const IS_DASHBOARD_PAGE = PAGE === "dashboard";

// Where the API lives. Loopback names use the local development API; a private LAN address assumes the API on the same
// machine (add that host to connect-src in the page CSP for a LAN test); everything else is the hosted demo API.
const API_ORIGIN = (() => {
    const host = window.location.hostname;
    const isLoopback = host === "localhost" || host === "127.0.0.1" || host === "[::1]" || host === "::1" || host.endsWith(".localhost");
    if (isLoopback) return "http://localhost:5038";
    if (/^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/.test(host) || host.endsWith(".local")) return `http://${host}:5038`;
    return "https://smartbank-fintech-api.onrender.com";
})();
const API_URL = `${API_ORIGIN}/api`;
const HUBS_URL = `${API_ORIGIN}/hubs`;

// Dynamic DOM is built with h() / textContent only: nothing here ever hands a string to the HTML parser, so there is
// nothing to escape (a guard test forbids innerHTML and friends).

// A value that may only become a CSS class token: lower-case letters, digits, dash and underscore.
function cssToken(value, fallback) {
    const token = String(value ?? "").toLowerCase().replace(/[^a-z0-9_-]/g, "");
    return token || fallback || "";
}

/* ==========================================================================
   Safe storage: localStorage / sessionStorage can throw (blocked site data, private modes). Every access goes through
   these helpers and falls back to memory, so the app still works for the life of the tab.
   ========================================================================== */
function makeSafeStorage(kind) {
    const memory = new Map();
    const area = () => { try { return window[kind]; } catch (err) { return null; } };
    return {
        get(key) {
            try { const s = area(); if (s) return s.getItem(key); } catch (err) { /* fall through */ }
            return memory.has(key) ? memory.get(key) : null;
        },
        set(key, value) {
            const text = String(value);
            try { const s = area(); if (s) { s.setItem(key, text); return; } } catch (err) { /* fall through */ }
            memory.set(key, text);
        },
        remove(key) {
            try { const s = area(); if (s) s.removeItem(key); } catch (err) { /* ignore */ }
            memory.delete(key);
        }
    };
}
const safeStorage = makeSafeStorage("localStorage");
const safeSession = makeSafeStorage("sessionStorage");

/* ==========================================================================
   Localization (EN / TR). The dictionary is in the next block; every user-visible string comes from t().
   Static HTML uses data-i18n / data-i18n-placeholder / data-i18n-aria-label / data-i18n-title attributes.
   ========================================================================== */
const SUPPORTED_LANGUAGES = ["en", "tr"];
const LOCALES = { en: "en-US", tr: "tr-TR" };

function detectLanguage() {
    const stored = safeStorage.get("lang");
    if (SUPPORTED_LANGUAGES.includes(stored)) return stored;
    const nav = (navigator.languages && navigator.languages[0]) || navigator.language || "en";
    return String(nav).toLowerCase().startsWith("tr") ? "tr" : "en";
}

let currentLanguage = detectLanguage();
function locale() { return LOCALES[currentLanguage] || "en-US"; }

function hasKey(key, lang) {
    return Object.prototype.hasOwnProperty.call(i18n[lang || currentLanguage], key);
}

function t(key, vars) {
    let text = hasKey(key) ? i18n[currentLanguage][key] : (hasKey(key, "en") ? i18n.en[key] : key);
    if (vars) {
        text = text.replace(/\{(\w+)\}/g, (match, name) => (Object.prototype.hasOwnProperty.call(vars, name) ? String(vars[name]) : match));
    }
    return text;
}

const languageListeners = [];
function onLanguageChange(listener) { languageListeners.push(listener); }

function translatePage(root) {
    const scope = root || document;
    scope.querySelectorAll("[data-i18n]").forEach(el => { el.textContent = t(el.dataset.i18n); });
    scope.querySelectorAll("[data-i18n-placeholder]").forEach(el => { el.placeholder = t(el.dataset.i18nPlaceholder); });
    scope.querySelectorAll("[data-i18n-aria-label]").forEach(el => { el.setAttribute("aria-label", t(el.dataset.i18nAriaLabel)); });
    scope.querySelectorAll("[data-i18n-title]").forEach(el => { el.title = t(el.dataset.i18nTitle); });

    document.documentElement.lang = currentLanguage;
    const titleKey = document.body && document.body.dataset.titleI18n;
    if (titleKey) document.title = t(titleKey);

    const langBtn = document.getElementById("lang-toggle");
    if (langBtn) {
        langBtn.textContent = currentLanguage === "en" ? "TR" : "EN";
        // The accessible name starts with the visible text ("TR - Switch language"), as WCAG 2.5.3 asks.
        langBtn.setAttribute("aria-label", `${langBtn.textContent} - ${t("a11y.langToggle")}`);
    }
}

function setLanguage(lang) {
    if (!SUPPORTED_LANGUAGES.includes(lang)) return;
    currentLanguage = lang;
    safeStorage.set("lang", lang);
    translatePage();
    languageListeners.forEach(listener => { try { listener(lang); } catch (err) { console.error(err); } });
}

/* ==========================================================================
   Small DOM helpers
   ========================================================================== */
// h("div", { class: "x", text: "..." , onclick: fn }, child, ...) builds elements without ever parsing HTML.
function h(tag, attrs, ...children) {
    const el = document.createElement(tag);
    if (attrs) {
        for (const [key, value] of Object.entries(attrs)) {
            if (value === null || value === undefined || value === false) continue;
            if (key === "class") el.className = value;
            else if (key === "text") el.textContent = value;
            else if (key === "dataset") Object.assign(el.dataset, value);
            else if (key === "style" && typeof value === "object") Object.assign(el.style, value);
            else if (key.startsWith("on") && typeof value === "function") el.addEventListener(key.slice(2).toLowerCase(), value);
            else el.setAttribute(key, value === true ? "" : String(value));
        }
    }
    for (const child of children.flat()) {
        if (child === null || child === undefined || child === false) continue;
        el.append(child.nodeType ? child : document.createTextNode(String(child)));
    }
    return el;
}

function byId(id) { return document.getElementById(id); }
function clearChildren(el) { if (el) el.replaceChildren(); }

function showMessage(el, text, kind) {
    if (!el) return;
    el.textContent = text;
    el.className = `alert alert-${kind === "success" ? "success" : "danger"}`;
    el.setAttribute("role", kind === "success" ? "status" : "alert");
}

function hideMessage(el) {
    if (!el) return;
    el.textContent = "";
    el.className = "alert hidden";
}

// Runs task() with the button disabled and aria-busy, and re-enables it whatever happens. A second call while busy is
// ignored, so a double click or a double Enter can never send a money-moving request twice.
async function withBusy(button, task) {
    if (!button) return task();
    if (button.dataset.busy === "1") return undefined;
    button.dataset.busy = "1";
    button.disabled = true;
    button.setAttribute("aria-busy", "true");
    try {
        return await task();
    } finally {
        delete button.dataset.busy;
        button.disabled = false;
        button.removeAttribute("aria-busy");
    }
}

function prefersReducedMotion() {
    return !!(window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches);
}

let confettiCannon = null;
function celebrate(options) {
    if (typeof confetti !== "function" || prefersReducedMotion()) return;
    try {
        // The default global instance renders in a blob: web worker, which the CSP does not allow: own instance, main thread.
        if (!confettiCannon) confettiCannon = typeof confetti.create === "function" ? confetti.create(null, { resize: true, useWorker: false }) : confetti;
        confettiCannon({ particleCount: 120, spread: 80, origin: { y: 0.6 }, ...(options || {}) });
    } catch (err) { /* purely cosmetic */ }
}

/* ==========================================================================
   Formatting: one place for money, numbers and dates (Intl, tr-TR / en-US)
   ========================================================================== */
function formatNumber(value, minDigits, maxDigits) {
    const number = Number(value);
    if (!Number.isFinite(number)) return "—";
    return new Intl.NumberFormat(locale(), {
        minimumFractionDigits: minDigits ?? 2,
        maximumFractionDigits: maxDigits ?? minDigits ?? 2
    }).format(number);
}

function formatMoney(amount, currency) {
    const number = Number(amount);
    if (!Number.isFinite(number)) return "—";
    const code = String(currency || "TRY").toUpperCase();
    if (code === "XAU" || code === "XAG") return `${formatNumber(number, 2, 4)} ${t("unit.gram")}`;
    const text = formatNumber(number, 2, 2);
    if (code === "USD") return `$${text}`;
    if (code === "EUR") return `€${text}`;
    return `${text} ${code}`;
}

// The server stores and sends UTC. A timestamp without a "Z" or offset (some database column types drop it) would be read
// as local time by the browser, so it is read as UTC here.
function toDate(value) {
    let input = value;
    if (typeof input === "string" && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?$/.test(input)) input += "Z";
    const date = input instanceof Date ? input : new Date(input);
    return Number.isNaN(date.getTime()) ? null : date;
}

function formatDateTime(value) {
    const date = toDate(value);
    return date ? date.toLocaleString(locale(), { year: "numeric", month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" }) : "-";
}

function formatShortDateTime(value) {
    const date = toDate(value);
    return date ? date.toLocaleString(locale(), { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" }) : "-";
}

function formatTime(value) {
    const date = toDate(value);
    return date ? date.toLocaleTimeString(locale(), { hour: "2-digit", minute: "2-digit" }) : "";
}

// Due dates and statement periods are business dates in Turkey, whatever the time zone of the browser is.
function formatBusinessDate(value) {
    const date = toDate(value);
    return date ? date.toLocaleDateString(locale(), { year: "numeric", month: "short", day: "numeric", timeZone: "Europe/Istanbul" }) : "-";
}

// 48.00% in English, %48,00 in Turkish.
function formatPercent(value) {
    const text = formatNumber(value, 2, 2);
    return currentLanguage === "tr" ? `%${text}` : `${text}%`;
}

// The server writes some descriptions in Turkish (deposit, exchange, account closing, card payments, standing orders).
// The English UI shows them translated; what a customer typed (transfer notes, merchants) is never touched.
const SERVER_DESCRIPTIONS = [
    [/^Hesaba Para Y\u00fckleme$/, "svc.deposit", []],
    [/^(\S+) (\w+) Al\u0131m\u0131 \(Kur: (\S+) TRY\)$/, "svc.buy", ["amount", "asset", "rate"]],
    [/^(\S+) (\w+) Sat\u0131\u015f\u0131 \(Kur: (\S+) TRY\)$/, "svc.sell", ["amount", "asset", "rate"]],
    [/^Hesap Kapatma Bakiye Aktar\u0131m\u0131 \((\w+) -> (\w+)\): (\S+) (\w+)$/, "svc.closeAccount", ["from", "to", "amount", "currency"]],
    [/^Kredi Kart\u0131 Bor\u00e7 \u00d6deme - Kart: \*(\S+)$/, "svc.cardPayment", ["last4"]],
    [/^Kredi Kart\u0131 Otomatik Bor\u00e7 \u00d6deme - K\u0131smi \((.+)\)$/, "svc.cardAutoPartial", ["period"]],
    [/^Kredi Kart\u0131 Otomatik Bor\u00e7 \u00d6deme \((.+)\)$/, "svc.cardAuto", ["period"]],
    [/^Gecikme\/Akdi Faiz Yans\u0131mas\u0131 \((.+)\)$/, "svc.interest", ["period"]],
    [/^Otomatik Talimat: (\w+) \u00d6demesi$/, "svc.standingOrder", ["type"]],
    [/^Market Harcamas\u0131$/, "svc.groceries", []]
];
const TURKISH_MONTHS = ["Ocak", "\u015eubat", "Mart", "Nisan", "May\u0131s", "Haziran", "Temmuz", "A\u011fustos", "Eyl\u00fcl", "Ekim", "Kas\u0131m", "Aral\u0131k"];

// "Ekim 2026" (written by the server) in the language of the page.
function localizePeriodName(name) {
    const match = /^(\S+) (\d{4})$/.exec(String(name ?? ""));
    const month = match ? TURKISH_MONTHS.indexOf(match[1]) : -1;
    if (month < 0) return String(name ?? "");
    return new Date(Date.UTC(Number(match[2]), month, 15)).toLocaleDateString(locale(), { month: "long", year: "numeric", timeZone: "UTC" });
}

function serverDescription(text) {
    const value = String(text ?? "");
    if (currentLanguage === "tr") return value;
    for (const [pattern, key, names] of SERVER_DESCRIPTIONS) {
        const match = pattern.exec(value);
        if (!match) continue;
        const vars = {};
        names.forEach((name, index) => { vars[name] = match[index + 1]; });
        if (vars.period) vars.period = localizePeriodName(vars.period);
        if (vars.type) vars.type = t(vars.type === "CreditCardAutoPay" ? "orders.titleCard" : "orders.titleTransfer");
        return t(key, vars);
    }
    return value;
}

// Account numbers are pasted with spaces and in lower case: the server compares them exactly.
function normalizeAccountNumber(value) {
    return String(value ?? "").replace(/\s+/g, "").toUpperCase();
}

// Display-only upper-casing in the Turkish locale (i -> I with dot). Never used for protocol strings.
function displayUpper(text) {
    return String(text ?? "").toLocaleUpperCase("tr-TR");
}

// The limits the server enforces (Money.MaxAmount / Money.MaxStandingOrderAmount).
const MAX_AMOUNT = 10000000;
const MAX_STANDING_ORDER_AMOUNT = 1000000;

// Parses what a money input holds. Returns { ok: true, value } or { ok: false, errorKey } (positive, at most 2 decimals, at most max).
function parseAmount(raw, max) {
    const text = String(raw ?? "").trim().replace(/\s/g, "");
    if (!text) return { ok: false, errorKey: "InvalidAmount" };
    const normalized = /^\d+,\d+$/.test(text) ? text.replace(",", ".") : text;
    if (!/^\d+(\.\d+)?$/.test(normalized)) return { ok: false, errorKey: "InvalidAmount" };
    const fraction = normalized.split(".")[1] || "";
    if (fraction.length > 2) return { ok: false, errorKey: "InvalidAmountScale" };
    const value = Number(normalized);
    if (!Number.isFinite(value) || value <= 0) return { ok: false, errorKey: "InvalidAmount" };
    if (value > (max || MAX_AMOUNT)) return { ok: false, errorKey: "AmountTooLarge" };
    return { ok: true, value };
}

function errorText(key) {
    return hasKey(`err.${key}`) ? t(`err.${key}`) : t("err.Generic");
}

function stripOtpMarker(message) {
    return String(message ?? "").split("|OTP:")[0];
}

/* ==========================================================================
   Session: a 15-minute access token plus a single-use refresh token (see docs/DEFENSE.md, T12)
   Every authenticated API call goes through the fetch wrapper below: it refreshes the access token shortly before it
   expires, and once (then retries) when the server answers 401. If the refresh token is refused too, the user is signed out.
   ========================================================================== */
const rawFetch = window.fetch.bind(window);
let refreshInFlight = null;
let loggingOut = false;
const REFRESH_MARGIN_MS = 30 * 1000;

function parseStoredUser() {
    try {
        const parsed = JSON.parse(safeStorage.get("user"));
        if (parsed && typeof parsed === "object") {
            if ("tckn" in parsed) {
                // Older versions kept the national id here; nothing needs it.
                delete parsed.tckn;
                safeStorage.set("user", JSON.stringify(parsed));
            }
            return parsed;
        }
    } catch (err) { /* corrupt value: treated as signed out */ }
    return null;
}

let currentUser = parseStoredUser();
let currentToken = safeStorage.get("token") || null;

function buildUser(data) {
    return { id: data.userId, username: data.username, fullName: data.fullName, role: data.role };
}

function saveAuth(token, user, refreshToken, accessTokenExpiresAt) {
    safeStorage.set("token", token);
    safeStorage.set("user", JSON.stringify(user));
    if (refreshToken) safeStorage.set("refreshToken", refreshToken);
    else safeStorage.remove("refreshToken");
    const expiresMs = accessTokenExpiresAt ? Date.parse(accessTokenExpiresAt) : 0;
    if (expiresMs) safeStorage.set("tokenExpiresAt", String(expiresMs));
    else safeStorage.remove("tokenExpiresAt");
    currentToken = token;
    currentUser = user;
}

// Another tab may have refreshed already (the refresh token is single-use): take over what it stored.
function adoptStoredSession() {
    const stored = safeStorage.get("token");
    if (stored && stored !== currentToken) {
        currentToken = stored;
        currentUser = parseStoredUser() || currentUser;
        return true;
    }
    return false;
}

function tokenIsExpiring() {
    const expiresMs = Number(safeStorage.get("tokenExpiresAt")) || 0;
    return expiresMs > 0 && Date.now() > expiresMs - REFRESH_MARGIN_MS;
}

// Resolves to "ok", "denied" (sign in again) or "error" (server or network problem: keep the session and try later).
function refreshSession() {
    if (refreshInFlight) return refreshInFlight;

    refreshInFlight = (async () => {
        const refreshToken = safeStorage.get("refreshToken");
        if (!refreshToken) return "denied";

        try {
            const response = await rawFetch(`${API_URL}/auth/refresh`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ refreshToken })
            });

            if (response.ok) {
                const data = await readJson(response);
                if (!data || !data.token) return "error";
                saveAuth(data.token, currentUser || parseStoredUser(), data.refreshToken, data.accessTokenExpiresAt);
                return "ok";
            }

            if (response.status === 401) {
                // Lost a race with another tab? Then it already stored a newer token.
                await new Promise(resolve => setTimeout(resolve, 500));
                if (safeStorage.get("refreshToken") !== refreshToken && adoptStoredSession()) return "ok";
                return "denied";
            }

            return "error";
        } catch (err) {
            return "error";
        }
    })().finally(() => { refreshInFlight = null; });

    return refreshInFlight;
}

// For callers outside fetch (the SignalR connection asks for a token every time it connects).
async function ensureFreshAccessToken() {
    adoptStoredSession();
    if (tokenIsExpiring()) {
        const outcome = await refreshSession();
        if (outcome === "denied") logout();
    }
    return currentToken;
}

window.fetch = async function (input, init) {
    const url = typeof input === "string" ? input : (input && input.url) || "";
    const headers = new Headers((init && init.headers) || (typeof input !== "string" && input && input.headers) || undefined);

    // Only authenticated calls to our own API: login, register, refresh and the rest stay untouched.
    if (!url.startsWith(API_URL) || !headers.has("Authorization")) {
        return rawFetch(input, init);
    }

    await ensureFreshAccessToken();
    if (!currentToken) {
        return new Response("{}", { status: 401, headers: { "Content-Type": "application/json" } });
    }
    headers.set("Authorization", `Bearer ${currentToken}`);
    let response = await rawFetch(input, { ...init, headers });

    if (response.status === 401) {
        const outcome = await refreshSession();
        if (outcome === "ok") {
            headers.set("Authorization", `Bearer ${currentToken}`);
            response = await rawFetch(input, { ...init, headers });
        } else if (outcome === "denied") {
            logout();
        }
    }

    return response;
};

function clearSession() {
    ["token", "user", "refreshToken", "tokenExpiresAt"].forEach(key => safeStorage.remove(key));
    safeSession.remove("activeChatSessionId");
    currentToken = null;
    currentUser = null;
    resetStores();
}

// Signs out: ends the server session, clears every app key (also the chat session id), stops the SignalR connection and
// leaves the page without a history entry. remote:false is used when another tab already signed out.
function logout(options) {
    if (loggingOut) return;
    loggingOut = true;

    const remote = !options || options.remote !== false;
    const refreshToken = safeStorage.get("refreshToken");
    if (remote && refreshToken) {
        // Tell the server to end the session; keepalive lets the request finish while the page navigates away.
        try {
            rawFetch(`${API_URL}/auth/logout`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ refreshToken }),
                keepalive: true
            }).catch(() => {});
        } catch (err) { /* signing out locally is what matters */ }
    }

    if (typeof stopSignalRConnection === "function") {
        try { stopSignalRConnection(); } catch (err) { /* ignore */ }
    }
    clearSession();
    window.location.replace("index.html");
}

// Signing out in one tab signs out every tab; a different user signing in elsewhere reloads this one.
window.addEventListener("storage", event => {
    if (PAGE === "login") return;
    let localArea = null;
    try { localArea = window.localStorage; } catch (err) { return; }
    if (event.storageArea !== localArea) return;

    if (event.key === null || (event.key === "token" && !event.newValue)) {
        logout({ remote: false });
    } else if (event.key === "token" && event.newValue) {
        const before = currentUser && currentUser.id;
        adoptStoredSession();
        if (currentUser && before && currentUser.id !== before) window.location.reload();
    }
});

// The back button can restore a page from the bfcache with the previous user's data still on screen.
window.addEventListener("pageshow", event => {
    if (!event.persisted) return;
    if (PAGE !== "login" && !safeStorage.get("token")) {
        window.location.replace("index.html");
    } else if (PAGE === "login" && safeStorage.get("token") && parseStoredUser()) {
        redirectByUserRole();
    }
});

function redirectByUserRole() {
    // The role is decided by the server (it is also inside the token); the UI only follows it.
    window.location.replace(currentUser && currentUser.role === "Agent" ? "agent.html" : "dashboard.html");
}

// T.C. Kimlik Numarası check digits (11 digits, first not 0; d10 and d11 follow from the others).
function isValidTckn(value) {
    if (!/^[1-9][0-9]{10}$/.test(String(value))) return false;
    const d = String(value).split("").map(Number);
    const odd = d[0] + d[2] + d[4] + d[6] + d[8];
    const even = d[1] + d[3] + d[5] + d[7];
    if (d[9] !== (((odd * 7 - even) % 10) + 10) % 10) return false;
    return d[10] === (odd + even + d[9]) % 10;
}

/* ==========================================================================
   API helper: never throws, never trusts the body to be JSON.
   Resolves to { ok, status, data, networkError } where data is the parsed body or null.
   ========================================================================== */
async function readJson(response) {
    try {
        const text = await response.text();
        return text ? JSON.parse(text) : null;
    } catch (err) {
        return null;
    }
}

async function api(path, options) {
    const opts = options || {};
    const method = opts.method || "GET";
    const headers = {};
    if (opts.body !== undefined) headers["Content-Type"] = "application/json";
    if (opts.auth !== false) headers["Authorization"] = `Bearer ${currentToken}`;

    let controller = null;
    let timer = null;
    if (opts.timeoutMs && typeof AbortController === "function") {
        controller = new AbortController();
        timer = setTimeout(() => controller.abort(), opts.timeoutMs);
    }

    try {
        const response = await fetch(`${API_URL}${path}`, {
            method,
            headers,
            body: opts.body !== undefined ? JSON.stringify(opts.body) : undefined,
            signal: controller ? controller.signal : undefined
        });
        const data = await readJson(response);
        // Retry-After is only readable when the API exposes it to scripts (CORS); null otherwise.
        const retryAfter = Number(response.headers && response.headers.get ? response.headers.get("Retry-After") : 0) || 0;
        return { ok: response.ok, status: response.status, data, networkError: false, retryAfter };
    } catch (err) {
        const timedOut = !!(controller && controller.signal.aborted);
        return { ok: false, status: 0, data: null, networkError: true, timedOut };
    } finally {
        if (timer) clearTimeout(timer);
    }
}

// The demo API runs on a free host that sleeps when idle: the first call can take up to a minute. Calls to it that a person is
// waiting for get a timeout and, after a few seconds, a hint that the server is waking up.
const AUTH_TIMEOUT_MS = 30000;
const WAKE_HINT_MS = 5000;

async function withWakeHint(task) {
    let hint = null;
    const timer = setTimeout(() => { hint = notify(t("auth.waking"), "info", 20000); }, WAKE_HINT_MS);
    try {
        return await task();
    } finally {
        clearTimeout(timer);
        if (hint) hint.remove();
    }
}

// The text to show for a failed api() result: translated by errorKey, never the raw English server text
// (except ValidationError / model-validation messages, which name the field that is wrong).
function messageFromResponse(res) {
    if (res && res.timedOut) return t("err.Timeout");
    if (!res || res.networkError) return t("err.ConnectionError");
    const data = res.data && typeof res.data === "object" ? res.data : {};

    // A rate limit that says how long to wait is more useful than the generic text of its error key.
    if (res.status === 429 && res.retryAfter > 0) return t("err.TooManyRequestsWait", { seconds: Math.min(res.retryAfter, 600) });

    // Validation texts name the wrong field but the server writes them in English: only the English UI shows them as they are.
    if (data.errors && typeof data.errors === "object") {
        const joined = Object.values(data.errors).flat().map(String).join(" ").trim();
        if (joined) return currentLanguage === "en" ? joined : t("err.ValidationError");
    }
    if (data.errorKey === "ValidationError" && data.message) return currentLanguage === "en" ? stripOtpMarker(data.message) : t("err.ValidationError");
    if (data.errorKey && hasKey(`err.${data.errorKey}`)) return t(`err.${data.errorKey}`);

    if (res.status === 429) return res.retryAfter > 0 ? t("err.TooManyRequestsWait", { seconds: Math.min(res.retryAfter, 600) }) : t("err.TooManyRequests");
    if (res.status === 401) return t("err.SessionExpired");
    if (res.status === 403) return t("err.Forbidden");
    if (res.status >= 500) return t("err.ServerError");
    return t("err.Generic");
}

// "message|OTP:123456": the code is only appended when the server runs in demo mode (Demo:ExposeOtp).
function splitOtpMarker(message) {
    const text = String(message ?? "");
    const index = text.indexOf("|OTP:");
    if (index < 0) return { text, otp: "" };
    return { text: text.slice(0, index), otp: text.slice(index + 5).trim() };
}

/* ==========================================================================
   Shared account / card store: one fetch per change instead of one per widget.
   ========================================================================== */
function makeLoader(path) {
    const loader = {
        data: null,
        listeners: new Set(),
        inflight: null,
        pending: null
    };

    const run = () => {
        loader.inflight = (async () => {
            const res = await api(path);
            if (res.ok && Array.isArray(res.data)) {
                loader.data = res.data;
                loader.listeners.forEach(listener => { try { listener(loader.data); } catch (err) { console.error(err); } });
                return { ok: true, data: loader.data };
            }
            return { ok: false, res };
        })().finally(() => { loader.inflight = null; });
        return loader.inflight;
    };

    // force: a mutation just happened, so a request that is already in flight may carry old data: fetch once more after it.
    loader.refresh = function (force) {
        if (!loader.inflight) return run();
        if (!force) return loader.inflight;
        if (!loader.pending) {
            loader.pending = loader.inflight.then(() => { loader.pending = null; return run(); });
        }
        return loader.pending;
    };
    loader.get = async function () {
        if (loader.data) return loader.data;
        const result = await loader.refresh();
        return result.ok ? result.data : null;
    };
    loader.subscribe = function (listener) { loader.listeners.add(listener); };
    loader.reset = function () { loader.data = null; };
    return loader;
}

const accountsStore = makeLoader("/banking/accounts");
const cardsStore = makeLoader("/banking/credit-cards");

function resetStores() {
    accountsStore.reset();
    cardsStore.reset();
}

/* ==========================================================================
   Modals, dialogs and toasts (accessible: role=dialog, aria-modal, Escape, focus trap, focus restore)
   ========================================================================== */
const modalStack = [];
let dialogSequence = 0;

const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

function focusableIn(root) {
    return Array.from(root.querySelectorAll(FOCUSABLE)).filter(el => !el.closest(".hidden") && el.getClientRects().length > 0);
}

// While a dialog is open everything behind it is inert: no Tab stops, no clicks, hidden from screen readers. Toasts stay usable.
const INERT_EXEMPT = "#toast-region, #mock-sms-toast, .skip-link, script";
function syncModalInert() {
    const top = modalStack[modalStack.length - 1] || null;
    Array.from(document.body.children).forEach(el => {
        if (!top || el === top || el.matches(INERT_EXEMPT)) el.removeAttribute("inert");
        else el.setAttribute("inert", "");
    });
}

function openModal(overlay, options) {
    if (!overlay) return;
    const opts = options || {};
    overlay.classList.remove("hidden");
    overlay._modal = { returnFocus: document.activeElement, onClose: opts.onClose, closeOnBackdrop: opts.closeOnBackdrop !== false };
    if (!overlay._backdropBound) {
        overlay._backdropBound = true;
        overlay.addEventListener("mousedown", event => {
            if (event.target === overlay && overlay._modal && overlay._modal.closeOnBackdrop) closeModal(overlay);
        });
    }
    if (!modalStack.includes(overlay)) modalStack.push(overlay);
    document.body.classList.add("modal-open");
    syncModalInert();

    const content = overlay.querySelector(".modal-content");
    let target = typeof opts.initialFocus === "string" ? overlay.querySelector(opts.initialFocus) : opts.initialFocus;
    if (!target) target = focusableIn(overlay)[0];
    if (!target && content) { content.setAttribute("tabindex", "-1"); target = content; }
    if (target && typeof target.focus === "function") target.focus();
}

function closeModal(overlay, options) {
    if (!overlay) return;
    const state = overlay._modal;
    overlay._modal = null;
    overlay.classList.add("hidden");
    const index = modalStack.indexOf(overlay);
    if (index >= 0) modalStack.splice(index, 1);
    if (!modalStack.length) document.body.classList.remove("modal-open");
    syncModalInert();
    if (!state) return;

    if (state.onClose) state.onClose();
    const restore = !options || options.restoreFocus !== false;
    if (restore) {
        // The element that opened the dialog may be gone (a re-rendered list): focus then falls back to the page's main area.
        const back = state.returnFocus && document.contains(state.returnFocus) && typeof state.returnFocus.focus === "function"
            ? state.returnFocus : document.getElementById("main-content");
        if (back) back.focus();
    }
}

document.addEventListener("keydown", event => {
    const top = modalStack[modalStack.length - 1];
    if (!top) return;

    if (event.key === "Escape") {
        event.preventDefault();
        closeModal(top);
        return;
    }
    if (event.key !== "Tab") return;

    const items = focusableIn(top);
    if (!items.length) { event.preventDefault(); return; }
    const first = items[0];
    const last = items[items.length - 1];
    if (!top.contains(document.activeElement)) {
        event.preventDefault();
        first.focus();
    } else if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
    }
});

// Builds a modal in the page (same markup and styling as the static ones). dialog.open(options) shows it; closing removes it.
function createDialog(config) {
    const titleId = `dialog-title-${++dialogSequence}`;
    const descId = `dialog-desc-${dialogSequence}`;
    const overlay = h("div", {
        class: "modal-overlay hidden", role: "dialog", "aria-modal": "true", "aria-labelledby": titleId,
        "aria-describedby": config.description ? descId : null
    });
    const closeButton = h("button", { type: "button", class: "close-btn", "aria-label": t("common.close") }, h("span", { "aria-hidden": "true" }, "×"));
    const body = h("div", { class: "modal-body modal-body-left" });
    if (config.description) body.append(h("p", { id: descId, class: "dialog-text", text: config.description }));
    const footer = h("div", { class: "dialog-actions" });
    overlay.append(h("div", { class: `modal-content glassmorphism ${config.wide ? "modal-content-md" : "modal-content-sm"}` },
        h("div", { class: "modal-header" }, h("h3", { id: titleId, text: config.title }), closeButton),
        body,
        footer));
    document.body.append(overlay);

    const dialog = {
        overlay, body, footer,
        close() { closeModal(overlay); },
        open(options) {
            const opts = options || {};
            openModal(overlay, {
                initialFocus: opts.initialFocus,
                closeOnBackdrop: opts.closeOnBackdrop,
                onClose: () => {
                    if (opts.onClose) opts.onClose();
                    overlay.remove();
                }
            });
        }
    };
    closeButton.addEventListener("click", () => dialog.close());
    return dialog;
}

function uiConfirm(message, options) {
    const opts = options || {};
    return new Promise(resolve => {
        const dialog = createDialog({ title: opts.title || t("dialog.confirmTitle"), description: message });
        let answer = false;
        const cancel = h("button", { type: "button", class: "btn btn-secondary", text: opts.cancelText || t("common.cancel") });
        const confirmButton = h("button", { type: "button", class: `btn ${opts.danger ? "btn-danger" : "btn-primary"}`, text: opts.confirmText || t("common.confirm") });
        cancel.addEventListener("click", () => dialog.close());
        confirmButton.addEventListener("click", () => { answer = true; dialog.close(); });
        dialog.footer.append(cancel, confirmButton);
        dialog.open({ initialFocus: opts.danger ? cancel : confirmButton, onClose: () => resolve(answer) });
    });
}

// Asks for one text value. Resolves with the value, or null when cancelled. With options.submit (async, returns
// { ok, message }) the dialog stays open and shows the message until the value is accepted.
function uiPrompt(options) {
    return new Promise(resolve => {
        const dialog = createDialog({ title: options.title, description: options.message });
        const inputId = `dialog-input-${dialogSequence}`;
        let answer = null;
        const input = h("input", {
            id: inputId, class: "form-control", type: options.type || "text", value: options.value || "",
            maxlength: options.maxlength || null, inputmode: options.inputmode || null,
            autocomplete: options.autocomplete || "off", pattern: options.pattern || null
        });
        const errorBox = h("div", { class: "alert alert-danger hidden", role: "alert" });
        dialog.body.append(
            h("div", { class: "dialog-field" }, h("label", { for: inputId, text: options.label || options.title }), input),
            errorBox);

        const cancel = h("button", { type: "button", class: "btn btn-secondary", text: t("common.cancel") });
        const confirmButton = h("button", { type: "button", class: "btn btn-primary", text: options.confirmText || t("common.confirm") });
        const submit = () => withBusy(confirmButton, async () => {
            const value = input.value.trim();
            errorBox.className = "alert alert-danger hidden";
            if (options.validate) {
                const problem = options.validate(value);
                if (problem) { errorBox.textContent = problem; errorBox.className = "alert alert-danger"; input.focus(); return; }
            }
            if (options.submit) {
                const result = await options.submit(value);
                if (!result || !result.ok) {
                    errorBox.textContent = (result && result.message) || t("err.Generic");
                    errorBox.className = "alert alert-danger";
                    input.focus();
                    return;
                }
            }
            answer = value;
            dialog.close();
        });
        cancel.addEventListener("click", () => dialog.close());
        confirmButton.addEventListener("click", submit);
        input.addEventListener("keydown", event => { if (event.key === "Enter") { event.preventDefault(); submit(); } });
        dialog.footer.append(cancel, confirmButton);
        // A click beside the box must not throw away a half-typed value (a PIN): only Cancel, the X and Escape close it.
        dialog.open({ initialFocus: input, closeOnBackdrop: false, onClose: () => resolve(answer) });
    });
}

// The toast region exists from the start (a live region that appears together with its first message is often not announced).
function ensureToastRegion() {
    let region = byId("toast-region");
    if (!region) {
        region = h("div", { id: "toast-region", class: "toast-region", "aria-live": "polite", "aria-atomic": "false" });
        document.body.append(region);
    }
    return region;
}

// Non-blocking message (replaces alert()). Announced by screen readers through the live region. Returns the toast element.
function notify(message, kind, timeoutMs) {
    const region = ensureToastRegion();
    const toast = h("div", { class: `toast toast-${kind === "error" ? "error" : kind === "success" ? "success" : "info"}`, role: kind === "error" ? "alert" : "status" },
        h("span", { class: "toast-text", text: message }),
        h("button", { type: "button", class: "toast-close", "aria-label": t("common.close"), onclick: () => toast.remove() }, "×"));
    region.append(toast);
    setTimeout(() => toast.remove(), timeoutMs || (kind === "error" ? 8000 : 5000));
    return toast;
}

// The demo "notification" for one-time codes. Without a code (the normal case) the server e-mailed it.
let codeToastTimers = [];
function showOtpToast(otpCode) {
    const toast = byId("mock-sms-toast");
    const textEl = byId("sms-text");
    if (!toast || !textEl) return;

    byId("sms-app-name").textContent = otpCode ? "SMARTBANK SMS" : "SMARTBANK";
    textEl.textContent = otpCode ? t("toast.codeDemo", { code: otpCode }) : t("toast.codeMailed");

    codeToastTimers.forEach(clearTimeout);
    codeToastTimers = [];
    toast.classList.remove("hidden");
    codeToastTimers.push(setTimeout(() => toast.classList.add("show"), 50));
    codeToastTimers.push(setTimeout(() => {
        toast.classList.remove("show");
        codeToastTimers.push(setTimeout(() => toast.classList.add("hidden"), 500));
    }, 10000));
}

// Roving-tabindex tab list (arrow keys, Home, End). Panels are the elements named by aria-controls.
function initTabList(tablist, onSelect) {
    if (!tablist) return null;
    const tabs = Array.from(tablist.querySelectorAll('[role="tab"]'));

    const select = (tab, focus) => {
        tabs.forEach(other => {
            const on = other === tab;
            other.classList.toggle("active", on);
            other.setAttribute("aria-selected", on ? "true" : "false");
            other.tabIndex = on ? 0 : -1;
            const panel = byId(other.getAttribute("aria-controls"));
            if (panel) panel.classList.toggle("hidden", !on);
        });
        if (focus) tab.focus();
        if (onSelect) onSelect(tab);
    };

    tabs.forEach((tab, index) => {
        tab.addEventListener("click", () => select(tab, false));
        tab.addEventListener("keydown", event => {
            let next = null;
            if (event.key === "ArrowRight") next = tabs[(index + 1) % tabs.length];
            else if (event.key === "ArrowLeft") next = tabs[(index - 1 + tabs.length) % tabs.length];
            else if (event.key === "Home") next = tabs[0];
            else if (event.key === "End") next = tabs[tabs.length - 1];
            if (next) { event.preventDefault(); select(next, true); }
        });
    });
    return { select, tabs };
}


// Localization dictionary. One key per line ("key": "text"), identical key sets in en and tr (a unit test checks it).
// {name} placeholders are filled by t(key, { name: ... }). Server error keys are stored as "err.<ErrorKey>".
const i18n = {
    en: {
        // Common
        "common.close": "Close",
        "common.cancel": "Cancel",
        "common.confirm": "Confirm",
        "common.delete": "Delete",
        "common.ok": "OK",
        "common.send": "Send",
        "common.retry": "Retry",
        "common.loading": "Loading...",
        "common.logout": "Logout",
        "a11y.skip": "Skip to main content",
        "a11y.langToggle": "Switch language",
        "dialog.confirmTitle": "Please confirm",
        "unit.gram": "g",

        // Page titles
        "title.login": "SmartBank - Secure Banking Portal",
        "title.dashboard": "SmartBank - Customer Dashboard",
        "title.agent": "SmartBank - Support Center Dashboard",

        // Form fields and placeholders
        "field.tckn": "T.C. Identity Number",
        "field.password": "Password (6 digits)",
        "field.newPassword": "New Password (6 digits)",
        "field.twofaCode": "Verification code",
        "field.verificationCode": "Verification Code",
        "field.firstName": "First Name",
        "field.lastName": "Last Name",
        "field.username": "Username",
        "field.email": "Email Address",
        "ph.tckn": "11 digits",
        "ph.password": "6-digit password",
        "ph.newPassword": "Create a 6-digit password",
        "ph.code": "6-digit code",
        "ph.emailCode": "6-digit code sent to your e-mail",
        "ph.firstName": "First name",
        "ph.lastName": "Last name",
        "ph.username": "Create a username",
        "ph.email": "name@example.com",

        // Login / register / forgot password
        "login.title": "Sign In",
        "login.subtitle": "Access your financial dashboard",
        "login.submit": "Sign In",
        "login.verify": "Verify & Sign In",
        "login.back": "Use a different account",
        "login.forgot": "Forgot Password?",
        "login.noAccount": "Don't have an account?",
        "login.register": "Register here",
        "login.twofaHint": "Enter the code that was sent to your e-mail address.",
        "login.twofaHintDemo": "Enter the code from the notification at the top of the page (public demo).",
        "login.tcknInvalid": "The T.C. Identity Number must have 11 digits.",
        "login.passwordInvalid": "The password must have 6 digits.",
        "login.codeInvalid": "Please enter the 6-digit verification code.",
        "register.title": "Create Account",
        "register.subtitle": "Register today using your T.C. Identity Number",
        "register.submit": "Register",
        "register.hasAccount": "Already have an account?",
        "register.signIn": "Sign In",
        "register.firstNameInvalid": "The first name can only contain letters, spaces, hyphens and apostrophes.",
        "register.lastNameInvalid": "The last name can only contain letters, spaces, hyphens and apostrophes.",
        "register.usernameRequired": "Please choose a username.",
        "register.emailInvalid": "Please enter a valid e-mail address.",
        "register.tcknInvalid": "The T.C. Identity Number is not valid: its check digits do not match. Please re-check the number.",
        "register.passwordInvalid": "The password must have exactly 6 digits.",
        "forgot.title": "Reset Password",
        "forgot.subtitle": "Reset your password using your T.C. Identity Number",
        "forgot.sendCode": "Send Code",
        "forgot.submit": "Reset Password",
        "forgot.back": "Back to Sign In",
        "forgot.tcknInvalid": "The T.C. Identity Number must have 11 digits.",
        "forgot.codeSent": "If this T.C. Identity Number is registered, a verification code has been sent to its e-mail address.",
        "forgot.success": "Your password was reset. You can sign in now.",
        "toast.now": "just now",
        "toast.codeMailed": "SmartBank: A verification code was sent to your registered e-mail address.",
        "toast.codeDemo": "SmartBank: Your security verification code is {code}. Do not share it.",

        // Market rates
        "market.title": "Live Market Rates",
        "market.live": "LIVE",
        "market.updated": "Last update:",
        "market.offline": "Rates unavailable. Retrying...",
        "market.unavailable": "Market rates could not be loaded.",
        "market.buy": "BUY",
        "market.sell": "SELL",

        // Dashboard shell
        "sidebar.open": "Quick actions",
        "sidebar.title": "Quick Actions",
        "sidebar.addMoneyTitle": "Add Money",
        "sidebar.addMoneyDesc": "Add 1000 TRY to your TRY demand account",
        "sidebar.addMoneyBtn": "Add +1000 TRY",
        "sidebar.added": "1000 TRY was added to your account.",
        "sidebar.noTryAccount": "You have no TRY demand account.",
        "tabs.label": "Sections",
        "tabs.accounts": "Accounts & Transfer",
        "tabs.cards": "Credit Cards & Statements",
        "tabs.orders": "Standing Orders",

        // Accounts
        "accounts.title": "Accounts",
        "accounts.new": "+ New Account",
        "accounts.loading": "Loading account details...",
        "accounts.none": "No active accounts.",
        "accounts.code": "Account code:",
        "accounts.delete": "Close",
        "accounts.deleted": "The account was closed.",
        "accounts.deleteConfirm": "Are you sure you want to permanently close this account?",
        "accounts.deleteNoTarget": "This account has a balance, but you have no other account to transfer it to, so it cannot be closed.",
        "accounts.closeTransferTitle": "Account Closure Balance Transfer",
        "accounts.closeTransferDesc": "The account you want to close has a balance of {balance}. Select the account that receives it:",
        "accounts.targetLabel": "Target account for the balance",
        "accounts.transferAndClose": "Transfer & Close Account",
        "accounts.timeInfo": "{rate} interest | Term: 30 days",
        "acc.titleDemand": "SmartSavings",
        "acc.titleTime": "SmartDeposit (Time)",
        "acc.titleGold": "SmartGold",
        "acc.titleSilver": "SmartSilver",

        // Operations: transfer and exchange
        "ops.label": "Operations",
        "ops.transferTab": "Money Transfer",
        "ops.exchangeTab": "Buy / Sell Currency",
        "transfer.title": "Transfer Funds",
        "transfer.desc": "Send money instantly using the account number",
        "transfer.source": "Source Account",
        "transfer.savedContacts": "Pick a saved recipient (quick fill)",
        "transfer.savedContactsPlaceholder": "-- Select saved recipient --",
        "transfer.manageContacts": "Manage recipients",
        "transfer.dest": "Destination Account Number",
        "transfer.amount": "Amount",
        "transfer.description": "Description",
        "transfer.descriptionPh": "E.g., rent, groceries",
        "transfer.category": "Category",
        "transfer.saveContact": "Add the recipient to my saved contacts",
        "transfer.aliasPh": "Nickname (e.g. Ali Savings)",
        "transfer.aliasLabel": "Nickname",
        "transfer.submit": "Execute Transfer",
        "transfer.success": "Transfer executed successfully!",
        "transfer.noSource": "Please select a source account.",
        "transfer.noDest": "Please enter the destination account number.",
        "cat.other": "Other",
        "cat.market": "Groceries",
        "cat.bills": "Bills",
        "cat.fun": "Entertainment",
        "cat.invest": "Investment",
        "exchange.title": "Currency & Precious Metals",
        "exchange.desc": "Buy or sell currency and gold/silver instantly using your TRY account",
        "exchange.action": "Transaction Type",
        "exchange.buy": "Buy (currency / metal)",
        "exchange.sell": "Sell (currency / metal)",
        "exchange.asset": "Currency / Metal",
        "exchange.usd": "USD - US Dollar",
        "exchange.eur": "EUR - Euro",
        "exchange.xau": "XAU - Gold (gram)",
        "exchange.xag": "XAG - Silver (gram)",
        "exchange.source": "Source Account (payment / proceeds)",
        "exchange.amount": "Asset Amount",
        "exchange.rate": "Rate:",
        "exchange.total": "Total:",
        "exchange.submit": "Complete Transaction",
        "exchange.noTry": "You have no TRY account",
        "exchange.noAsset": "You have no {asset} account to sell from",
        "exchange.noAccount": "Please select a valid account.",
        "exchange.success": "Exchange transaction completed successfully!",

        // History and receipt
        "history.title": "Transaction History",
        "history.desc": "Recent financial movements (click a row to see the receipt)",
        "history.date": "Date",
        "history.type": "Type",
        "history.description": "Description",
        "history.amount": "Amount",
        "history.empty": "Select an account to view history",
        "history.none": "There are no transactions yet.",
        "history.loading": "Loading transactions...",
        "history.showMore": "Show More ({count} more)",
        "history.showLess": "Show Less",
        "history.openReceipt": "Open the receipt",
        "txType.Transfer": "Transfer",
        "txType.Deposit": "Deposit",
        "txType.Withdrawal": "Withdrawal",
        "slip.title": "Transaction Receipt",
        "slip.stamp": "SmartBank A.Ş. Approved",
        "slip.date": "Date:",
        "slip.ref": "Reference No:",
        "slip.type": "Transaction Type:",
        "slip.sender": "Sender:",
        "slip.receiver": "Receiver:",
        "slip.accountNo": "Account No:",
        "slip.amount": "Amount:",
        "slip.description": "Description:",
        "slip.customer": "SmartBank Customer",

        // Contacts
        "contacts.manageTitle": "Manage Saved Contacts",
        "contacts.manageDesc": "You can edit contact nicknames or delete them from your list.",
        "contacts.none": "No saved contacts found.",
        "contacts.editLabel": "Edit {name}",
        "contacts.deleteLabel": "Delete {name}",
        "contacts.editTitle": "Edit nickname",
        "contacts.editMessage": "Enter a new nickname for \"{name}\":",
        "contacts.aliasLabel": "Nickname",
        "contacts.aliasEmpty": "The nickname cannot be empty.",
        "contacts.deleteConfirm": "Are you sure you want to delete this contact?",
        "contacts.defaultAlias": "Saved contact",
        "contacts.saveFailed": "The transfer was made, but the recipient could not be saved.",

        // Card customizer and 2FA
        "customizer.title": "Card & Security Workspace",
        "customizer.desc": "Custom card style & 2FA security",
        "customizer.flip": "Flip the card",
        "customizer.signature": "AUTHORIZED SIGNATURE",
        "customizer.cardInfo": "This card is property of SmartBank. Use is subject to bank rules.",
        "customizer.theme": "Preview theme:",
        "theme.neon": "Neon Blue",
        "theme.sunset": "Sunset Orange",
        "theme.metallic": "Metallic Dark",
        "theme.glass": "Glassmorphism",
        "twofa.title": "2FA Security",
        "twofa.desc": "Requires a verification code for transfers over 1000 TRY",
        "twofa.pinTitle": "Confirm with your password",
        "twofa.pinEnable": "Enter your 6-digit password to turn two-factor verification on.",
        "twofa.pinDisable": "Enter your 6-digit password to turn two-factor verification off.",
        "twofa.pinLabel": "Password (6 digits)",
        "twofa.pinInvalid": "The password must have 6 digits.",
        "twofa.enabled": "Two-factor verification is on.",
        "twofa.disabled": "Two-factor verification is off.",

        // Credit cards
        "cards.title": "Credit Cards",
        "cards.apply": "+ New Application",
        "cards.applyNow": "Apply Now",
        "cards.applyConfirm": "Do you confirm the credit card application?",
        "cards.applyDone": "Your credit card was created!",
        "cards.loading": "Loading credit card details...",
        "cards.none": "You have no active credit cards.",
        "cards.emptyTitle": "No Credit Card Found",
        "cards.emptyDesc": "Apply now to split your payments and enjoy SmartCredit benefits.",
        "cards.selectPrompt": "Please select a credit card to view its details and statement.",
        "cards.panelLabel": "Credit card details",
        "cards.limitShort": "Limit",
        "cards.availableShort": "Available",
        "cards.viewStatement": "View Statement",
        "cards.detailsTitle": "SmartCredit Card Details",
        "cards.limit": "Total Limit",
        "cards.available": "Available Limit",
        "cards.debt": "Current Debt",
        "cards.stmtTitle": "Account Summary (Statement)",
        "cards.period": "Period",
        "cards.periodDebt": "Period Debt",
        "cards.minPayment": "Minimum Payment",
        "cards.dueDate": "Due Date",
        "cards.periodTx": "Period Transactions",
        "cards.txAmount": "Amount",
        "cards.noSpend": "No spending yet.",
        "cards.noStatement": "There is no statement for this card yet.",
        "cards.statusPaid": "Fully Paid",
        "cards.statusUnpaid": "Unpaid (minimum due: {min})",
        "cards.statusMinPaid": "Minimum paid (remaining debt: {remaining})",
        "cards.payTitle": "Pay Debt",
        "cards.paySource": "Payment account",
        "cards.payAmount": "Amount to pay",
        "cards.payAmountPh": "Amount",
        "cards.pay": "Pay",
        "cards.payMin": "Pay Minimum",
        "cards.payFull": "Pay in Full",
        "cards.paySuccess": "Payment completed successfully!",
        "cards.noTryAccount": "You have no TRY demand account",
        "cards.advanceTitle": "Advance Period",
        "cards.advanceDesc": "Simulates the due date: interest is applied to unpaid debt and a new statement is issued.",
        "cards.advanceBtn": "Close Period (Apply Interest)",
        "cards.advanceConfirm": "This simulates the due date: interest is applied to the unpaid debt and a new statement is issued. Continue?",
        "cards.advanceDone": "The billing period was advanced and interest was calculated.",
        "cards.chargeTitle": "Simulate Spending",
        "cards.chargeMerchant": "Merchant",
        "cards.chargeMerchantPh": "Merchant (e.g. Starbucks)",
        "cards.chargeAmount": "Amount",
        "cards.chargeAmountPh": "Amount",
        "cards.chargeBtn": "Spend",
        "cards.chargeNoMerchant": "Please enter the merchant name.",
        "cards.chargeSuccess": "Transaction approved!",

        // New account dialog
        "newacc.title": "Open New Account",
        "newacc.selectType": "Select Account Type:",
        "newacc.tryName": "TRY Demand Account",
        "newacc.tryDesc": "For daily transactions and transfers (TRY)",
        "newacc.usdName": "USD Demand Account",
        "newacc.usdDesc": "For dollar savings and transfers (USD)",
        "newacc.eurName": "EUR Demand Account",
        "newacc.eurDesc": "For euro savings and transfers (EUR)",
        "newacc.xauName": "Gold (gram) Account",
        "newacc.xauDesc": "For gold investment and savings (XAU)",
        "newacc.xagName": "Silver (gram) Account",
        "newacc.xagDesc": "For silver investment and savings (XAG)",
        "newacc.timeName": "TRY Time Deposit (Tiered Interest)",
        "newacc.timeDesc": "High tiered return over a 30-day term (TRY)",
        "newacc.ratesTitle": "Current Deposit Interest Rates",
        "newacc.range": "Amount Range",
        "newacc.rate": "Interest Rate",
        "newacc.rangeOver": "{from} TRY and above",
        "newacc.calcTitle": "Deposit Profit Calculator (30-day term)",
        "newacc.calcPh": "Enter amount (e.g. 100000)",
        "newacc.calcBtn": "Calculate",
        "newacc.calcRate": "Applied interest rate:",
        "newacc.calcProfit": "Net profit at maturity:",
        "newacc.submit": "Open Account",
        "newacc.created": "Your new account was opened.",

        // Standing orders
        "orders.title": "My Standing Orders",
        "orders.loading": "Loading standing orders...",
        "orders.none": "You have no standing orders.",
        "orders.newTitle": "Create Standing Order",
        "orders.newDesc": "Set up automatic money transfers or automatic credit card payments.",
        "orders.source": "TRY demand account to pay from",
        "orders.type": "Order type",
        "orders.typeTransfer": "Regular money transfer",
        "orders.typeCard": "Automatic credit card payment",
        "orders.dest": "Recipient account number",
        "orders.amount": "Recurring amount",
        "orders.frequency": "Frequency",
        "orders.daily": "Every day",
        "orders.weekly": "Every week",
        "orders.monthly": "Every month",
        "orders.targetCard": "Credit card to pay",
        "orders.submit": "Save Order",
        "orders.created": "The standing order was created.",
        "orders.noSource": "Please select the source account.",
        "orders.noCard": "Please select a credit card.",
        "orders.titleCard": "Automatic statement payment",
        "orders.titleTransfer": "Regular money transfer",
        "orders.descCard": "The credit card statement is paid in full automatically on the due date.",
        "orders.descTransfer": "{freq} transfer of {amount} to {dest}",
        "orders.dailyAdj": "Daily",
        "orders.weeklyAdj": "Weekly",
        "orders.monthlyAdj": "Monthly",
        "orders.sourceLabel": "Source",
        "orders.cancel": "Cancel Order",
        "orders.cancelConfirm": "Do you want to cancel this standing order?",

        // One-time code dialog
        "otp.title": "Security Verification",
        "otp.desc": "Please enter the 6-digit verification code sent to you to approve this transfer.",
        "otp.label": "Verification code",
        "otp.submit": "Confirm Code",
        "otp.invalid": "Please enter the 6-digit code.",

        // Live chat (customer widget and agent panel)
        "chat.toggle": "Live Support",
        "chat.title": "Live Chat Support",
        "chat.welcome": "Welcome! Need help with your accounts, transfers, or card limits?",
        "chat.start": "Start Session",
        "chat.typing": "Typing...",
        "chat.inputLabel": "Your message",
        "chat.inputPlaceholder": "Type a message...",
        "chat.loadingMessages": "Loading messages...",
        "chat.historyFailed": "The chat history could not be loaded.",
        "chat.started": "Chat session started.",
        "chat.sessionClosed": "This session has been closed.",
        "chat.offline": "The chat connection is offline. Trying to reconnect...",
        "chat.reconnecting": "Reconnecting...",
        "chat.sendFailed": "The message could not be sent.",
        "chat.startFailed": "The chat session could not be started.",
        "chat.confirmTitle": "Transfer Confirmation",
        "chat.sourceAccount": "Source Account",
        "chat.destAccount": "Recipient Account",
        "chat.confirm": "Confirm",
        "chat.processing": "Processing...",
        "chat.cancelled": "Cancelled",
        "chat.confirmExpired": "This request is from an earlier conversation and can no longer be confirmed.",
        "chat.confirmInvalid": "The amount in this request is not valid, so it cannot be confirmed.",
        "chat.confirmFailed": "The transfer could not be confirmed. Please try again.",
        "chat.successTitle": "Transfer Successful",
        "chat.successDesc": "{amount} was sent to {dest}.",
        "chat.failedTitle": "Transfer Failed",
        "chat.transferredTitle": "Session Transferred",
        "chat.transferredDesc": "The chat has been transferred to the {dept} department.",
        "hub.tooManyChats": "You have started too many chats. Please try again later.",
        "hub.startFailed": "The support session could not be started.",
        "hub.accessDenied": "You do not have access to this chat session.",
        "hub.empty": "The message cannot be empty.",
        "hub.tooLong": "The message is too long.",
        "hub.tooFast": "You are sending messages too fast. Please wait a moment.",
        "hub.sendFailed": "The message could not be sent.",
        "hub.unauthorized": "You are not authorized to do this.",
        "hub.tooManyTransfers": "Too many transfer attempts. Please wait a moment.",
        "hub.closeFailed": "The session could not be closed.",
        "hub.agentRequired": "A support agent account is required.",
        "hub.generic": "Something went wrong with the chat. Please try again.",

        // Agent panel
        "agent.badge": "Support Center",
        "agent.metricResolved": "Resolved Chats",
        "agent.metricTime": "Avg Response Time",
        "agent.metricCsat": "CSAT Score",
        "agent.metricStatus": "Your Status (display only)",
        "agent.statusActive": "Active",
        "agent.statusBusy": "Busy",
        "agent.statusBreak": "Break",
        "agent.activeChats": "Active Support Chats",
        "agent.refresh": "Refresh",
        "agent.noActive": "No active chats at the moment",
        "agent.loadingChats": "Loading chats...",
        "agent.loadingConversation": "Loading conversation...",
        "agent.userLabel": "User:",
        "agent.sessionId": "Session ID: {id}",
        "agent.selectChat": "Select a Support Session",
        "agent.selectChatDesc": "Click on a chat session on the left sidebar to start helping customers.",
        "agent.closeSession": "Close Session",
        "agent.closeOffline": "The chat connection is offline, so the session could not be closed.",
        "agent.closeFailed": "The session could not be closed.",
        "agent.transferLabel": "Transfer to department",
        "agent.transferTo": "Transfer to...",
        "agent.transferred": "The chat was transferred to {dept}.",
        "agent.deptGeneral": "General Support",
        "agent.deptLoans": "Loans Department",
        "agent.deptCards": "Card Services",
        "agent.deptInvestments": "Investment Advisory",
        "agent.copilotTitle": "AI Co-Pilot Recommendation",
        "agent.copilotLoading": "Generating suggestion...",
        "agent.copilotFailed": "Could not generate a suggestion.",
        "agent.regenerate": "Regenerate suggestion",
        "agent.useSuggestion": "Use Recommendation",
        "agent.inputLabel": "Support message",
        "agent.inputPlaceholder": "Type support message...",

        // New in 1.3.2
        "unit.seconds": "s",
        "auth.waking": "The demo server was asleep and is waking up. This can take up to a minute...",
        "login.resend": "Send a new code",
        "login.resent": "A new code was sent.",
        "register.usernameShort": "The username must have at least 3 characters.",
        "forgot.resendIn": "Send again in {seconds}s",
        "transfer.destInvalid": "The account number must have 10 to 30 characters.",
        "svc.deposit": "Deposit to account",
        "svc.buy": "Bought {amount} {asset} (rate: {rate} TRY)",
        "svc.sell": "Sold {amount} {asset} (rate: {rate} TRY)",
        "svc.closeAccount": "Balance moved when closing an account ({from} -> {to}): {amount} {currency}",
        "svc.cardPayment": "Credit card debt payment - card *{last4}",
        "svc.cardAutoPartial": "Automatic credit card payment - partial ({period})",
        "svc.cardAuto": "Automatic credit card payment ({period})",
        "svc.interest": "Late fee and interest ({period})",
        "svc.standingOrder": "Standing order: {type}",
        "svc.groceries": "Grocery spending",
        "exchange.indicativeHint": "The live rate is not available, so only an indicative price is shown and exchange is paused. Please try again in a moment.",
        "market.indicative": "INDICATIVE",
        "orders.inactive": "Inactive",
        "orders.deleteConfirm": "Remove this inactive standing order from the list?",
        "twofa.loadFailed": "The 2FA status could not be loaded.",
        "twofa.pinWrong": "The password is not correct.",
        "cards.issuedTitle": "Your card is ready",
        "cards.issuedDesc": "Your card was created. The security code (CVV) is shown only now and cannot be displayed again, so please note it down.",
        "cards.cvvLabel": "Security code (CVV)",
        "chat.unread": "New message from support",
        "chat.done": "Done",
        "hub.sessionClosed": "This chat session has been closed.",
        "hub.expired": "Your session expired. Please try again.",
        "agent.chatLabel": "Chat",
        "agent.closeConfirm": "Close this conversation for the customer?",
        "err.AmountTooLarge": "The amount is above the allowed limit.",
        "err.SimulationDisabled": "The simulation tools are switched off on this server.",
        "err.Timeout": "The server did not answer in time. The demo server may be waking up; please try again in a moment.",
        "err.TooManyRequestsWait": "Too many requests. Please wait {seconds} seconds and try again.",

        // Errors (server error keys and client-side problems)
        "err.Generic": "Something went wrong. Please try again.",
        "err.ConnectionError": "Connection to the server failed. Please check your connection and try again.",
        "err.ServerError": "The server had a problem. Please try again in a moment.",
        "err.SessionExpired": "Your session has expired. Please sign in again.",
        "err.Forbidden": "You are not allowed to do this.",
        "err.TooManyRequests": "Too many requests. Please wait a moment and try again.",
        "err.ValidationError": "Please check the entered information.",
        "err.UsernameAlreadyExists": "This username is already taken.",
        "err.TcknAlreadyExists": "This T.C. Identity Number is already registered.",
        "err.EmailAlreadyExists": "This e-mail address is already registered.",
        "err.InvalidCredentials": "Invalid T.C. Identity Number or password.",
        "err.AccountLocked": "Too many failed attempts. Your account is temporarily locked, please try again later.",
        "err.TooManyOtpAttempts": "Too many wrong codes. Please request a new code and try again.",
        "err.InvalidOrExpiredCode": "Invalid or expired verification code.",
        "err.InvalidOtpCode": "Invalid or expired verification code.",
        "err.InvalidRefreshToken": "Your session is no longer valid. Please sign in again.",
        "err.UserNotFound": "The user was not found.",
        "err.PinRequired": "Please enter your 6-digit password.",
        "err.Requires2FA": "Two-factor verification required.",
        "err.SuspectedFraudDuplicate": "Suspicious activity: the same transfer was submitted within 30 seconds.",
        "err.SuspectedFraudHighValue": "Suspicious activity: the transfer amount exceeds the standard limit.",
        "err.InsufficientFunds": "Insufficient funds in the source account.",
        "err.InsufficientLimit": "Insufficient credit card limit.",
        "err.SourceAccountNotFound": "The source account was not found.",
        "err.DestinationAccountNotFound": "The destination account was not found.",
        "err.AccountNotFound": "The account was not found.",
        "err.TargetAccountNotFound": "The target account was not found.",
        "err.TargetAccountRequired": "Please choose an account to transfer the remaining balance to.",
        "err.TryAccountNotFound": "You need a TRY demand account for this.",
        "err.UnauthorizedAccountAccess": "You do not have access to this account.",
        "err.CannotDeleteLastAccount": "Your last account cannot be closed.",
        "err.CannotTransferToSelf": "You cannot transfer money to the same account.",
        "err.CurrencyMismatch": "Transfers between different currencies are not supported.",
        "err.InvalidSourceAccount": "This account cannot be used as the source.",
        "err.InvalidExchangeSource": "This account cannot be used for this exchange.",
        "err.InvalidAmount": "The amount must be greater than zero.",
        "err.InvalidAmountScale": "The amount can have at most 2 decimal places.",
        "err.InvalidCurrency": "This currency is not supported.",
        "err.InvalidAction": "This transaction type is not valid.",
        "err.InvalidOrderType": "This standing order type is not valid.",
        "err.InvalidFrequency": "This frequency is not valid.",
        "err.RateUnavailable": "The exchange rate is not available right now. Please try again in a moment.",
        "err.RateNotFound": "The exchange rate for this currency was not found.",
        "err.TransactionFailed": "The transaction could not be completed.",
        "err.ConcurrentModification": "The data changed while you were working. Please try again.",
        "err.CreditCardNotFound": "The credit card was not found.",
        "err.MaxCreditCardsLimitReached": "You have reached the maximum number of credit cards.",
        "err.PaymentFailed": "The payment failed.",
        "err.ChargeFailed": "The purchase was declined.",
        "err.ContactNotFound": "The saved contact was not found.",
        "err.OrderNotFound": "The standing order was not found.",
        "err.SessionNotFound": "The chat session was not found.",
        "err.AlreadyExists": "This item already exists.",
        "err.InvalidDepartment": "The department name is not valid.",
        "err.InvalidSender": "The message sender is not valid.",
        "err.SessionClosed": "This chat session has been closed.",
        "err.AccountCloseFailed": "The account could not be closed. Please try again.",
        "err.AccountLimitReached": "You have reached the maximum number of accounts.",
        "err.AmountTooSmall": "This amount is too small to be converted.",
        "err.ContactLimitReached": "You have reached the maximum number of saved recipients.",
        "err.InvalidAccountNumber": "The account number is not valid.",
        "err.InvalidAccountType": "The account type is not valid.",
        "err.InvalidAlias": "The recipient name is not valid.",
        "err.InvalidDescription": "The description is not valid (at most 200 characters).",
        "err.PaymentExceedsDebt": "The payment cannot be more than the current card debt.",
        "err.StandingOrderLimitReached": "You have reached the maximum number of active standing orders.",
        "err.StandingOrderNeedsVerification": "This amount needs a verification code, which a standing order cannot ask for. Send it as a normal transfer or use a smaller amount.",
        "err.UnauthorizedSessionAccess": "You do not have access to this chat session."
    },
    tr: {
        // Common
        "common.close": "Kapat",
        "common.cancel": "Vazgeç",
        "common.confirm": "Onayla",
        "common.delete": "Sil",
        "common.ok": "Tamam",
        "common.send": "Gönder",
        "common.retry": "Tekrar Dene",
        "common.loading": "Yükleniyor...",
        "common.logout": "Çıkış Yap",
        "a11y.skip": "Ana içeriğe geç",
        "a11y.langToggle": "Dili değiştir",
        "dialog.confirmTitle": "Lütfen onaylayın",
        "unit.gram": "gr",

        // Page titles
        "title.login": "SmartBank - Güvenli Bankacılık Portalı",
        "title.dashboard": "SmartBank - Müşteri Paneli",
        "title.agent": "SmartBank - Destek Merkezi Paneli",

        // Form fields and placeholders
        "field.tckn": "T.C. Kimlik Numarası",
        "field.password": "Şifre (6 haneli)",
        "field.newPassword": "Yeni Şifre (6 haneli)",
        "field.twofaCode": "Doğrulama kodu",
        "field.verificationCode": "Doğrulama Kodu",
        "field.firstName": "Adı",
        "field.lastName": "Soyadı",
        "field.username": "Kullanıcı Adı",
        "field.email": "E-posta Adresi",
        "ph.tckn": "11 haneli numara",
        "ph.password": "6 haneli şifre",
        "ph.newPassword": "6 haneli bir şifre belirleyin",
        "ph.code": "6 haneli kod",
        "ph.emailCode": "E-postanıza gelen 6 haneli kod",
        "ph.firstName": "Adınız",
        "ph.lastName": "Soyadınız",
        "ph.username": "Bir kullanıcı adı belirleyin",
        "ph.email": "ad@ornek.com",

        // Login / register / forgot password
        "login.title": "Giriş Yap",
        "login.subtitle": "Finansal panelinize erişin",
        "login.submit": "Giriş Yap",
        "login.verify": "Doğrula ve Giriş Yap",
        "login.back": "Başka bir hesapla giriş yap",
        "login.forgot": "Şifremi Unuttum?",
        "login.noAccount": "Hesabınız yok mu?",
        "login.register": "Buradan kaydolun",
        "login.twofaHint": "E-posta adresinize gönderilen kodu girin.",
        "login.twofaHintDemo": "Sayfanın üstündeki bildirimde görünen kodu girin (herkese açık demo).",
        "login.tcknInvalid": "T.C. Kimlik Numarası 11 haneli olmalıdır.",
        "login.passwordInvalid": "Şifre 6 haneli olmalıdır.",
        "login.codeInvalid": "Lütfen 6 haneli doğrulama kodunu girin.",
        "register.title": "Hesap Oluştur",
        "register.subtitle": "T.C. Kimlik numaranız ile hemen kaydolun",
        "register.submit": "Kaydol",
        "register.hasAccount": "Zaten hesabınız var mı?",
        "register.signIn": "Giriş Yap",
        "register.firstNameInvalid": "Ad yalnızca harf, boşluk, tire ve kesme işareti içerebilir.",
        "register.lastNameInvalid": "Soyad yalnızca harf, boşluk, tire ve kesme işareti içerebilir.",
        "register.usernameRequired": "Lütfen bir kullanıcı adı seçin.",
        "register.emailInvalid": "Lütfen geçerli bir e-posta adresi girin.",
        "register.tcknInvalid": "T.C. Kimlik Numarası geçerli değil: kontrol basamakları uyuşmuyor. Lütfen numarayı kontrol edin.",
        "register.passwordInvalid": "Şifre tam olarak 6 haneli olmalıdır.",
        "forgot.title": "Şifreyi Sıfırla",
        "forgot.subtitle": "T.C. Kimlik numaranız ile şifrenizi kolayca sıfırlayın",
        "forgot.sendCode": "Kod Gönder",
        "forgot.submit": "Şifreyi Sıfırla",
        "forgot.back": "Giriş Ekranına Dön",
        "forgot.tcknInvalid": "T.C. Kimlik Numarası 11 haneli olmalıdır.",
        "forgot.codeSent": "Bu T.C. Kimlik Numarası kayıtlıysa, e-posta adresine bir doğrulama kodu gönderildi.",
        "forgot.success": "Şifreniz sıfırlandı. Şimdi giriş yapabilirsiniz.",
        "toast.now": "şimdi",
        "toast.codeMailed": "SmartBank: Doğrulama kodu kayıtlı e-posta adresinize gönderildi.",
        "toast.codeDemo": "SmartBank: Güvenlik doğrulama kodunuz {code}. Bu kodu kimseyle paylaşmayın.",

        // Market rates
        "market.title": "Canlı Piyasalar",
        "market.live": "CANLI",
        "market.updated": "Son güncelleme:",
        "market.offline": "Kurlar alınamıyor. Yeniden deneniyor...",
        "market.unavailable": "Piyasa kurları yüklenemedi.",
        "market.buy": "ALIŞ",
        "market.sell": "SATIŞ",

        // Dashboard shell
        "sidebar.open": "Hızlı işlemler",
        "sidebar.title": "Hızlı İşlemler",
        "sidebar.addMoneyTitle": "Hesaba Para Yükle",
        "sidebar.addMoneyDesc": "Vadesiz TL hesabınıza 1000 TL yükleyin",
        "sidebar.addMoneyBtn": "+1000 TL Ekle",
        "sidebar.added": "Hesabınıza 1000 TL eklendi.",
        "sidebar.noTryAccount": "Vadesiz TL hesabınız bulunmuyor.",
        "tabs.label": "Bölümler",
        "tabs.accounts": "Hesaplarım & Transfer",
        "tabs.cards": "Kredi Kartlarım & Ekstre",
        "tabs.orders": "Talimatlarım",

        // Accounts
        "accounts.title": "Hesaplarım",
        "accounts.new": "+ Yeni Hesap Aç",
        "accounts.loading": "Hesap bilgileri yükleniyor...",
        "accounts.none": "Aktif hesabınız bulunmuyor.",
        "accounts.code": "Hesap Kodu:",
        "accounts.delete": "Kapat",
        "accounts.deleted": "Hesap kapatıldı.",
        "accounts.deleteConfirm": "Bu hesabı kalıcı olarak kapatmak istediğinize emin misiniz?",
        "accounts.deleteNoTarget": "Hesapta bakiye var ve aktarabileceğiniz başka bir hesabınız yok. Hesap kapatılamaz.",
        "accounts.closeTransferTitle": "Hesap Kapatma Bakiye Aktarımı",
        "accounts.closeTransferDesc": "Kapatmak istediğiniz hesapta {balance} bakiye bulunmaktadır. Bakiyenin aktarılacağı hesabı seçin:",
        "accounts.targetLabel": "Bakiyenin aktarılacağı hesap",
        "accounts.transferAndClose": "Aktar ve Hesabı Kapat",
        "accounts.timeInfo": "{rate} faiz | Vade: 30 gün",
        "acc.titleDemand": "Vadesiz Hesap",
        "acc.titleTime": "SmartDeposit (Vadeli)",
        "acc.titleGold": "SmartGold (Altın)",
        "acc.titleSilver": "SmartSilver (Gümüş)",

        // Operations: transfer and exchange
        "ops.label": "İşlemler",
        "ops.transferTab": "Para Transferi",
        "ops.exchangeTab": "Döviz Al/Sat",
        "transfer.title": "Para Gönder",
        "transfer.desc": "Hesap numarasını kullanarak anında para transferi yapın",
        "transfer.source": "Kaynak Hesap",
        "transfer.savedContacts": "Kayıtlı alıcı seç (hızlı doldur)",
        "transfer.savedContactsPlaceholder": "-- Kayıtlı alıcı seç --",
        "transfer.manageContacts": "Alıcıları yönet",
        "transfer.dest": "Alıcı Hesap Numarası",
        "transfer.amount": "Tutar",
        "transfer.description": "Açıklama",
        "transfer.descriptionPh": "Örn. kira, market",
        "transfer.category": "Kategori",
        "transfer.saveContact": "Alıcıyı kayıtlı kişilerime ekle",
        "transfer.aliasPh": "Rumuz (örn. Ali Enpara)",
        "transfer.aliasLabel": "Rumuz",
        "transfer.submit": "Transferi Gerçekleştir",
        "transfer.success": "Para transferi başarıyla gerçekleştirildi!",
        "transfer.noSource": "Lütfen bir kaynak hesap seçin.",
        "transfer.noDest": "Lütfen alıcı hesap numarasını girin.",
        "cat.other": "Diğer",
        "cat.market": "Market",
        "cat.bills": "Fatura",
        "cat.fun": "Eğlence",
        "cat.invest": "Yatırım",
        "exchange.title": "Döviz & Değerli Maden İşlemleri",
        "exchange.desc": "TRY hesabınızı kullanarak anında döviz veya altın/gümüş alıp satın",
        "exchange.action": "İşlem Türü",
        "exchange.buy": "Alış (döviz / maden al)",
        "exchange.sell": "Satış (döviz / maden sat)",
        "exchange.asset": "Döviz / Maden Cinsi",
        "exchange.usd": "USD - Amerikan Doları",
        "exchange.eur": "EUR - Euro",
        "exchange.xau": "XAU - Altın (gram)",
        "exchange.xag": "XAG - Gümüş (gram)",
        "exchange.source": "Kaynak Hesap (ödeme / tahsilat)",
        "exchange.amount": "Miktar",
        "exchange.rate": "İşlem Kuru:",
        "exchange.total": "Toplam Karşılık:",
        "exchange.submit": "İşlemi Tamamla",
        "exchange.noTry": "TRY hesabınız bulunmuyor",
        "exchange.noAsset": "Satış yapabileceğiniz bir {asset} hesabınız yok",
        "exchange.noAccount": "Lütfen geçerli bir hesap seçin.",
        "exchange.success": "Döviz/Maden işlemi başarıyla gerçekleştirildi!",

        // History and receipt
        "history.title": "Hesap Hareketleri",
        "history.desc": "Son finansal işlemleriniz (dekont görmek için satıra tıklayın)",
        "history.date": "Tarih",
        "history.type": "Tür",
        "history.description": "Açıklama",
        "history.amount": "Tutar",
        "history.empty": "İşlem geçmişini görüntülemek için bir hesap seçin",
        "history.none": "Henüz işlem bulunmuyor.",
        "history.loading": "İşlemler yükleniyor...",
        "history.showMore": "Daha Fazla Göster ({count} işlem daha)",
        "history.showLess": "Daha Az Göster",
        "history.openReceipt": "Dekontu aç",
        "txType.Transfer": "Transfer",
        "txType.Deposit": "Para Yatırma",
        "txType.Withdrawal": "Para Çekme",
        "slip.title": "İşlem Sonucu Dekontu",
        "slip.stamp": "SmartBank A.Ş. Onaylıdır",
        "slip.date": "İşlem Tarihi:",
        "slip.ref": "Referans No:",
        "slip.type": "İşlem Türü:",
        "slip.sender": "Gönderen:",
        "slip.receiver": "Alıcı:",
        "slip.accountNo": "Hesap No:",
        "slip.amount": "Tutar:",
        "slip.description": "Açıklama:",
        "slip.customer": "SmartBank Müşterisi",

        // Contacts
        "contacts.manageTitle": "Kayıtlı Alıcıları Yönet",
        "contacts.manageDesc": "Kayıtlı alıcılarınızın rumuzlarını düzenleyebilir veya listeden silebilirsiniz.",
        "contacts.none": "Kayıtlı alıcı bulunamadı.",
        "contacts.editLabel": "{name} alıcısını düzenle",
        "contacts.deleteLabel": "{name} alıcısını sil",
        "contacts.editTitle": "Rumuzu düzenle",
        "contacts.editMessage": "\"{name}\" alıcısı için yeni bir rumuz girin:",
        "contacts.aliasLabel": "Rumuz",
        "contacts.aliasEmpty": "Rumuz boş bırakılamaz.",
        "contacts.deleteConfirm": "Bu alıcıyı kayıtlı kişilerden silmek istediğinize emin misiniz?",
        "contacts.defaultAlias": "Kayıtlı Alıcı",
        "contacts.saveFailed": "Transfer yapıldı ancak alıcı kaydedilemedi.",

        // Card customizer and 2FA
        "customizer.title": "Kart ve Güvenlik Paneli",
        "customizer.desc": "Kart stili ve 2FA güvenlik ayarı",
        "customizer.flip": "Kartı çevir",
        "customizer.signature": "YETKİLİ İMZA",
        "customizer.cardInfo": "Bu kart SmartBank'ın mülkiyetindedir. Kullanımı banka kurallarına tabidir.",
        "customizer.theme": "Önizleme teması:",
        "theme.neon": "Neon Mavi",
        "theme.sunset": "Gün Batımı Turuncusu",
        "theme.metallic": "Metalik Koyu",
        "theme.glass": "Cam Efekti",
        "twofa.title": "2FA Güvenliği",
        "twofa.desc": "1000 TRY üzerindeki transferler için doğrulama kodu ister",
        "twofa.pinTitle": "Şifrenizle onaylayın",
        "twofa.pinEnable": "İki aşamalı doğrulamayı açmak için 6 haneli şifrenizi girin.",
        "twofa.pinDisable": "İki aşamalı doğrulamayı kapatmak için 6 haneli şifrenizi girin.",
        "twofa.pinLabel": "Şifre (6 haneli)",
        "twofa.pinInvalid": "Şifre 6 haneli olmalıdır.",
        "twofa.enabled": "İki aşamalı doğrulama açıldı.",
        "twofa.disabled": "İki aşamalı doğrulama kapatıldı.",

        // Credit cards
        "cards.title": "Kredi Kartlarım",
        "cards.apply": "+ Yeni Başvuru",
        "cards.applyNow": "Hemen Başvur",
        "cards.applyConfirm": "Kredi kartı başvurusunu onaylıyor musunuz?",
        "cards.applyDone": "Kredi kartınız başarıyla oluşturuldu!",
        "cards.loading": "Kredi kartı bilgileri yükleniyor...",
        "cards.none": "Aktif kredi kartınız bulunmuyor.",
        "cards.emptyTitle": "Kredi Kartınız Bulunmuyor",
        "cards.emptyDesc": "Harcamalarınızı taksitlendirmek ve SmartCredit avantajlarından yararlanmak için hemen başvurun.",
        "cards.selectPrompt": "Lütfen detaylarını ve ekstre hareketlerini görmek istediğiniz kredi kartını seçiniz.",
        "cards.panelLabel": "Kredi kartı ayrıntıları",
        "cards.limitShort": "Limit",
        "cards.availableShort": "Kalan",
        "cards.viewStatement": "Ekstre Görüntüle",
        "cards.detailsTitle": "SmartCredit Kart Detayları",
        "cards.limit": "Toplam Limit",
        "cards.available": "Kalan Limit",
        "cards.debt": "Güncel Borç",
        "cards.stmtTitle": "Hesap Özeti (Ekstre)",
        "cards.period": "Dönem",
        "cards.periodDebt": "Dönem Borcu",
        "cards.minPayment": "Asgari Ödeme",
        "cards.dueDate": "Son Ödeme",
        "cards.periodTx": "Dönem İçi Hareketler",
        "cards.txAmount": "Tutar",
        "cards.noSpend": "Henüz harcama bulunmuyor.",
        "cards.noStatement": "Bu kart için henüz ekstre yok.",
        "cards.statusPaid": "Tamamı Ödendi",
        "cards.statusUnpaid": "Ödenmedi (asgari borç: {min})",
        "cards.statusMinPaid": "Asgari ödendi (kalan borç: {remaining})",
        "cards.payTitle": "Borç Ödeme",
        "cards.paySource": "Ödeme yapılacak hesap",
        "cards.payAmount": "Ödenecek tutar",
        "cards.payAmountPh": "Tutar",
        "cards.pay": "Öde",
        "cards.payMin": "Asgari Öde",
        "cards.payFull": "Borç Kapat",
        "cards.paySuccess": "Borç ödeme işlemi başarıyla tamamlandı!",
        "cards.noTryAccount": "Vadesiz TL hesabınız bulunmuyor",
        "cards.advanceTitle": "Dönem Atlat",
        "cards.advanceDesc": "Son ödeme günü simülasyonu. Ödenmeyen borca faiz uygulanıp yeni dönem ekstresi kesilir.",
        "cards.advanceBtn": "Dönemi Kapat (Faiz Uygula)",
        "cards.advanceConfirm": "Bu işlem son ödeme gününü simüle eder: ödenmeyen borca faiz uygulanır ve yeni dönem ekstresi kesilir. Devam edilsin mi?",
        "cards.advanceDone": "Dönem atlatıldı ve faiz hesaplandı.",
        "cards.chargeTitle": "Harcama Simüle Et",
        "cards.chargeMerchant": "İşyeri",
        "cards.chargeMerchantPh": "İşyeri (örn. Starbucks)",
        "cards.chargeAmount": "Tutar",
        "cards.chargeAmountPh": "Tutar",
        "cards.chargeBtn": "Harcama Yap",
        "cards.chargeNoMerchant": "Lütfen işyeri adını girin.",
        "cards.chargeSuccess": "Harcama başarıyla yapıldı!",

        // New account dialog
        "newacc.title": "Yeni Hesap Aç",
        "newacc.selectType": "Hesap Türü Seçiniz:",
        "newacc.tryName": "Vadesiz TL Hesabı",
        "newacc.tryDesc": "Günlük işlemler ve transferler için (TRY)",
        "newacc.usdName": "Vadesiz Dolar Hesabı",
        "newacc.usdDesc": "Dolar birikim ve transferler için (USD)",
        "newacc.eurName": "Vadesiz Euro Hesabı",
        "newacc.eurDesc": "Euro birikim ve transferler için (EUR)",
        "newacc.xauName": "Gram Altın Hesabı",
        "newacc.xauDesc": "Altın yatırımı ve birikimi için (XAU)",
        "newacc.xagName": "Gram Gümüş Hesabı",
        "newacc.xagDesc": "Gümüş yatırımı ve birikimi için (XAG)",
        "newacc.timeName": "Vadeli TL Hesabı (Kademeli Faiz)",
        "newacc.timeDesc": "30 günlük vadede yüksek kademeli getiri (TRY)",
        "newacc.ratesTitle": "Güncel Mevduat Faiz Oranları",
        "newacc.range": "Tutar Aralığı",
        "newacc.rate": "Faiz Oranı",
        "newacc.rangeOver": "{from} TRY ve üzeri",
        "newacc.calcTitle": "Mevduat Kârı Hesaplama (30 günlük vade)",
        "newacc.calcPh": "Tutar girin (örn. 100000)",
        "newacc.calcBtn": "Hesapla",
        "newacc.calcRate": "Uygulanan faiz oranı:",
        "newacc.calcProfit": "Vade sonu net kazanç:",
        "newacc.submit": "Hesap Aç",
        "newacc.created": "Yeni hesabınız açıldı.",

        // Standing orders
        "orders.title": "Mevcut Talimatlarım",
        "orders.loading": "Talimatlar yükleniyor...",
        "orders.none": "Tanımlı talimatınız bulunmuyor.",
        "orders.newTitle": "Yeni Talimat Tanımla",
        "orders.newDesc": "Otomatik para transferleri veya kredi kartı otomatik borç ödemesi kurgulayın.",
        "orders.source": "Ödeme yapılacak vadesiz TL hesabı",
        "orders.type": "Talimat türü",
        "orders.typeTransfer": "Düzenli para transferi",
        "orders.typeCard": "Kredi kartı otomatik borç ödeme",
        "orders.dest": "Alıcı hesap numarası",
        "orders.amount": "Yinelenen tutar",
        "orders.frequency": "Yinelenme sıklığı",
        "orders.daily": "Her gün",
        "orders.weekly": "Her hafta",
        "orders.monthly": "Her ay",
        "orders.targetCard": "Ödenecek kredi kartı",
        "orders.submit": "Talimatı Kaydet",
        "orders.created": "Talimat başarıyla tanımlandı!",
        "orders.noSource": "Lütfen kaynak hesabı seçin.",
        "orders.noCard": "Lütfen bir kredi kartı seçin.",
        "orders.titleCard": "Otomatik ekstre ödeme",
        "orders.titleTransfer": "Düzenli para transferi",
        "orders.descCard": "Kredi kartı ekstresi son ödeme gününde otomatik olarak tamamen ödenir.",
        "orders.descTransfer": "{freq} düzenli transfer: {amount} tutarında, alıcı {dest}",
        "orders.dailyAdj": "Günlük",
        "orders.weeklyAdj": "Haftalık",
        "orders.monthlyAdj": "Aylık",
        "orders.sourceLabel": "Kaynak",
        "orders.cancel": "İptal Et",
        "orders.cancelConfirm": "Bu talimatı iptal etmek istiyor musunuz?",

        // One-time code dialog
        "otp.title": "Güvenlik Doğrulaması",
        "otp.desc": "Bu transferi onaylamak için size gönderilen 6 haneli doğrulama kodunu girin.",
        "otp.label": "Doğrulama kodu",
        "otp.submit": "Kodu Doğrula",
        "otp.invalid": "Lütfen 6 haneli kodu girin.",

        // Live chat (customer widget and agent panel)
        "chat.toggle": "Canlı Destek",
        "chat.title": "Canlı Destek Sohbeti",
        "chat.welcome": "Merhaba! Hesaplarınız, transferleriniz veya kart limitleriniz hakkında yardıma mı ihtiyacınız var?",
        "chat.start": "Sohbeti Başlat",
        "chat.typing": "Yazıyor...",
        "chat.inputLabel": "Mesajınız",
        "chat.inputPlaceholder": "Mesajınızı yazın...",
        "chat.loadingMessages": "Mesajlar yükleniyor...",
        "chat.historyFailed": "Sohbet geçmişi yüklenemedi.",
        "chat.started": "Sohbet oturumu başladı.",
        "chat.sessionClosed": "Görüşme sonlandırılmıştır.",
        "chat.offline": "Sohbet bağlantısı kapalı. Yeniden bağlanılmaya çalışılıyor...",
        "chat.reconnecting": "Yeniden bağlanılıyor...",
        "chat.sendFailed": "Mesaj gönderilemedi.",
        "chat.startFailed": "Sohbet oturumu başlatılamadı.",
        "chat.confirmTitle": "Para Transferi Onayı",
        "chat.sourceAccount": "Kaynak Hesap",
        "chat.destAccount": "Alıcı Hesap",
        "chat.confirm": "Onayla",
        "chat.processing": "İşleniyor...",
        "chat.cancelled": "İptal Edildi",
        "chat.confirmExpired": "Bu istek önceki bir görüşmeden kaldı ve artık onaylanamaz.",
        "chat.confirmInvalid": "Bu istekteki tutar geçerli değil, bu yüzden onaylanamaz.",
        "chat.confirmFailed": "Transfer onaylanamadı. Lütfen tekrar deneyin.",
        "chat.successTitle": "İşlem Başarılı",
        "chat.successDesc": "{amount}, {dest} numaralı hesaba başarıyla gönderildi.",
        "chat.failedTitle": "İşlem Başarısız",
        "chat.transferredTitle": "Oda Transfer Edildi",
        "chat.transferredDesc": "Sohbet başarıyla {dept} birimine aktarıldı.",
        "hub.tooManyChats": "Çok fazla sohbet başlattınız. Lütfen daha sonra tekrar deneyin.",
        "hub.startFailed": "Destek oturumu başlatılamadı.",
        "hub.accessDenied": "Bu destek odasına erişim yetkiniz yok.",
        "hub.empty": "Mesaj boş olamaz.",
        "hub.tooLong": "Mesaj çok uzun.",
        "hub.tooFast": "Çok hızlı mesaj gönderiyorsunuz. Lütfen biraz bekleyin.",
        "hub.sendFailed": "Mesaj gönderilemedi.",
        "hub.unauthorized": "Bu işlem için yetkiniz yok.",
        "hub.tooManyTransfers": "Çok fazla transfer denemesi. Lütfen biraz bekleyin.",
        "hub.closeFailed": "Oturum kapatılamadı.",
        "hub.agentRequired": "Destek temsilcisi hesabı gereklidir.",
        "hub.generic": "Sohbette bir sorun oluştu. Lütfen tekrar deneyin.",

        // Agent panel
        "agent.badge": "Destek Merkezi",
        "agent.metricResolved": "Çözülen Sohbetler",
        "agent.metricTime": "Ort. Yanıt Süresi",
        "agent.metricCsat": "CSAT Skoru",
        "agent.metricStatus": "Durumunuz (yalnızca görünüm)",
        "agent.statusActive": "Aktif",
        "agent.statusBusy": "Meşgul",
        "agent.statusBreak": "Mola",
        "agent.activeChats": "Aktif Destek Talepleri",
        "agent.refresh": "Yenile",
        "agent.noActive": "Şu anda aktif destek talebi bulunmuyor",
        "agent.loadingChats": "Sohbetler yükleniyor...",
        "agent.loadingConversation": "Görüşme yükleniyor...",
        "agent.userLabel": "Kullanıcı:",
        "agent.sessionId": "Oturum No: {id}",
        "agent.selectChat": "Bir Sohbet Odası Seçin",
        "agent.selectChatDesc": "Müşterilere yardımcı olmaya başlamak için sol paneldeki aktif sohbet odalarından birine tıklayın.",
        "agent.closeSession": "Oturumu Kapat",
        "agent.closeOffline": "Sohbet bağlantısı kapalı olduğu için oturum kapatılamadı.",
        "agent.closeFailed": "Oturum kapatılamadı.",
        "agent.transferLabel": "Birime aktar",
        "agent.transferTo": "Aktar...",
        "agent.transferred": "Sohbet {dept} birimine aktarıldı.",
        "agent.deptGeneral": "Genel Destek",
        "agent.deptLoans": "Kredi Departmanı",
        "agent.deptCards": "Kart Hizmetleri",
        "agent.deptInvestments": "Yatırım Danışmanlığı",
        "agent.copilotTitle": "AI Co-Pilot Önerisi",
        "agent.copilotLoading": "Öneri oluşturuluyor...",
        "agent.copilotFailed": "Öneri oluşturulamadı.",
        "agent.regenerate": "Öneriyi yenile",
        "agent.useSuggestion": "Öneriyi Kullan",
        "agent.inputLabel": "Destek mesajı",
        "agent.inputPlaceholder": "Destek mesajı yazın...",

        // New in 1.3.2
        "unit.seconds": "sn",
        "auth.waking": "Demo sunucusu uykudaydı ve uyanıyor. Bu bir dakikaya kadar sürebilir...",
        "login.resend": "Yeni kod gönder",
        "login.resent": "Yeni bir kod gönderildi.",
        "register.usernameShort": "Kullanıcı adı en az 3 karakter olmalıdır.",
        "forgot.resendIn": "{seconds} sn sonra tekrar gönder",
        "transfer.destInvalid": "Hesap numarası 10 ile 30 karakter arasında olmalıdır.",
        "svc.deposit": "Hesaba Para Yükleme",
        "svc.buy": "{amount} {asset} Alımı (Kur: {rate} TRY)",
        "svc.sell": "{amount} {asset} Satışı (Kur: {rate} TRY)",
        "svc.closeAccount": "Hesap Kapatma Bakiye Aktarımı ({from} -> {to}): {amount} {currency}",
        "svc.cardPayment": "Kredi Kartı Borç Ödeme - Kart: *{last4}",
        "svc.cardAutoPartial": "Kredi Kartı Otomatik Borç Ödeme - Kısmi ({period})",
        "svc.cardAuto": "Kredi Kartı Otomatik Borç Ödeme ({period})",
        "svc.interest": "Gecikme/Akdi Faiz Yansıması ({period})",
        "svc.standingOrder": "Otomatik Talimat: {type}",
        "svc.groceries": "Market Harcaması",
        "exchange.indicativeHint": "Canlı kur alınamadığı için yalnızca gösterge fiyat gösteriliyor ve döviz işlemi duraklatıldı. Lütfen biraz sonra tekrar deneyin.",
        "market.indicative": "GÖSTERGE",
        "orders.inactive": "Pasif",
        "orders.deleteConfirm": "Bu pasif talimat listeden kaldırılsın mı?",
        "twofa.loadFailed": "2FA durumu yüklenemedi.",
        "twofa.pinWrong": "Şifre doğru değil.",
        "cards.issuedTitle": "Kartınız hazır",
        "cards.issuedDesc": "Kartınız oluşturuldu. Güvenlik kodu (CVV) yalnızca şimdi gösterilir ve bir daha görüntülenemez, lütfen not alın.",
        "cards.cvvLabel": "Güvenlik kodu (CVV)",
        "chat.unread": "Destekten yeni mesaj",
        "chat.done": "Tamamlandı",
        "hub.sessionClosed": "Bu sohbet oturumu kapatıldı.",
        "hub.expired": "Oturumunuz sona erdi. Lütfen tekrar deneyin.",
        "agent.chatLabel": "Sohbet",
        "agent.closeConfirm": "Bu görüşme müşteri için kapatılsın mı?",
        "err.AmountTooLarge": "Tutar izin verilen sınırın üzerinde.",
        "err.SimulationDisabled": "Bu sunucuda simülasyon araçları kapalı.",
        "err.Timeout": "Sunucu zamanında yanıt vermedi. Demo sunucusu uyanıyor olabilir; lütfen biraz sonra tekrar deneyin.",
        "err.TooManyRequestsWait": "Çok fazla istek. Lütfen {seconds} saniye bekleyip tekrar deneyin.",

        // Errors (server error keys and client-side problems)
        "err.Generic": "Bir sorun oluştu. Lütfen tekrar deneyin.",
        "err.ConnectionError": "Sunucuya bağlanılamadı. Lütfen bağlantınızı kontrol edip tekrar deneyin.",
        "err.ServerError": "Sunucuda bir sorun oluştu. Lütfen biraz sonra tekrar deneyin.",
        "err.SessionExpired": "Oturumunuzun süresi doldu. Lütfen tekrar giriş yapın.",
        "err.Forbidden": "Bu işlemi yapmaya yetkiniz yok.",
        "err.TooManyRequests": "Çok fazla istek gönderildi. Lütfen biraz bekleyip tekrar deneyin.",
        "err.ValidationError": "Lütfen girdiğiniz bilgileri kontrol edin.",
        "err.UsernameAlreadyExists": "Bu kullanıcı adı zaten alınmış.",
        "err.TcknAlreadyExists": "Bu T.C. Kimlik Numarası zaten kayıtlı.",
        "err.EmailAlreadyExists": "Bu e-posta adresi zaten kayıtlı.",
        "err.InvalidCredentials": "Hatalı T.C. Kimlik Numarası veya şifre.",
        "err.AccountLocked": "Çok fazla başarısız deneme. Hesabınız geçici olarak kilitlendi, lütfen daha sonra tekrar deneyin.",
        "err.TooManyOtpAttempts": "Çok fazla hatalı kod girdiniz. Lütfen yeni bir kod isteyip tekrar deneyin.",
        "err.InvalidOrExpiredCode": "Geçersiz veya süresi dolmuş doğrulama kodu.",
        "err.InvalidOtpCode": "Geçersiz veya süresi dolmuş doğrulama kodu.",
        "err.InvalidRefreshToken": "Oturumunuz artık geçerli değil. Lütfen tekrar giriş yapın.",
        "err.UserNotFound": "Kullanıcı bulunamadı.",
        "err.PinRequired": "Lütfen 6 haneli şifrenizi girin.",
        "err.Requires2FA": "İki aşamalı güvenlik doğrulaması gerekiyor.",
        "err.SuspectedFraudDuplicate": "Şüpheli işlem: 30 saniye içinde aynı transfer tekrar gönderildi.",
        "err.SuspectedFraudHighValue": "Şüpheli işlem: transfer tutarı standart limitleri aşıyor.",
        "err.InsufficientFunds": "Gönderen hesapta yetersiz bakiye.",
        "err.InsufficientLimit": "Kredi kartı limiti yetersiz.",
        "err.SourceAccountNotFound": "Kaynak hesap bulunamadı.",
        "err.DestinationAccountNotFound": "Alıcı hesap bulunamadı.",
        "err.AccountNotFound": "Hesap bulunamadı.",
        "err.TargetAccountNotFound": "Hedef hesap bulunamadı.",
        "err.TargetAccountRequired": "Lütfen kalan bakiyenin aktarılacağı bir hesap seçin.",
        "err.TryAccountNotFound": "Bu işlem için vadesiz bir TL hesabınız olmalı.",
        "err.UnauthorizedAccountAccess": "Bu hesaba erişim yetkiniz yok.",
        "err.CannotDeleteLastAccount": "Son hesabınız kapatılamaz.",
        "err.CannotTransferToSelf": "Kendi hesabınıza para transferi yapamazsınız.",
        "err.CurrencyMismatch": "Farklı para birimleri arasında transfer desteklenmiyor.",
        "err.InvalidSourceAccount": "Bu hesap kaynak olarak kullanılamaz.",
        "err.InvalidExchangeSource": "Bu hesap bu döviz işlemi için kullanılamaz.",
        "err.InvalidAmount": "Tutar sıfırdan büyük olmalıdır.",
        "err.InvalidAmountScale": "Tutar en fazla 2 ondalık basamak içerebilir.",
        "err.InvalidCurrency": "Bu para birimi desteklenmiyor.",
        "err.InvalidAction": "Bu işlem türü geçerli değil.",
        "err.InvalidOrderType": "Bu talimat türü geçerli değil.",
        "err.InvalidFrequency": "Bu sıklık geçerli değil.",
        "err.RateUnavailable": "Döviz kuru şu anda alınamıyor. Lütfen biraz sonra tekrar deneyin.",
        "err.RateNotFound": "Bu para birimi için kur bulunamadı.",
        "err.TransactionFailed": "İşlem tamamlanamadı.",
        "err.ConcurrentModification": "Siz işlem yaparken veriler değişti. Lütfen tekrar deneyin.",
        "err.CreditCardNotFound": "Kredi kartı bulunamadı.",
        "err.MaxCreditCardsLimitReached": "Azami kredi kartı sayısına ulaştınız.",
        "err.PaymentFailed": "Ödeme işlemi başarısız oldu.",
        "err.ChargeFailed": "Harcama reddedildi.",
        "err.ContactNotFound": "Kayıtlı alıcı bulunamadı.",
        "err.OrderNotFound": "Talimat bulunamadı.",
        "err.SessionNotFound": "Sohbet oturumu bulunamadı.",
        "err.AlreadyExists": "Bu kayıt zaten mevcut.",
        "err.InvalidDepartment": "Birim adı geçerli değil.",
        "err.InvalidSender": "Mesaj göndericisi geçerli değil.",
        "err.SessionClosed": "Bu sohbet oturumu kapatıldı.",
        "err.AccountCloseFailed": "Hesap kapatılamadı. Lütfen tekrar deneyin.",
        "err.AccountLimitReached": "En fazla hesap sayısına ulaştınız.",
        "err.AmountTooSmall": "Bu tutar çevrilemeyecek kadar küçük.",
        "err.ContactLimitReached": "Kayıtlı alıcı sınırına ulaştınız.",
        "err.InvalidAccountNumber": "Hesap numarası geçerli değil.",
        "err.InvalidAccountType": "Hesap türü geçerli değil.",
        "err.InvalidAlias": "Alıcı adı geçerli değil.",
        "err.InvalidDescription": "Açıklama geçerli değil (en fazla 200 karakter).",
        "err.PaymentExceedsDebt": "Ödeme, mevcut kart borcundan fazla olamaz.",
        "err.StandingOrderLimitReached": "Etkin düzenli talimat sınırına ulaştınız.",
        "err.StandingOrderNeedsVerification": "Bu tutar için doğrulama kodu gerekir, düzenli talimat ise kod soramaz. Normal transfer olarak gönderin ya da daha küçük bir tutar girin.",
        "err.UnauthorizedSessionAccess": "Bu destek odasına erişim yetkiniz yok."
    }
};



// Request counters: a loader remembers the number of its latest request and drops answers that arrive out of order.
const requestSeq = { transactions: 0, statements: 0, orders: 0, sessions: 0, agentChat: 0, copilot: 0 };

// Keeps a numeric input digits-only (T.C. numbers, PINs, one-time codes).
function digitsOnly(input) {
    if (!input) return;
    input.addEventListener("input", () => {
        const cleaned = input.value.replace(/\D/g, "");
        if (cleaned !== input.value) input.value = cleaned;
    });
}

// <a href="#" role="button"> that also answers to the Space key, like a real button.
function bindLink(link, handler) {
    if (!link) return;
    link.addEventListener("click", event => { event.preventDefault(); handler(event); });
    link.addEventListener("keydown", event => {
        if (event.key === " ") { event.preventDefault(); handler(event); }
    });
}

/* ==========================================================================
   AUTHENTICATION LOGIC (index.html)
   ========================================================================== */
const NAME_PATTERN = /^[\p{L}\p{M}]+(?:[ '’.-]+[\p{L}\p{M}]+)*\.?$/u;
const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

function initAuthEvents() {
    const cards = ["login-card", "register-card", "forgot-card"].map(byId);
    const showCard = (card) => {
        cards.forEach(other => other.classList.toggle("hidden", other !== card));
        ["login-error", "register-error", "forgot-error", "forgot-success"].forEach(id => hideMessage(byId(id)));
        const first = card.querySelector("input:not([readonly])");
        if (first) first.focus();
    };

    ["login-tckn", "login-password", "login-2fa-code", "reg-tckn", "reg-password", "forgot-tckn", "forgot-code", "forgot-new-password"]
        .forEach(id => digitsOnly(byId(id)));

    bindLink(byId("link-to-register"), () => showCard(byId("register-card")));
    bindLink(byId("link-to-login"), () => showCard(byId("login-card")));
    bindLink(byId("link-to-forgot"), () => {
        ["forgot-tckn", "forgot-code", "forgot-new-password"].forEach(id => { byId(id).value = ""; });
        showCard(byId("forgot-card"));
    });
    bindLink(byId("link-forgot-to-login"), () => showCard(byId("login-card")));

    initLoginForm();
    initRegisterForm();
    initForgotForm();
}

function initLoginForm() {
    const form = byId("login-form");
    const submit = byId("btn-login-submit");
    const resendButton = byId("btn-login-resend");
    const errorDiv = byId("login-error");
    const state = { step: "credentials", demo: false };

    const applyStep = (focus) => {
        const is2fa = state.step === "2fa";
        byId("login-2fa-group").classList.toggle("hidden", !is2fa);
        byId("btn-login-back").classList.toggle("hidden", !is2fa);
        resendButton.classList.toggle("hidden", !is2fa);
        byId("login-tckn").readOnly = is2fa;
        byId("login-password").readOnly = is2fa;
        submit.textContent = t(is2fa ? "login.verify" : "login.submit");
        byId("login-2fa-hint").textContent = t(state.demo ? "login.twofaHintDemo" : "login.twofaHint");
        if (is2fa && focus) byId("login-2fa-code").focus();
    };
    onLanguageChange(() => applyStep(false));

    byId("btn-login-back").addEventListener("click", () => {
        state.step = "credentials";
        state.demo = false;
        byId("login-2fa-code").value = "";
        byId("login-password").value = "";
        hideMessage(errorDiv);
        applyStep(false);
        byId("login-password").focus();
    });

    const fail = (text, focusId) => {
        showMessage(errorDiv, text, "error");
        if (focusId) byId(focusId).focus();
    };

    const finishLogin = (data) => {
        if (!data || !data.token) { fail(t("err.Generic")); return; }
        saveAuth(data.token, buildUser(data), data.refreshToken, data.accessTokenExpiresAt);
        redirectByUserRole();
    };

    // First step (and "send a new code"): the credentials go to the server, which either signs in or asks for a code.
    const signIn = async (resend) => {
        hideMessage(errorDiv);
        const tckn = byId("login-tckn").value.trim();
        const password = byId("login-password").value;

        if (!/^\d{11}$/.test(tckn)) { fail(t("login.tcknInvalid"), "login-tckn"); return; }
        if (!/^\d{6}$/.test(password)) { fail(t("login.passwordInvalid"), "login-password"); return; }

        const res = await withWakeHint(() => api("/auth/login", { method: "POST", auth: false, timeoutMs: AUTH_TIMEOUT_MS, body: { tckn, password } }));
        if (!res.ok) {
            if (res.data && res.data.errorKey === "Requires2FA") {
                // The server only appends "|OTP:code" in demo mode (Demo:ExposeOtp). Normally the code is e-mailed.
                const { otp } = splitOtpMarker(res.data.message);
                state.demo = !!otp;
                state.step = "2fa";
                showOtpToast(otp);
                applyStep(!resend);
                if (resend) notify(t("login.resent"), "success");
                return;
            }
            fail(messageFromResponse(res));
            return;
        }
        finishLogin(res.data);
    };

    form.addEventListener("submit", event => {
        event.preventDefault();
        withBusy(submit, async () => {
            if (state.step !== "2fa") { await signIn(false); return; }

            hideMessage(errorDiv);
            const tckn = byId("login-tckn").value.trim();
            const code = byId("login-2fa-code").value.trim();
            if (!/^\d{6}$/.test(code)) { fail(t("login.codeInvalid"), "login-2fa-code"); return; }

            const res = await withWakeHint(() => api("/auth/verify-2fa", { method: "POST", auth: false, timeoutMs: AUTH_TIMEOUT_MS, body: { tckn, code } }));
            if (!res.ok) { fail(messageFromResponse(res), "login-2fa-code"); return; }
            finishLogin(res.data);
        });
    });

    resendButton.addEventListener("click", () => withBusy(resendButton, () => signIn(true)));
}

function initRegisterForm() {
    const form = byId("register-form");
    const submit = byId("btn-register-submit");
    const errorDiv = byId("register-error");

    form.addEventListener("submit", event => {
        event.preventDefault();
        withBusy(submit, async () => {
            hideMessage(errorDiv);
            const firstName = byId("reg-firstname").value.trim();
            const lastName = byId("reg-lastname").value.trim();
            const username = byId("reg-username").value.trim();
            const email = byId("reg-email").value.trim();
            const tckn = byId("reg-tckn").value.trim();
            const password = byId("reg-password").value;

            const fail = (text, focusId) => { showMessage(errorDiv, text, "error"); byId(focusId).focus(); };

            if (!NAME_PATTERN.test(firstName)) { fail(t("register.firstNameInvalid"), "reg-firstname"); return; }
            if (!NAME_PATTERN.test(lastName)) { fail(t("register.lastNameInvalid"), "reg-lastname"); return; }
            if (!username) { fail(t("register.usernameRequired"), "reg-username"); return; }
            if (username.length < 3) { fail(t("register.usernameShort"), "reg-username"); return; }
            if (!EMAIL_PATTERN.test(email)) { fail(t("register.emailInvalid"), "reg-email"); return; }
            // The same check-digit rule as the server (docs/DEFENSE.md, T15): catches a mistyped number before the request.
            if (!isValidTckn(tckn)) { fail(t("register.tcknInvalid"), "reg-tckn"); return; }
            if (!/^\d{6}$/.test(password)) { fail(t("register.passwordInvalid"), "reg-password"); return; }

            const res = await withWakeHint(() => api("/auth/register", {
                method: "POST", auth: false, timeoutMs: AUTH_TIMEOUT_MS, body: { firstName, lastName, username, email, tckn, password }
            }));
            if (!res.ok) { showMessage(errorDiv, messageFromResponse(res), "error"); return; }
            if (!res.data || !res.data.token) { showMessage(errorDiv, t("err.Generic"), "error"); return; }

            saveAuth(res.data.token, buildUser(res.data), res.data.refreshToken, res.data.accessTokenExpiresAt);
            redirectByUserRole();
        });
    });
}

// Forgot password: step 1 e-mails a one-time code, step 2 sets the new PIN with that code.
function initForgotForm() {
    const form = byId("forgot-form");
    const errorDiv = byId("forgot-error");
    const successDiv = byId("forgot-success");
    const btnSend = byId("btn-forgot-send");

    const hideAll = () => { hideMessage(errorDiv); hideMessage(successDiv); };
    const fail = (text, focusId) => { hideAll(); showMessage(errorDiv, text, "error"); if (focusId) byId(focusId).focus(); };

    // The server also throttles repeats; the button counts the minute down so nobody has to guess why it is grey.
    const COOLDOWN_MS = 60000;
    let cooldownUntil = 0;
    let cooldownTimer = null;
    const renderSendButton = () => {
        const left = Math.ceil((cooldownUntil - Date.now()) / 1000);
        if (left > 0) {
            btnSend.disabled = true;
            btnSend.textContent = t("forgot.resendIn", { seconds: left });
        } else {
            clearInterval(cooldownTimer);
            cooldownTimer = null;
            if (btnSend.dataset.busy !== "1") btnSend.disabled = false;
            btnSend.textContent = t("forgot.sendCode");
        }
    };
    onLanguageChange(renderSendButton);

    btnSend.addEventListener("click", async () => {
        const tckn = byId("forgot-tckn").value.trim();
        hideAll();
        if (!/^\d{11}$/.test(tckn)) { fail(t("forgot.tcknInvalid"), "forgot-tckn"); return; }

        const sent = await withBusy(btnSend, async () => {
            const res = await withWakeHint(() => api("/auth/forgot-password", { method: "POST", auth: false, timeoutMs: AUTH_TIMEOUT_MS, body: { tckn } }));
            if (!res.ok) { fail(messageFromResponse(res)); return false; }
            // The answer is the same whether or not the T.C. number is registered.
            showMessage(successDiv, t("forgot.codeSent"), "success");
            byId("forgot-code").focus();
            return true;
        });

        if (sent) {
            cooldownUntil = Date.now() + COOLDOWN_MS;
            clearInterval(cooldownTimer);
            cooldownTimer = setInterval(renderSendButton, 1000);
            renderSendButton();
        }
    });

    form.addEventListener("submit", event => {
        event.preventDefault();
        withBusy(byId("btn-forgot-submit"), async () => {
            hideAll();
            const tckn = byId("forgot-tckn").value.trim();
            const code = byId("forgot-code").value.trim();
            const newPassword = byId("forgot-new-password").value;

            if (!/^\d{11}$/.test(tckn)) { fail(t("forgot.tcknInvalid"), "forgot-tckn"); return; }
            if (!/^\d{6}$/.test(code)) { fail(t("login.codeInvalid"), "forgot-code"); return; }
            if (!/^\d{6}$/.test(newPassword)) { fail(t("register.passwordInvalid"), "forgot-new-password"); return; }

            const res = await withWakeHint(() => api("/auth/reset-password", { method: "POST", auth: false, timeoutMs: AUTH_TIMEOUT_MS, body: { tckn, code, newPassword } }));
            if (!res.ok) { fail(messageFromResponse(res)); return; }

            showMessage(successDiv, t("forgot.success"), "success");
            ["forgot-tckn", "forgot-code", "forgot-new-password"].forEach(id => { byId(id).value = ""; });
        });
    });
}

/* ==========================================================================
   CUSTOMER DASHBOARD: accounts and transaction history (dashboard.html)
   ========================================================================== */
let activeAccountId = null;      // selected account (drives the history table)
let activeCreditCardId = null;   // selected credit card (drives the card panel)
let showAllTransactions = false;
let currentTransactions = { accountId: null, items: [] };
let savedContacts = [];
let standingOrders = [];

function infoRow(text, columns, extraClass) {
    return h("tr", null, h("td", { colspan: String(columns), class: `text-center ${extraClass || "text-muted"}`, text }));
}

function accountTitle(acc) {
    if (acc.accountType === "TimeDeposit") return t("acc.titleTime");
    if (acc.currency === "XAU") return t("acc.titleGold");
    if (acc.currency === "XAG") return t("acc.titleSilver");
    return t("acc.titleDemand");
}

function findAccount(id) {
    return (accountsStore.data || []).find(acc => acc.id === id) || null;
}

function renderAccounts(accounts) {
    const listEl = byId("accounts-list");
    if (!listEl) return;
    const list = Array.isArray(accounts) ? accounts : [];

    if (activeAccountId && !list.some(acc => acc.id === activeAccountId)) activeAccountId = null;
    clearChildren(listEl);

    if (!list.length) {
        listEl.append(h("div", { class: "text-muted", text: t("accounts.none") }));
    }

    list.forEach(acc => {
        const isActive = acc.id === activeAccountId;
        const card = h("div", { class: `account-card glassmorphism${isActive ? " active" : ""}`, dataset: { accountId: acc.id } },
            h("button", {
                type: "button", class: "card-hit", "aria-pressed": isActive ? "true" : "false",
                "aria-label": `${accountTitle(acc)} ${acc.accountNumber}`,
                onclick: () => selectAccount(acc.id)
            }),
            h("div", { class: "account-header" },
                h("span", { text: accountTitle(acc) }),
                h("span", { class: "account-currency", text: acc.currency })),
            h("div", { class: "account-balance", text: formatMoney(acc.balance, acc.currency) }),
            h("div", { class: "account-number", text: acc.accountNumber }),
            h("div", { class: "account-meta" }, `${t("accounts.code")} `, h("span", { class: "account-meta-value", text: acc.accountCode || "-" })));

        if (acc.accountType === "TimeDeposit" && Number(acc.interestRate) > 0) {
            card.append(h("div", { class: "account-extra", text: t("accounts.timeInfo", { rate: formatPercent(acc.interestRate) }) }));
        }

        const deleteButton = h("button", { type: "button", class: "btn btn-danger btn-xs account-delete", text: t("accounts.delete"),
            "aria-label": `${t("accounts.delete")}: ${acc.accountNumber}` });
        deleteButton.addEventListener("click", () => startAccountDeletion(acc, deleteButton));
        card.append(h("div", { class: "account-actions" }, deleteButton));
        listEl.append(card);
    });

    fillTransferSource();
    if (!activeAccountId && list.length) selectAccount(list[0].id);
}

function selectAccount(accountId) {
    if (accountId !== activeAccountId) showAllTransactions = false;
    activeAccountId = accountId;
    document.querySelectorAll("#accounts-list .account-card").forEach(card => {
        const on = card.dataset.accountId === accountId;
        card.classList.toggle("active", on);
        const hit = card.querySelector(".card-hit");
        if (hit) hit.setAttribute("aria-pressed", on ? "true" : "false");
    });
    loadTransactions(accountId);
}

async function loadAccounts() {
    const listEl = byId("accounts-list");
    if (listEl && !accountsStore.data) {
        clearChildren(listEl);
        listEl.append(h("div", { class: "loading-spinner", text: t("accounts.loading") }));
    }

    const result = await accountsStore.refresh();
    if (!result.ok && listEl) {
        clearChildren(listEl);
        listEl.append(
            h("div", { class: "alert alert-danger", role: "alert", text: messageFromResponse(result.res) }),
            h("button", { type: "button", class: "btn btn-secondary btn-sm", text: t("common.retry"), onclick: () => loadAccounts() }));
    }
}

function fillTransferSource() {
    const select = byId("transfer-source");
    if (!select) return;
    const previous = select.value;
    clearChildren(select);
    (accountsStore.data || []).forEach(acc => {
        select.append(h("option", { value: acc.accountNumber, text: `${acc.accountNumber} (${formatMoney(acc.balance, acc.currency)})` }));
    });
    if (previous && Array.from(select.options).some(option => option.value === previous)) select.value = previous;
}

// Closing an account: with a balance the money moves to another account of the customer first.
async function startAccountDeletion(acc, button) {
    const runDeletion = async (targetId) => {
        const query = targetId ? `?targetAccountId=${encodeURIComponent(targetId)}` : "";
        const res = await api(`/banking/accounts/${encodeURIComponent(acc.id)}${query}`, { method: "DELETE" });
        if (!res.ok) { notify(messageFromResponse(res), "error"); return false; }
        notify(t("accounts.deleted"), "success");
        if (activeAccountId === acc.id) activeAccountId = null;
        await accountsStore.refresh(true);
        if (activeAccountId) loadTransactions(activeAccountId);
        return true;
    };

    if (!(acc.balance > 0)) {
        if (!(await uiConfirm(t("accounts.deleteConfirm"), { danger: true, confirmText: t("accounts.delete") }))) return;
        await withBusy(button, () => runDeletion(null));
        return;
    }

    const targets = (accountsStore.data || []).filter(other => other.id !== acc.id);
    if (!targets.length) { notify(t("accounts.deleteNoTarget"), "error"); return; }

    const dialog = createDialog({
        title: t("accounts.closeTransferTitle"),
        description: t("accounts.closeTransferDesc", { balance: formatMoney(acc.balance, acc.currency) })
    });
    const selectId = `delete-target-${dialogSequence}`;
    const select = h("select", { id: selectId, class: "form-control" },
        targets.map(other => h("option", { value: other.id, text: `${other.accountNumber} (${formatMoney(other.balance, other.currency)})` })));
    dialog.body.append(h("div", { class: "dialog-field" }, h("label", { for: selectId, text: t("accounts.targetLabel") }), select));

    const cancel = h("button", { type: "button", class: "btn btn-secondary", text: t("common.cancel") });
    const confirmButton = h("button", { type: "button", class: "btn btn-primary", text: t("accounts.transferAndClose") });
    cancel.addEventListener("click", () => dialog.close());
    confirmButton.addEventListener("click", () => withBusy(confirmButton, async () => {
        if (await runDeletion(select.value)) dialog.close();
    }));
    dialog.footer.append(cancel, confirmButton);
    dialog.open({ initialFocus: select });
}

/* ---------- transaction history ---------- */
const CATEGORY_ICONS = { "Market": "🛒", "Fatura": "📄", "Eğlence": "🍿", "Yatırım": "📈" };

function transactionIcon(tx) {
    return CATEGORY_ICONS[tx.category] || "💸";
}

// Is this movement money leaving the given account? The server's types are Deposit, Withdrawal and Transfer (an exchange is a
// Transfer between two of the customer's own accounts), so the account numbers decide.
function isOutgoingTransaction(tx, accountNumber) {
    switch (tx.type) {
        case "Deposit":
            return false;
        case "Transfer":
            if (tx.destinationAccountNumber === accountNumber) return false;
            return tx.sourceAccountNumber === accountNumber;
        default:
            return tx.sourceAccountNumber === accountNumber;
    }
}

// The amount and currency the viewed account sees. The server sends one amount and one currency per side (an exchange moves
// TRY on one side and dollars or grams on the other); an older answer without them falls back to amount + the account's currency.
function transactionSide(tx, outgoing, fallbackCurrency) {
    const amount = outgoing ? tx.sourceAmount : tx.destinationAmount;
    const currency = outgoing ? tx.sourceCurrency : tx.destinationCurrency;
    return {
        amount: amount !== null && amount !== undefined ? amount : tx.amount,
        currency: currency || tx.currency || fallbackCurrency
    };
}

async function loadTransactions(accountId) {
    const bodyEl = byId("transactions-body");
    if (!bodyEl || !accountId) return;

    const seq = ++requestSeq.transactions;
    clearChildren(bodyEl);
    bodyEl.append(infoRow(t("history.loading"), 4));

    const res = await api(`/banking/transactions/${encodeURIComponent(accountId)}`);
    if (seq !== requestSeq.transactions) return; // a newer request replaced this one

    if (!res.ok || !Array.isArray(res.data)) {
        clearChildren(bodyEl);
        bodyEl.append(infoRow(messageFromResponse(res), 4, "text-danger"));
        byId("btn-tx-load-more").classList.add("hidden");
        return;
    }
    currentTransactions = { accountId, items: res.data };
    renderTransactions();
}

function renderTransactions() {
    const bodyEl = byId("transactions-body");
    const moreButton = byId("btn-tx-load-more");
    if (!bodyEl) return;

    const { accountId, items } = currentTransactions;
    const acc = findAccount(accountId);
    const accountNumber = acc ? acc.accountNumber : "";
    clearChildren(bodyEl);

    if (!items.length) {
        bodyEl.append(infoRow(accountId ? t("history.none") : t("history.empty"), 4));
        moreButton.classList.add("hidden");
        return;
    }

    const visible = showAllTransactions ? items : items.slice(0, 5);
    visible.forEach(tx => {
        const outgoing = isOutgoingTransaction(tx, accountNumber);
        const side = transactionSide(tx, outgoing, acc && acc.currency);
        const typeLabel = hasKey(`txType.${tx.type}`) ? t(`txType.${tx.type}`) : String(tx.type ?? "-");
        const row = h("tr", { class: "clickable-row", tabindex: "0", title: t("history.openReceipt") },
            h("td", { text: formatShortDateTime(tx.createdAt) }),
            h("td", null, h("span", { class: "badge-role", text: typeLabel })),
            h("td", null, h("span", { class: "tx-icon", "aria-hidden": "true", text: transactionIcon(tx) }), serverDescription(tx.description) || "-"),
            h("td", { class: `text-right ${outgoing ? "tx-amount-negative" : "tx-amount-positive"}`,
                text: `${outgoing ? "-" : "+"}${formatMoney(side.amount, side.currency)}` }));
        row.addEventListener("click", () => showTransactionSlip(tx));
        row.addEventListener("keydown", event => {
            if (event.key === "Enter" || event.key === " ") { event.preventDefault(); showTransactionSlip(tx); }
        });
        bodyEl.append(row);
    });

    if (items.length > 5) {
        moreButton.textContent = showAllTransactions ? t("history.showLess") : t("history.showMore", { count: items.length - 5 });
        moreButton.classList.remove("hidden");
    } else {
        moreButton.classList.add("hidden");
    }
}

function showTransactionSlip(tx) {
    const modal = byId("slip-modal");
    if (!modal) return;
    const acc = findAccount(currentTransactions.accountId);

    byId("slip-date").textContent = formatDateTime(tx.createdAt);
    byId("slip-ref-no").textContent = typeof tx.id === "number" ? `TX-${String(tx.id).padStart(8, "0")}` : `TX-${String(tx.id ?? "-").toUpperCase()}`;
    byId("slip-type").textContent = hasKey(`txType.${tx.type}`) ? t(`txType.${tx.type}`) : String(tx.type ?? "-");
    byId("slip-sender-name").textContent = tx.sourceAccountOwnerName || t("slip.customer");
    byId("slip-sender-acc").textContent = tx.sourceAccountNumber || "-";
    byId("slip-receiver-name").textContent = tx.destinationAccountOwnerName || t("slip.customer");
    byId("slip-receiver-acc").textContent = tx.destinationAccountNumber || "-";
    // A conversion shows both sides ("100.00 USD -> 3,450.00 TRY"); everything else the amount of the viewed account.
    const outgoing = isOutgoingTransaction(tx, acc ? acc.accountNumber : "");
    const side = transactionSide(tx, outgoing, acc && acc.currency);
    const converted = tx.sourceCurrency && tx.destinationCurrency && tx.sourceCurrency !== tx.destinationCurrency
        && tx.sourceAmount !== null && tx.sourceAmount !== undefined && tx.destinationAmount !== null && tx.destinationAmount !== undefined;
    byId("slip-amount").textContent = converted
        ? `${formatMoney(tx.sourceAmount, tx.sourceCurrency)} -> ${formatMoney(tx.destinationAmount, tx.destinationCurrency)}`
        : formatMoney(side.amount, side.currency);
    byId("slip-desc").textContent = serverDescription(tx.description) || "-";

    openModal(modal, { initialFocus: "#btn-close-slip" });
}

function initHistoryAndSlip() {
    byId("btn-tx-load-more").addEventListener("click", () => {
        showAllTransactions = !showAllTransactions;
        renderTransactions();
    });
    byId("btn-close-slip").addEventListener("click", () => closeModal(byId("slip-modal")));
}

/* ==========================================================================
   Transfer form + one-time code modal (dashboard.html)
   ========================================================================== */
let currentOtpCallback = null;

// Shows the one-time code dialog. confirmCallback(code) resolves to { success, message }.
function showOTPModal(reasonMessage, confirmCallback, onClosed) {
    const modal = byId("otp-modal");
    const inputEl = byId("otp-code-input");
    if (!modal || !inputEl) return;

    byId("otp-modal-desc").textContent = reasonMessage ? `${reasonMessage} ${t("otp.desc")}` : t("otp.desc");
    inputEl.value = "";
    hideMessage(byId("otp-error-msg"));
    byId("otp-error-msg").classList.add("hidden");
    byId("btn-submit-otp").disabled = false;

    currentOtpCallback = confirmCallback;
    openModal(modal, { initialFocus: inputEl, closeOnBackdrop: false, onClose: () => { currentOtpCallback = null; if (onClosed) onClosed(); } });
}

function initOTPModalEvents() {
    const modal = byId("otp-modal");
    const inputEl = byId("otp-code-input");
    const errorEl = byId("otp-error-msg");
    const submitBtn = byId("btn-submit-otp");
    if (!modal) return;

    digitsOnly(inputEl);
    byId("btn-close-otp").addEventListener("click", () => closeModal(modal));

    const submitOtp = () => withBusy(submitBtn, async () => {
        const code = inputEl.value.trim();
        errorEl.classList.add("hidden");
        if (!/^\d{6}$/.test(code)) {
            errorEl.textContent = t("otp.invalid");
            errorEl.className = "alert alert-danger";
            inputEl.focus();
            return;
        }
        const callback = currentOtpCallback;
        if (!callback) return;

        let result;
        try { result = await callback(code); } catch (err) { result = { success: false, message: t("err.ConnectionError") }; }
        if (result && result.success) {
            closeModal(modal);
        } else {
            errorEl.textContent = (result && result.message) || t("err.Generic");
            errorEl.className = "alert alert-danger";
            inputEl.focus();
        }
    });

    submitBtn.addEventListener("click", submitOtp);
    inputEl.addEventListener("keydown", event => {
        if (event.key === "Enter") { event.preventDefault(); submitOtp(); }
    });
}

async function onTransferSucceeded(payload) {
    const msgEl = byId("transfer-message");
    showMessage(msgEl, t("transfer.success"), "success");

    await saveContactAfterTransfer(payload.destinationAccountNumber);

    byId("transfer-dest").value = "";
    byId("transfer-amount").value = "";
    byId("transfer-desc-input").value = "";
    byId("transfer-saved-contacts").value = "";

    await accountsStore.refresh(true);
    if (activeAccountId) loadTransactions(activeAccountId);
    celebrate();
}

async function handleTransferSubmit() {
    const msgEl = byId("transfer-message");
    hideMessage(msgEl);

    const sourceAccountNumber = byId("transfer-source").value;
    const destinationAccountNumber = normalizeAccountNumber(byId("transfer-dest").value);
    const description = byId("transfer-desc-input").value.trim();
    const category = byId("transfer-category-input").value;
    const amount = parseAmount(byId("transfer-amount").value);

    if (!sourceAccountNumber) { showMessage(msgEl, t("transfer.noSource"), "error"); byId("transfer-source").focus(); return; }
    if (!destinationAccountNumber) { showMessage(msgEl, t("transfer.noDest"), "error"); byId("transfer-dest").focus(); return; }
    if (destinationAccountNumber.length < 10 || destinationAccountNumber.length > 30) { showMessage(msgEl, t("transfer.destInvalid"), "error"); byId("transfer-dest").focus(); return; }
    if (!amount.ok) { showMessage(msgEl, errorText(amount.errorKey), "error"); byId("transfer-amount").focus(); return; }

    const payload = { sourceAccountNumber, destinationAccountNumber, amount: amount.value, description, category };
    const res = await api("/banking/transfer", { method: "POST", body: payload });
    if (res.ok) { await onTransferSucceeded(payload); return; }

    const key = res.data && res.data.errorKey;
    if (key === "Requires2FA" || key === "SuspectedFraudDuplicate" || key === "SuspectedFraudHighValue") {
        // The server asks for a one-time code ("message|OTP:code" in demo mode).
        const { otp } = splitOtpMarker(res.data.message);
        showOtpToast(otp);
        showOTPModal(errorText(key), async (code) => {
            const second = await api("/banking/transfer", { method: "POST", body: { ...payload, otpCode: code } });
            if (!second.ok) return { success: false, message: messageFromResponse(second) };
            await onTransferSucceeded(payload);
            return { success: true };
        });
        return;
    }

    showMessage(msgEl, messageFromResponse(res), "error");
}

function initTransferForm() {
    byId("transfer-form").addEventListener("submit", event => {
        event.preventDefault();
        withBusy(byId("btn-transfer-submit"), handleTransferSubmit);
    });
}

/* ==========================================================================
   Currency / precious metal exchange (dashboard.html)
   ========================================================================== */
let activeMarketRates = [];

// The rate row of the selected asset (null while the rates have not arrived).
function selectedRateInfo() {
    const select = byId("exchange-asset");
    return select ? activeMarketRates.find(rate => rate.code === select.value) || null : null;
}

function updateExchangeRateDisplay() {
    const rateEl = byId("exchange-current-rate");
    const totalEl = byId("exchange-total-cost");
    if (!rateEl || !totalEl) return;

    const action = byId("exchange-action").value;
    const info = selectedRateInfo();

    // A stand-in price (the live feed is down) is shown as indicative and the form is paused: the server refuses to trade at it.
    const indicative = !!(info && info.isFallback);
    const hint = byId("exchange-rate-hint");
    const submitButton = byId("btn-exchange-submit");
    if (hint) {
        hint.classList.toggle("hidden", !indicative);
        hint.textContent = indicative ? t("exchange.indicativeHint") : "";
    }
    if (submitButton && submitButton.dataset.busy !== "1") submitButton.disabled = indicative;

    if (!info) {
        rateEl.textContent = "-";
        totalEl.textContent = formatMoney(0, "TRY");
        return;
    }

    const rate = action === "buy" ? info.sell : info.buy;
    rateEl.textContent = `${formatNumber(rate, 2, 4)} TRY`;
    const amount = Number(byId("exchange-amount").value) || 0;
    totalEl.textContent = formatMoney(amount * rate, "TRY");
}

function fillExchangeSources() {
    const select = byId("exchange-source");
    if (!select) return;
    const isBuy = byId("exchange-action").value === "buy";
    const asset = byId("exchange-asset").value;
    const wanted = isBuy ? "TRY" : asset;
    const previous = select.value;

    clearChildren(select);
    const matching = (accountsStore.data || []).filter(acc => acc.currency === wanted);
    if (!matching.length) {
        select.append(h("option", { value: "", text: isBuy ? t("exchange.noTry") : t("exchange.noAsset", { asset }) }));
    } else {
        matching.forEach(acc => select.append(h("option", { value: acc.id, text: `${acc.accountNumber} (${formatMoney(acc.balance, acc.currency)})` })));
        if (previous && Array.from(select.options).some(option => option.value === previous)) select.value = previous;
    }
    updateExchangeRateDisplay();
}

function initExchangeWidget() {
    const form = byId("exchange-form");
    if (!form) return;
    const msgEl = byId("exchange-message");

    byId("exchange-action").addEventListener("change", fillExchangeSources);
    byId("exchange-asset").addEventListener("change", fillExchangeSources);
    byId("exchange-amount").addEventListener("input", updateExchangeRateDisplay);
    accountsStore.subscribe(fillExchangeSources);
    onLanguageChange(fillExchangeSources);
    fillExchangeSources();

    form.addEventListener("submit", event => {
        event.preventDefault();
        withBusy(byId("btn-exchange-submit"), async () => {
            hideMessage(msgEl);
            const sourceAccountId = byId("exchange-source").value;
            const asset = byId("exchange-asset").value;
            const action = byId("exchange-action").value;
            const amount = parseAmount(byId("exchange-amount").value);

            const info = selectedRateInfo();
            if (info && info.isFallback) { showMessage(msgEl, t("exchange.indicativeHint"), "error"); return; }

            if (!sourceAccountId) { showMessage(msgEl, t("exchange.noAccount"), "error"); byId("exchange-source").focus(); return; }
            if (!amount.ok) { showMessage(msgEl, errorText(amount.errorKey), "error"); byId("exchange-amount").focus(); return; }

            const res = await api("/banking/exchange", { method: "POST", body: { sourceAccountId, asset, action, amount: amount.value } });
            if (!res.ok) { showMessage(msgEl, messageFromResponse(res), "error"); return; }

            showMessage(msgEl, t("exchange.success"), "success");
            byId("exchange-amount").value = "";
            await accountsStore.refresh(true);
            if (activeAccountId) loadTransactions(activeAccountId);
        }).then(updateExchangeRateDisplay); // withBusy re-enables the button: apply the indicative-price lock again
    });
}

/* ==========================================================================
   Saved contacts (dashboard.html)
   ========================================================================== */
function renderContactSelect() {
    const select = byId("transfer-saved-contacts");
    if (!select) return;
    clearChildren(select);
    select.append(h("option", { value: "", text: t("transfer.savedContactsPlaceholder") }));
    savedContacts.forEach(contact => {
        select.append(h("option", { value: contact.accountNumber, text: `${contact.alias} (${contact.accountNumber})` }));
    });
}

async function loadSavedContacts() {
    const res = await api("/banking/contacts");
    if (res.ok && Array.isArray(res.data)) {
        savedContacts = res.data;
        renderContactSelect();
    }
    return res.ok;
}

async function saveContactAfterTransfer(destinationAccountNumber) {
    const checkEl = byId("save-contact-check");
    const aliasInput = byId("save-contact-alias");
    if (!checkEl || !checkEl.checked) return;

    const alias = aliasInput.value.trim() || `${t("contacts.defaultAlias")} ${destinationAccountNumber.slice(-4)}`;
    const res = await api("/banking/contacts", { method: "POST", body: { alias, accountNumber: destinationAccountNumber } });
    if (res.ok) {
        checkEl.checked = false;
        aliasInput.value = "";
        aliasInput.classList.add("hidden");
        await loadSavedContacts();
    } else {
        notify(`${t("contacts.saveFailed")} ${messageFromResponse(res)}`, "error");
    }
}

function openManageContacts() {
    const dialog = createDialog({ title: t("contacts.manageTitle"), description: t("contacts.manageDesc") });
    const listBox = h("div", { class: "contacts-list" });
    dialog.body.append(listBox);
    dialog.footer.append(h("button", { type: "button", class: "btn btn-secondary btn-block", text: t("common.close"), onclick: () => dialog.close() }));

    const render = () => {
        clearChildren(listBox);
        if (!savedContacts.length) {
            listBox.append(h("div", { class: "text-muted text-center py-4", text: t("contacts.none") }));
            return;
        }
        savedContacts.forEach(contact => {
            const editButton = h("button", { type: "button", class: "btn btn-secondary btn-xs", "aria-label": t("contacts.editLabel", { name: contact.alias }), title: t("contacts.editLabel", { name: contact.alias }) }, "✏️");
            const deleteButton = h("button", { type: "button", class: "btn btn-danger btn-xs", "aria-label": t("contacts.deleteLabel", { name: contact.alias }), title: t("contacts.deleteLabel", { name: contact.alias }) }, "🗑️");

            editButton.addEventListener("click", async () => {
                const alias = await uiPrompt({
                    title: t("contacts.editTitle"), message: t("contacts.editMessage", { name: contact.alias }),
                    label: t("contacts.aliasLabel"), value: contact.alias, maxlength: 50,
                    validate: value => (value ? null : t("contacts.aliasEmpty")),
                    submit: async (value) => {
                        const res = await api("/banking/contacts", { method: "POST", body: { alias: value, accountNumber: contact.accountNumber } });
                        return res.ok ? { ok: true } : { ok: false, message: messageFromResponse(res) };
                    }
                });
                if (alias !== null) { await loadSavedContacts(); render(); }
            });

            deleteButton.addEventListener("click", async () => {
                if (!(await uiConfirm(t("contacts.deleteConfirm"), { danger: true, confirmText: t("common.delete") }))) return;
                const res = await withBusy(deleteButton, () => api(`/banking/contacts/${encodeURIComponent(contact.id)}`, { method: "DELETE" }));
                if (res && res.ok) { await loadSavedContacts(); render(); }
                else if (res) notify(messageFromResponse(res), "error");
            });

            listBox.append(h("div", { class: "contact-row" },
                h("div", { class: "contact-info" },
                    h("span", { class: "contact-alias", text: contact.alias }),
                    h("span", { class: "contact-number", text: contact.accountNumber })),
                h("div", { class: "contact-actions" }, editButton, deleteButton)));
        });
    };

    render();
    dialog.open({ initialFocus: dialog.footer.querySelector("button") });
}

function initSavedContacts() {
    const selectEl = byId("transfer-saved-contacts");
    if (!selectEl) return;
    const checkEl = byId("save-contact-check");
    const aliasInput = byId("save-contact-alias");

    checkEl.addEventListener("change", () => {
        aliasInput.classList.toggle("hidden", !checkEl.checked);
        if (!checkEl.checked) aliasInput.value = "";
        else aliasInput.focus();
    });
    selectEl.addEventListener("change", () => { if (selectEl.value) byId("transfer-dest").value = selectEl.value; });
    byId("transfer-dest").addEventListener("blur", event => { event.currentTarget.value = normalizeAccountNumber(event.currentTarget.value); });
    byId("btn-manage-contacts").addEventListener("click", openManageContacts);
    onLanguageChange(renderContactSelect);
    loadSavedContacts();
}


/* ==========================================================================
   Credit cards, statements and the card panel (dashboard.html)
   ========================================================================== */
let currentStatement = null;   // the newest statement of the selected card (null while loading or when there is none)
let chosenTheme = null;        // the theme the customer picked in the preview (kept across refreshes and language changes)
let simulationDisabled = false;

function maskedCardNumber(card) {
    return `**** **** **** ${String(card.cardNumber || "").slice(-4) || "0000"}`;
}

function updateCardPreview(card) {
    const numberEl = byId("preview-card-number");
    if (!numberEl) return;
    numberEl.textContent = card ? maskedCardNumber(card) : "**** **** **** ****";
    byId("preview-card-expiry").textContent = `EXP ${(card && card.expiryDate) || "12/31"}`;
    // The CVV is never stored or listed: it is shown once, in a dialog, when the card is issued (see showIssuedCardDialog).
    byId("preview-card-cvv").textContent = "•••";
    byId("preview-card-type-badge").textContent = "CREDIT CARD";
    setCardTheme(chosenTheme || (card && card.cardTheme) || "theme-neon-blue");
}

function setCardTheme(theme) {
    const safeTheme = ["theme-neon-blue", "theme-sunset", "theme-metallic-dark", "theme-glass"].includes(theme) ? theme : "theme-neon-blue";
    byId("debit-card-preview").className = `debit-card card-front ${safeTheme}`;
    byId("debit-card-preview-back").className = `debit-card card-back ${safeTheme}`;
    document.querySelectorAll(".theme-btn").forEach(btn => {
        const on = btn.dataset.theme === safeTheme;
        btn.classList.toggle("active", on);
        btn.setAttribute("aria-pressed", on ? "true" : "false");
    });
}

function renderCreditCards(cards) {
    const listEl = byId("credit-cards-list");
    if (!listEl) return;
    const list = Array.isArray(cards) ? cards : [];
    const placeholder = byId("cc-no-selected");
    const details = byId("cc-details-content");
    clearChildren(listEl);
    clearChildren(placeholder);

    if (!list.length) {
        activeCreditCardId = null;
        currentStatement = null;
        listEl.append(h("div", { class: "text-muted", text: t("cards.none") }));
        details.classList.add("hidden");
        placeholder.classList.remove("hidden");
        placeholder.append(h("div", { class: "cc-empty" },
            h("span", { class: "cc-empty-icon", "aria-hidden": "true" }, "💳"),
            h("h4", { text: t("cards.emptyTitle") }),
            h("p", { class: "text-muted", text: t("cards.emptyDesc") }),
            h("button", { type: "button", class: "btn btn-primary btn-sm", text: t("cards.applyNow"), onclick: () => byId("btn-apply-creditcard").click() })));
        updateCardPreview(null);
        return;
    }

    placeholder.textContent = t("cards.selectPrompt");
    if (!list.some(card => card.id === activeCreditCardId)) activeCreditCardId = list[0].id;

    list.forEach(card => {
        const isActive = card.id === activeCreditCardId;
        listEl.append(h("div", { class: `account-card credit glassmorphism${isActive ? " active" : ""}`, dataset: { cardId: card.id } },
            h("button", { type: "button", class: "card-hit", "aria-pressed": isActive ? "true" : "false",
                "aria-label": `SmartCredit ${maskedCardNumber(card)}`, onclick: () => selectCreditCard(card.id) }),
            h("div", { class: "account-header" }, h("span", { text: "SmartCredit" }), h("span", { class: "account-currency", text: "TRY" })),
            h("div", { class: "account-balance", text: formatMoney(card.currentDebt, "TRY") }),
            h("div", { class: "account-number", text: maskedCardNumber(card) }),
            h("div", { class: "credit-limit-info" },
                h("span", { text: `${t("cards.limitShort")}: ${formatMoney(card.cardLimit, "TRY")}` }),
                h("span", { text: `${t("cards.availableShort")}: ${formatMoney(card.availableLimit, "TRY")}` })),
            h("div", { class: "btn btn-secondary btn-xs btn-stmt-view", "aria-hidden": "true", text: t("cards.viewStatement") })));
    });

    renderCardDetails(list.find(card => card.id === activeCreditCardId));
}

function renderCardDetails(card) {
    const details = byId("cc-details-content");
    if (!card) { details.classList.add("hidden"); return; }
    byId("cc-no-selected").classList.add("hidden");
    details.classList.remove("hidden");
    byId("cc-details-masked-no").textContent = maskedCardNumber(card);
    byId("cc-details-limit").textContent = formatMoney(card.cardLimit, "TRY");
    byId("cc-details-avail").textContent = formatMoney(card.availableLimit, "TRY");
    byId("cc-details-debt").textContent = formatMoney(card.currentDebt, "TRY");
    updateCardPreview(card);
}

function selectCreditCard(cardId) {
    if (cardId === activeCreditCardId && currentStatement) return;
    activeCreditCardId = cardId;
    document.querySelectorAll("#credit-cards-list .account-card").forEach(el => {
        const on = el.dataset.cardId === cardId;
        el.classList.toggle("active", on);
        const hit = el.querySelector(".card-hit");
        if (hit) hit.setAttribute("aria-pressed", on ? "true" : "false");
    });
    renderCardDetails((cardsStore.data || []).find(card => card.id === cardId));
    hideMessage(byId("cc-pay-message"));
    byId("cc-pay-amount").value = "";
    loadStatement(cardId);
}

function setStatementPlaceholders() {
    ["val-cc-stmt-period", "val-cc-stmt-due"].forEach(id => { byId(id).textContent = "-"; });
    byId("val-cc-stmt-debt").textContent = "-";
    byId("val-cc-stmt-min").textContent = "-";
    byId("cc-stmt-status").classList.add("hidden");
}

function renderStatement() {
    const stmt = currentStatement;
    const tbody = byId("cc-stmt-transactions-body");
    const statusEl = byId("cc-stmt-status");
    clearChildren(tbody);

    if (!stmt) {
        setStatementPlaceholders();
        tbody.append(infoRow(t("cards.noSpend"), 3));
        return;
    }

    byId("val-cc-stmt-period").textContent = stmt.periodName ? localizePeriodName(stmt.periodName) : "-";
    byId("val-cc-stmt-debt").textContent = formatMoney(stmt.periodDebt, "TRY");
    byId("val-cc-stmt-min").textContent = formatMoney(stmt.minimumPayment, "TRY");
    byId("val-cc-stmt-due").textContent = formatBusinessDate(stmt.dueDate);

    const paidAmount = Number(stmt.paidAmount) || 0;
    const remaining = Math.max(0, stmt.periodDebt - paidAmount);
    const minRemaining = Math.max(0, stmt.minimumPayment - paidAmount);
    statusEl.classList.remove("hidden");
    if (stmt.isPaid || remaining <= 0) {
        statusEl.className = "statement-payment-status paid";
        statusEl.textContent = t("cards.statusPaid");
    } else {
        statusEl.className = "statement-payment-status unpaid";
        statusEl.textContent = minRemaining <= 0
            ? t("cards.statusMinPaid", { remaining: formatMoney(remaining, "TRY") })
            : t("cards.statusUnpaid", { min: formatMoney(minRemaining, "TRY") });
    }

    const transactions = Array.isArray(stmt.transactions) ? stmt.transactions : [];
    if (!transactions.length) {
        tbody.append(infoRow(t("cards.noSpend"), 3));
        return;
    }
    transactions.forEach(tx => {
        tbody.append(h("tr", null,
            h("td", { text: formatShortDateTime(tx.createdAt) }),
            h("td", { text: serverDescription(tx.description) || "-" }),
            h("td", { class: "text-right tx-amount-negative", text: `-${formatMoney(tx.amount, "TRY")}` })));
    });
}

async function loadStatement(cardId) {
    currentStatement = null;
    if (!cardId) return;
    const tbody = byId("cc-stmt-transactions-body");
    const seq = ++requestSeq.statements;
    setStatementPlaceholders();
    clearChildren(tbody);
    tbody.append(infoRow(t("common.loading"), 3));

    const res = await api(`/banking/credit-cards/${encodeURIComponent(cardId)}/statements`);
    if (seq !== requestSeq.statements || cardId !== activeCreditCardId) return;

    if (!res.ok || !Array.isArray(res.data)) {
        clearChildren(tbody);
        tbody.append(infoRow(messageFromResponse(res), 3, "text-danger"));
        return;
    }
    // The server returns the statements newest first (ordered by cut-off date, descending).
    currentStatement = res.data[0] || null;
    renderStatement();
}

// One refresh for everything that changes after a card operation: the card list, its details and the statement.
async function refreshCreditCardPanel() {
    await cardsStore.refresh(true);
    await loadStatement(activeCreditCardId);
}

function populatePaymentSources() {
    const select = byId("cc-pay-source");
    if (!select) return;
    const previous = select.value;
    clearChildren(select);
    const tryAccounts = (accountsStore.data || []).filter(acc => acc.currency === "TRY");
    if (!tryAccounts.length) {
        select.append(h("option", { value: "", text: t("cards.noTryAccount") }));
        return;
    }
    tryAccounts.forEach(acc => select.append(h("option", { value: acc.accountNumber, text: `${acc.accountNumber} (${formatMoney(acc.balance, "TRY")})` })));
    if (previous && Array.from(select.options).some(option => option.value === previous)) select.value = previous;
}

// The server shows the CVV exactly once, in the answer that issues the card. It lives in this dialog only (never in the store,
// storage or the card list) and is gone when the dialog closes.
function showIssuedCardDialog(card) {
    const dialog = createDialog({ title: t("cards.issuedTitle"), description: t("cards.issuedDesc") });
    dialog.body.append(
        h("div", { class: "cvv-card-no", text: maskedCardNumber(card) }),
        h("p", { class: "dialog-text", text: t("cards.cvvLabel") }),
        h("div", { class: "cvv-box", text: String(card.cardCvv) }));
    const ok = h("button", { type: "button", class: "btn btn-primary btn-block", text: t("common.ok") });
    ok.addEventListener("click", () => dialog.close());
    dialog.footer.append(ok);
    dialog.open({ initialFocus: ok, closeOnBackdrop: false });
}

// The charge and advance-period simulations can be switched off on the server (404 SimulationDisabled): their widgets go away.
function isSimulationDisabled(res) {
    return !!(res && !res.ok && res.data && res.data.errorKey === "SimulationDisabled");
}

function hideSimulationWidgets() {
    simulationDisabled = true;
    ["cc-sim-advance", "cc-sim-charge"].forEach(id => { const el = byId(id); if (el) el.classList.add("hidden"); });
}

function initCreditCardEvents() {
    cardsStore.subscribe(renderCreditCards);
    accountsStore.subscribe(populatePaymentSources);
    onLanguageChange(() => {
        if (cardsStore.data) renderCreditCards(cardsStore.data);
        renderStatement();
        populatePaymentSources();
    });

    const amountInput = byId("cc-pay-amount");
    const presetAmount = (kind) => {
        if (!currentStatement) { notify(t("cards.noStatement"), "info"); return; }
        const paid = Number(currentStatement.paidAmount) || 0;
        const base = kind === "min" ? currentStatement.minimumPayment : currentStatement.periodDebt;
        amountInput.value = Math.max(0, base - paid).toFixed(2);
        amountInput.focus();
    };
    byId("btn-cc-pay-min").addEventListener("click", () => presetAmount("min"));
    byId("btn-cc-pay-full").addEventListener("click", () => presetAmount("full"));

    // Pay the card debt
    const payMsg = byId("cc-pay-message");
    byId("cc-pay-debt-form").addEventListener("submit", event => {
        event.preventDefault();
        withBusy(byId("btn-cc-pay-submit"), async () => {
            hideMessage(payMsg);
            const sourceAccountNumber = byId("cc-pay-source").value;
            const amount = parseAmount(amountInput.value);
            if (!activeCreditCardId) return;
            if (!sourceAccountNumber) { showMessage(payMsg, t("transfer.noSource"), "error"); return; }
            if (!amount.ok) { showMessage(payMsg, errorText(amount.errorKey), "error"); amountInput.focus(); return; }

            const res = await api(`/banking/credit-cards/${encodeURIComponent(activeCreditCardId)}/pay`, {
                method: "POST", body: { sourceAccountNumber, amount: amount.value }
            });
            if (!res.ok) { showMessage(payMsg, messageFromResponse(res), "error"); return; }

            showMessage(payMsg, t("cards.paySuccess"), "success");
            amountInput.value = "";
            await Promise.all([accountsStore.refresh(true), refreshCreditCardPanel()]);
            if (activeAccountId) loadTransactions(activeAccountId); // the payment left the TRY account
        });
    });

    // Simulate a purchase (the backend keeps the query-string contract for this endpoint)
    const chargeForm = byId("cc-charge-form");
    const chargeMsg = byId("cc-charge-message");
    chargeForm.addEventListener("submit", event => {
        event.preventDefault();
        withBusy(byId("btn-cc-charge-submit"), async () => {
            hideMessage(chargeMsg);
            if (!activeCreditCardId) return;
            const description = byId("cc-charge-desc").value.trim();
            const amount = parseAmount(byId("cc-charge-amount").value);
            if (!description) { showMessage(chargeMsg, t("cards.chargeNoMerchant"), "error"); byId("cc-charge-desc").focus(); return; }
            if (!amount.ok) { showMessage(chargeMsg, errorText(amount.errorKey), "error"); byId("cc-charge-amount").focus(); return; }

            const res = await api(`/banking/credit-cards/${encodeURIComponent(activeCreditCardId)}/charge?amount=${encodeURIComponent(String(amount.value))}&description=${encodeURIComponent(description)}`, { method: "POST" });
            if (!res.ok) {
                if (isSimulationDisabled(res)) hideSimulationWidgets();
                showMessage(chargeMsg, messageFromResponse(res), "error");
                return;
            }

            showMessage(chargeMsg, t("cards.chargeSuccess"), "success");
            chargeForm.reset();
            await Promise.all([accountsStore.refresh(true), refreshCreditCardPanel()]);
        });
    });

    // Advance the billing period (simulation)
    byId("btn-cc-advance-period").addEventListener("click", async event => {
        if (!activeCreditCardId) return;
        const button = event.currentTarget;
        if (!(await uiConfirm(t("cards.advanceConfirm"), { danger: true, confirmText: t("cards.advanceBtn") }))) return;
        await withBusy(button, async () => {
            const res = await api(`/banking/credit-cards/${encodeURIComponent(activeCreditCardId)}/advance-period`, { method: "POST" });
            if (!res.ok) {
                if (isSimulationDisabled(res)) hideSimulationWidgets();
                notify(messageFromResponse(res), "error");
                return;
            }
            notify(t("cards.advanceDone"), "success");
            await Promise.all([accountsStore.refresh(true), refreshCreditCardPanel()]);
        });
    });

    // Apply for a new card
    byId("btn-apply-creditcard").addEventListener("click", async event => {
        const button = event.currentTarget;
        if (!(await uiConfirm(t("cards.applyConfirm"), { confirmText: t("cards.apply") }))) return;
        await withBusy(button, async () => {
            const res = await api("/banking/credit-cards", { method: "POST" });
            if (!res.ok) { notify(messageFromResponse(res), "error"); return; }
            notify(t("cards.applyDone"), "success");
            celebrate();
            await refreshCreditCardPanel();
            if (res.data && res.data.cardCvv) showIssuedCardDialog(res.data);
        });
    });
}

async function loadCreditCards() {
    const listEl = byId("credit-cards-list");
    if (listEl && !cardsStore.data) {
        clearChildren(listEl);
        listEl.append(h("div", { class: "loading-spinner", text: t("cards.loading") }));
    }
    const result = await cardsStore.refresh();
    if (!result.ok && listEl) {
        clearChildren(listEl);
        listEl.append(
            h("div", { class: "alert alert-danger", role: "alert", text: messageFromResponse(result.res) }),
            h("button", { type: "button", class: "btn btn-secondary btn-sm", text: t("common.retry"), onclick: () => loadCreditCards() }));
        return;
    }
    await loadStatement(activeCreditCardId);
}

/* ==========================================================================
   Open a new account (dashboard.html)
   ========================================================================== */
// Tiered deposit rates for the 30-day time deposit. They are shown in the dialog and used by the calculator, which is an
// estimate: the bank's actual rate is the one stored on the account.
const DEPOSIT_TIERS = [
    { upTo: 50000, rate: 48.00 },
    { upTo: 250000, rate: 49.50 },
    { upTo: 1000000, rate: 51.00 },
    { upTo: Infinity, rate: 52.50 }
];
const DEPOSIT_WITHHOLDING_TAX = 0.075;
const DEPOSIT_TERM_DAYS = 30;

function renderDepositTiers() {
    const body = byId("deposit-tiers-body");
    if (!body) return;
    clearChildren(body);
    let from = 0;
    DEPOSIT_TIERS.forEach(tier => {
        const range = Number.isFinite(tier.upTo)
            ? `${formatNumber(from, 0, 0)} - ${formatNumber(tier.upTo, 0, 0)} TRY`
            : t("newacc.rangeOver", { from: formatNumber(from, 0, 0) });
        body.append(h("tr", null,
            h("td", { text: range }),
            h("td", { class: "text-right value-positive", text: formatPercent(tier.rate) })));
        from = tier.upTo;
    });
}

function resetCreateAccountDialog() {
    byId("vadeli-tiers-panel").classList.add("hidden");
    byId("calc-result").classList.add("hidden");
    byId("calc-principal").value = "";
    const first = document.querySelector('input[name="new-acc-choice"]');
    if (first) { first.checked = true; syncAccountTypeCards(); }
}

function syncAccountTypeCards() {
    document.querySelectorAll(".acc-type-card").forEach(card => {
        const radio = card.querySelector('input[type="radio"]');
        card.classList.toggle("active", !!(radio && radio.checked));
    });
    const checked = document.querySelector('input[name="new-acc-choice"]:checked');
    byId("vadeli-tiers-panel").classList.toggle("hidden", !(checked && checked.value === "TimeDeposit-TRY"));
}

function initCreateAccountEvent() {
    const modal = byId("create-account-modal");
    if (!modal) return;

    renderDepositTiers();
    onLanguageChange(renderDepositTiers);

    byId("btn-create-account").addEventListener("click", () => {
        resetCreateAccountDialog();
        openModal(modal, { onClose: resetCreateAccountDialog });
    });
    byId("btn-close-create-acc").addEventListener("click", () => closeModal(modal));
    document.querySelectorAll('input[name="new-acc-choice"]').forEach(radio => radio.addEventListener("change", syncAccountTypeCards));

    byId("btn-calc-interest").addEventListener("click", () => {
        const principal = parseAmount(byId("calc-principal").value);
        if (!principal.ok) { notify(errorText(principal.errorKey), "error"); byId("calc-principal").focus(); return; }

        const tier = DEPOSIT_TIERS.find(candidate => principal.value < candidate.upTo) || DEPOSIT_TIERS[DEPOSIT_TIERS.length - 1];
        const gross = principal.value * (tier.rate / 100) * (DEPOSIT_TERM_DAYS / 365);
        const net = gross - gross * DEPOSIT_WITHHOLDING_TAX;
        byId("calc-rate").textContent = formatPercent(tier.rate);
        byId("calc-net-profit").textContent = formatMoney(net, "TRY");
        byId("calc-result").classList.remove("hidden");
    });

    byId("btn-submit-create-acc").addEventListener("click", event => {
        withBusy(event.currentTarget, async () => {
            const checked = document.querySelector('input[name="new-acc-choice"]:checked');
            if (!checked) return;
            const [accountType, currency] = checked.value.split("-");

            const res = await api(`/banking/accounts?currency=${encodeURIComponent(currency)}&accountType=${encodeURIComponent(accountType)}`, { method: "POST" });
            if (!res.ok) { notify(messageFromResponse(res), "error"); return; }

            closeModal(modal);
            notify(t("newacc.created"), "success");
            await accountsStore.refresh(true);
        });
    });
}

/* ==========================================================================
   Standing orders (dashboard.html)
   ========================================================================== */
function fillStandingOrderSources() {
    const sourceSelect = byId("so-source-acc");
    const cardSelect = byId("so-target-cc");
    if (!sourceSelect || !cardSelect) return;

    const previousSource = sourceSelect.value;
    clearChildren(sourceSelect);
    (accountsStore.data || []).filter(acc => acc.currency === "TRY").forEach(acc => {
        sourceSelect.append(h("option", { value: acc.accountNumber, text: `${acc.accountNumber} (${formatMoney(acc.balance, "TRY")})` }));
    });
    if (previousSource && Array.from(sourceSelect.options).some(option => option.value === previousSource)) sourceSelect.value = previousSource;

    const previousCard = cardSelect.value;
    clearChildren(cardSelect);
    (cardsStore.data || []).forEach(card => {
        cardSelect.append(h("option", { value: card.id, text: `SmartCredit (**** ${String(card.cardNumber || "").slice(-4)})` }));
    });
    if (previousCard && Array.from(cardSelect.options).some(option => option.value === previousCard)) cardSelect.value = previousCard;
}

function frequencyLabel(frequency) {
    return t(frequency === "Daily" ? "orders.dailyAdj" : frequency === "Weekly" ? "orders.weeklyAdj" : "orders.monthlyAdj");
}

function renderStandingOrders() {
    const listEl = byId("standing-orders-list");
    if (!listEl) return;
    clearChildren(listEl);

    if (!standingOrders.length) {
        listEl.append(h("div", { class: "text-muted text-center py-4", text: t("orders.none") }));
        return;
    }

    standingOrders.forEach(order => {
        // The API names the kind "orderType" ("Transfer" or "CreditCardAutoPay"); the card's last four digits come with it.
        const isCard = order.orderType === "CreditCardAutoPay";
        const inactive = order.isActive === false;
        const description = isCard
            ? `${t("orders.descCard")}${order.creditCardLast4 ? ` (**** ${order.creditCardLast4})` : ""}`
            : t("orders.descTransfer", { freq: frequencyLabel(order.frequency), amount: formatMoney(order.amount, "TRY"), dest: order.destinationAccountNumber });
        const cancelButton = h("button", { type: "button", class: "btn btn-danger btn-xs so-cancel", text: inactive ? t("common.delete") : t("orders.cancel") });

        cancelButton.addEventListener("click", async () => {
            if (!(await uiConfirm(t(inactive ? "orders.deleteConfirm" : "orders.cancelConfirm"), { danger: true, confirmText: inactive ? t("common.delete") : t("orders.cancel") }))) return;
            const res = await withBusy(cancelButton, () => api(`/banking/standing-orders/${encodeURIComponent(order.id)}`, { method: "DELETE" }));
            if (!res) return;
            if (res.ok) loadStandingOrders();
            else notify(messageFromResponse(res), "error");
        });

        listEl.append(h("div", { class: `account-card glassmorphism so-item${inactive ? " so-inactive" : ""}` },
            inactive ? h("span", { class: "so-inactive-badge", text: t("orders.inactive") }) : null,
            h("div", { class: "so-item-title", text: isCard ? t("orders.titleCard") : t("orders.titleTransfer") }),
            h("div", { class: "so-item-desc", text: description }),
            h("div", { class: "so-item-source", text: t("orders.sourceLabel") + ": " + order.sourceAccountNumber }),
            cancelButton));
    });
}

async function loadStandingOrders() {
    const listEl = byId("standing-orders-list");
    if (!listEl) return;
    const seq = ++requestSeq.orders;
    clearChildren(listEl);
    listEl.append(h("div", { class: "loading-spinner", text: t("orders.loading") }));

    const res = await api("/banking/standing-orders");
    if (seq !== requestSeq.orders) return;

    if (!res.ok || !Array.isArray(res.data)) {
        clearChildren(listEl);
        listEl.append(
            h("div", { class: "alert alert-danger", role: "alert", text: messageFromResponse(res) }),
            h("button", { type: "button", class: "btn btn-secondary btn-sm", text: t("common.retry"), onclick: () => loadStandingOrders() }));
        return;
    }
    standingOrders = res.data;
    renderStandingOrders();
}

function initStandingOrders() {
    const form = byId("standing-order-form");
    if (!form) return;
    const typeSelect = byId("so-type");
    const msgEl = byId("standing-order-message");

    accountsStore.subscribe(fillStandingOrderSources);
    cardsStore.subscribe(fillStandingOrderSources);
    onLanguageChange(() => { renderStandingOrders(); fillStandingOrderSources(); });
    fillStandingOrderSources();

    typeSelect.addEventListener("change", () => {
        const isTransfer = typeSelect.value === "Transfer";
        byId("so-transfer-fields").classList.toggle("hidden", !isTransfer);
        byId("so-cc-fields").classList.toggle("hidden", isTransfer);
    });

    form.addEventListener("submit", event => {
        event.preventDefault();
        withBusy(byId("btn-submit-standing-order"), async () => {
            hideMessage(msgEl);
            const sourceAccountNumber = byId("so-source-acc").value;
            const type = typeSelect.value;
            let destinationAccountNumber = null;
            let amount = null;
            let frequency = null;
            let creditCardId = null;

            if (!sourceAccountNumber) { showMessage(msgEl, t("orders.noSource"), "error"); return; }

            if (type === "Transfer") {
                destinationAccountNumber = normalizeAccountNumber(byId("so-dest-acc").value);
                frequency = byId("so-frequency").value;
                const parsed = parseAmount(byId("so-amount").value, MAX_STANDING_ORDER_AMOUNT);
                if (!destinationAccountNumber) { showMessage(msgEl, t("transfer.noDest"), "error"); byId("so-dest-acc").focus(); return; }
                if (destinationAccountNumber.length < 10 || destinationAccountNumber.length > 30) { showMessage(msgEl, t("transfer.destInvalid"), "error"); byId("so-dest-acc").focus(); return; }
                if (!parsed.ok) { showMessage(msgEl, errorText(parsed.errorKey), "error"); byId("so-amount").focus(); return; }
                amount = parsed.value;
            } else {
                creditCardId = byId("so-target-cc").value;
                frequency = "Monthly";
                if (!creditCardId) { showMessage(msgEl, t("orders.noCard"), "error"); return; }
            }

            const res = await api("/banking/standing-orders", {
                method: "POST",
                body: { sourceAccountNumber, orderType: type, destinationAccountNumber, amount, frequency, creditCardId }
            });
            if (!res.ok) { showMessage(msgEl, messageFromResponse(res), "error"); return; }

            showMessage(msgEl, t("orders.created"), "success");
            byId("so-dest-acc").value = "";
            byId("so-amount").value = "";
            loadStandingOrders();
        });
    });
}

/* ==========================================================================
   2FA setting, sidebar quick action, tabs and the card customizer (dashboard.html)
   ========================================================================== */
let twoFactorEnabled = null; // unknown until the status request answers

async function load2FAStatus() {
    const toggle = byId("switch-2fa");
    if (!toggle) return;
    const retryBox = byId("twofa-retry");
    const res = await api("/auth/2fa-status");
    if (res.ok && res.data && typeof res.data.enabled === "boolean") {
        twoFactorEnabled = res.data.enabled;
        toggle.checked = twoFactorEnabled;
        toggle.disabled = false;
        if (retryBox) retryBox.classList.add("hidden");
        return;
    }
    // The switch stays locked until the real state is known, but it says why and offers a retry.
    if (retryBox) {
        byId("twofa-retry-text").textContent = messageFromResponse(res);
        retryBox.classList.remove("hidden");
    }
}

// Changing the setting needs the 6-digit PIN (the server checks it and answers InvalidCredentials when it is wrong).
function init2FASettings() {
    const toggle = byId("switch-2fa");
    if (!toggle) return;
    toggle.disabled = true; // until the current status is known
    byId("btn-twofa-retry").addEventListener("click", event => withBusy(event.currentTarget, load2FAStatus));

    toggle.addEventListener("click", async event => {
        event.preventDefault(); // the switch only moves once the server accepted the change
        if (twoFactorEnabled === null) return;
        const enable = !twoFactorEnabled;

        const pin = await uiPrompt({
            title: t("twofa.pinTitle"),
            message: t(enable ? "twofa.pinEnable" : "twofa.pinDisable"),
            label: t("twofa.pinLabel"),
            type: "password", inputmode: "numeric", maxlength: 6, autocomplete: "current-password", pattern: "\\d*",
            confirmText: t("common.confirm"),
            validate: value => (/^\d{6}$/.test(value) ? null : t("twofa.pinInvalid")),
            submit: async (value) => {
                const res = await api("/auth/toggle-2fa", { method: "POST", body: { enable, password: value } });
                if (res.ok) return { ok: true };
                // "Invalid T.C. number or password" would be confusing here: the person only typed a password.
                const wrongPin = res.data && res.data.errorKey === "InvalidCredentials";
                return { ok: false, message: wrongPin ? t("twofa.pinWrong") : messageFromResponse(res) };
            }
        });

        if (pin !== null) {
            twoFactorEnabled = enable;
            toggle.checked = enable;
            notify(t(enable ? "twofa.enabled" : "twofa.disabled"), "success");
        }
        toggle.focus();
    });
}

function initSidebar() {
    const toggle = byId("sidebar-toggle");
    const sidebar = byId("sidebar");
    const closeButton = byId("sidebar-close");
    if (!toggle || !sidebar) return;

    const setOpen = (open, returnFocus) => {
        sidebar.classList.toggle("hidden", !open);
        toggle.setAttribute("aria-expanded", open ? "true" : "false");
        if (open) closeButton.focus();
        else if (returnFocus) toggle.focus();
    };
    toggle.addEventListener("click", () => setOpen(sidebar.classList.contains("hidden"), true));
    closeButton.addEventListener("click", () => setOpen(false, true));
    document.addEventListener("keydown", event => {
        if (event.key === "Escape" && !sidebar.classList.contains("hidden") && !modalStack.length) setOpen(false, true);
    });

    // Demo helper: adds 1000 TRY to the first TRY demand account.
    const addButton = byId("btn-add-1000-try");
    const message = byId("add-balance-message");
    addButton.addEventListener("click", () => withBusy(addButton, async () => {
        hideMessage(message);
        const accounts = await accountsStore.get();
        const tryAccount = (accounts || []).find(acc => acc.currency === "TRY");
        if (!tryAccount) { showMessage(message, t("sidebar.noTryAccount"), "error"); return; }

        const res = await api("/banking/deposit", { method: "POST", body: { accountNumber: tryAccount.accountNumber, amount: 1000.00 } });
        if (!res.ok) { showMessage(message, messageFromResponse(res), "error"); return; }

        showMessage(message, t("sidebar.added"), "success");
        await accountsStore.refresh(true);
        if (activeAccountId) loadTransactions(activeAccountId);
    }));
}

function initCardCustomizer() {
    const preview = byId("debit-card-preview");
    const wrapper = byId("debit-card-wrapper-hover");
    const holder = byId("preview-card-holder");

    if (holder && currentUser) {
        const name = displayUpper(currentUser.fullName);
        holder.textContent = name;
        holder.style.fontSize = name.length > 20 ? "0.55rem" : name.length > 15 ? "0.62rem" : "0.75rem";
    }

    document.querySelectorAll(".theme-btn").forEach(btn => {
        btn.addEventListener("click", () => { chosenTheme = btn.dataset.theme; setCardTheme(chosenTheme); });
    });

    // The card flips on hover (CSS), on click and on Enter / Space.
    const flip = () => {
        wrapper.classList.add("flip-touched"); // from now on hover no longer decides (see styles.css), so click and hover cannot fight
        const flipped = wrapper.classList.toggle("flipped");
        wrapper.setAttribute("aria-pressed", flipped ? "true" : "false");
    };
    wrapper.addEventListener("click", flip);
    wrapper.addEventListener("keydown", event => {
        if (event.key === "Enter" || event.key === " ") { event.preventDefault(); flip(); }
    });

    if (preview && !prefersReducedMotion()) {
        preview.addEventListener("mousemove", event => {
            const rect = preview.getBoundingClientRect();
            const x = event.clientX - rect.left;
            const y = event.clientY - rect.top;
            const maxRotate = 15;
            preview.style.transform = `rotateY(${(x / rect.width - 0.5) * maxRotate}deg) rotateX(${-(y / rect.height - 0.5) * maxRotate}deg)`;
            preview.style.setProperty("--mouse-x", `${(x / rect.width) * 100}%`);
            preview.style.setProperty("--mouse-y", `${(y / rect.height) * 100}%`);
        });
        preview.addEventListener("mouseleave", () => { preview.style.transform = "rotateY(0deg) rotateX(0deg)"; });
    }
}

function initDashboard() {
    byId("user-display").textContent = currentUser.fullName || currentUser.username || "";

    // Stores first, so every widget that listens is registered before the first answer arrives.
    accountsStore.subscribe(renderAccounts);
    cardsStore.subscribe(renderCreditCards);
    onLanguageChange(() => {
        if (accountsStore.data) renderAccounts(accountsStore.data);
        renderTransactions();
    });

    initTabList(document.querySelector(".tabs-nav-bar"), tab => {
        if (tab.dataset.tab === "tab-standing-orders") loadStandingOrders();
    });
    initTabList(document.querySelector(".operations-tabs"));

    initHistoryAndSlip();
    initTransferForm();
    initOTPModalEvents();
    initExchangeWidget();
    initSavedContacts();
    initCreditCardEvents();
    initCreateAccountEvent();
    initStandingOrders();
    init2FASettings();
    initCardCustomizer();
    initSidebar();

    loadAccounts();
    loadCreditCards();
    load2FAStatus();
    startMarketRates();
}


/* ==========================================================================
   SUPPORT AGENT PANEL (agent.html)
   chat.js owns the SignalR connection; the functions below call into it (ensureConnected, joinAgentChatSession,
   renderChatHistory, signalRConnection).
   ========================================================================== */
let selectedSessionId = null;   // the one conversation the agent is looking at (chat.js ignores messages for any other)
let activeSessions = [];
let copilotSuggestion = "";
let agentChatBaseTitle = "";    // "customer - title" of the open conversation (a hand-over adds the department)
const unreadSessions = new Set();   // ids of sessions that got a message while another one was open
const closingSessionIds = new Set(); // sessions this agent is closing right now (their SessionClosed event needs no extra notice)

// "customer name - chat title": what the header of an open conversation says (the title alone is the same for every customer).
function sessionDisplayTitle(session) {
    return session.username ? `${session.username} - ${session.title}` : String(session.title ?? "");
}

function renderActiveSessions() {
    const listEl = byId("active-sessions-list");
    if (!listEl) return;
    clearChildren(listEl);

    if (!activeSessions.length) {
        listEl.append(h("div", { class: "text-muted text-center py-4", text: t("agent.noActive") }));
        return;
    }

    activeSessions.forEach(session => {
        const isActive = selectedSessionId === session.id;
        const unread = !isActive && unreadSessions.has(String(session.id));
        const item = h("div", {
            class: `session-item${isActive ? " active" : ""}${unread ? " has-unread" : ""}`, role: "button", tabindex: "0",
            "aria-pressed": isActive ? "true" : "false", dataset: { sessionId: session.id }
        },
            h("h5", { text: session.title }),
            h("p", null, `${t("agent.userLabel")} `, h("strong", { text: session.username }), ` | ${formatTime(session.createdAt)}`));

        const open = () => loadAgentChat(session.id, sessionDisplayTitle(session));
        item.addEventListener("click", open);
        item.addEventListener("keydown", event => {
            if (event.key === "Enter" || event.key === " ") { event.preventDefault(); open(); }
        });
        listEl.append(item);
    });
}

async function loadActiveSessions() {
    const listEl = byId("active-sessions-list");
    if (!listEl) return;
    const seq = ++requestSeq.sessions;
    if (!activeSessions.length) {
        clearChildren(listEl);
        listEl.append(h("div", { class: "loading-spinner", text: t("agent.loadingChats") }));
    }

    const res = await api("/chat/active-sessions");
    if (seq !== requestSeq.sessions) return;

    if (!res.ok || !Array.isArray(res.data)) {
        clearChildren(listEl);
        listEl.append(h("div", { class: "alert alert-danger", role: "alert", text: messageFromResponse(res) }));
        return;
    }
    activeSessions = res.data;
    renderActiveSessions();
}

// Back to the "select a session" state. The room of the conversation that was open is left.
function resetAgentChatView() {
    const previous = selectedSessionId;
    selectedSessionId = null;
    copilotSuggestion = "";
    agentChatBaseTitle = "";
    if (typeof clearChatLog === "function") clearChatLog();
    byId("agent-chat-header").classList.add("hidden");
    byId("agent-chat-form").classList.add("hidden");
    byId("ai-copilot-container").classList.add("hidden");
    const box = byId("agent-chat-messages");
    clearChildren(box);
    // data-i18n keeps these two lines in the right language when the user switches languages
    box.append(h("div", { class: "agent-chat-placeholder", id: "agent-placeholder" },
        h("span", { class: "chat-placeholder-icon", "aria-hidden": "true" }, "📥"),
        h("h3", { "data-i18n": "agent.selectChat", text: t("agent.selectChat") }),
        h("p", { class: "text-muted", "data-i18n": "agent.selectChatDesc", text: t("agent.selectChatDesc") })));
    renderActiveSessions();
    if (previous && typeof leaveAgentChatSession === "function") leaveAgentChatSession(previous);
}

async function loadAgentChat(sessionId, title) {
    const previous = selectedSessionId;
    if (previous && previous !== sessionId && typeof leaveAgentChatSession === "function") leaveAgentChatSession(previous);

    selectedSessionId = sessionId;
    unreadSessions.delete(String(sessionId));
    agentChatBaseTitle = title;
    const seq = ++requestSeq.agentChat;
    clearChatLog();

    byId("agent-chat-header").classList.remove("hidden");
    byId("agent-chat-form").classList.remove("hidden");
    byId("agent-chat-title").textContent = title;
    byId("agent-chat-session-id").textContent = t("agent.sessionId", { id: sessionId });

    document.querySelectorAll("#active-sessions-list .session-item").forEach(item => {
        const on = item.dataset.sessionId === sessionId;
        item.classList.toggle("active", on);
        item.classList.remove("has-unread");
        item.setAttribute("aria-pressed", on ? "true" : "false");
    });

    const box = byId("agent-chat-messages");
    clearChildren(box);
    box.append(h("div", { class: "loading-spinner", text: t("agent.loadingConversation") }));

    // Join the room first and read the history afterwards: a message sent in between is pushed live and kept by renderChatHistory.
    await joinAgentChatSession(sessionId);
    if (seq !== requestSeq.agentChat || selectedSessionId !== sessionId) return; // the agent already opened another chat

    const res = await api(`/chat/messages/${encodeURIComponent(sessionId)}`);
    if (seq !== requestSeq.agentChat || selectedSessionId !== sessionId) return;

    if (!res.ok || !Array.isArray(res.data)) {
        clearChildren(box);
        box.append(h("div", { class: "alert alert-danger", role: "alert", text: messageFromResponse(res) }));
        return;
    }

    renderChatHistory(res.data);
    fetchCoPilotSuggestion(sessionId);
}

function initAgentEvents() {
    byId("btn-refresh-sessions").addEventListener("click", event => withBusy(event.currentTarget, loadActiveSessions));

    byId("btn-close-session").addEventListener("click", event => {
        withBusy(event.currentTarget, async () => {
            const sessionId = selectedSessionId;
            if (!sessionId) return;
            if (!(await uiConfirm(t("agent.closeConfirm"), { danger: true, confirmText: t("agent.closeSession") }))) return;

            // Only report success when the server really closed it.
            closingSessionIds.add(String(sessionId));
            const result = await hubInvoke("CloseSessionAsync", sessionId);
            if (!result.ok) {
                closingSessionIds.delete(String(sessionId));
                notify(result.offline ? t("agent.closeOffline") : (result.message || t("agent.closeFailed")), "error");
                return;
            }
            if (selectedSessionId === sessionId) resetAgentChatView();
            await loadActiveSessions();
            loadAgentMetrics();
            closingSessionIds.delete(String(sessionId));
        });
    });

    const form = byId("agent-chat-form");
    form.addEventListener("submit", event => {
        event.preventDefault();
        withBusy(byId("agent-chat-send-btn"), async () => {
            const input = byId("agent-chat-input");
            const text = input.value.trim();
            const sessionId = selectedSessionId;
            if (!text || !sessionId) return;

            const result = await hubInvoke("SendMessageAsync", sessionId, text);
            if (!result.ok) {
                // The typed reply stays in the box.
                notify(result.offline ? t("chat.offline") : (result.message || t("chat.sendFailed")), "error");
                if (result.sessionClosed && selectedSessionId === sessionId) { resetAgentChatView(); loadActiveSessions(); }
                return;
            }
            input.value = "";
            input.focus();
        });
    });
}

async function loadAgentMetrics() {
    const res = await api("/chat/agent-metrics");
    if (!res.ok || !res.data) return;

    // Real data only: a missing value is shown as an em dash.
    const show = (id, value, formatter) => {
        const el = byId(id);
        if (!el) return;
        el.textContent = value === null || value === undefined || value === "" ? "—" : (formatter ? formatter(value) : String(value));
    };
    show("val-metric-resolved", res.data.resolvedCount, v => formatNumber(v, 0, 0));
    // "42s" from the server: the unit is written in the page language.
    show("val-metric-time", res.data.avgResponseTime, v => String(v).replace(/^(\d+)s$/, (m, seconds) => `${seconds} ${t("unit.seconds")}`));
    show("val-metric-csat", res.data.csatScore);
}

function initAgentStatusControl() {
    const select = byId("agent-status-select");
    const dot = byId("status-indicator-dot");
    if (!select || !dot) return;

    // The status is a local indicator only (the label says "display only"): the API has no endpoint for it, so it changes nothing for customers.
    select.addEventListener("change", () => {
        dot.className = `status-dot ${select.value === "Busy" ? "busy" : select.value === "Break" ? "break" : "online"}`;
    });
}

async function fetchCoPilotSuggestion(sessionId) {
    const container = byId("ai-copilot-container");
    const textEl = byId("ai-suggestion-text");
    if (!container || !textEl) return;

    const seq = ++requestSeq.copilot;
    copilotSuggestion = "";
    container.classList.remove("hidden");
    textEl.textContent = t("agent.copilotLoading");

    const res = await api(`/chat/suggest-response/${encodeURIComponent(sessionId)}`);
    if (seq !== requestSeq.copilot || selectedSessionId !== sessionId) return;

    if (res.ok && res.data && res.data.suggestion) {
        copilotSuggestion = String(res.data.suggestion);
        textEl.textContent = copilotSuggestion;
    } else {
        textEl.textContent = res.networkError ? t("err.ConnectionError") : t("agent.copilotFailed");
    }
}

function initCoPilotEvents() {
    byId("btn-regenerate-suggestion").addEventListener("click", event => {
        if (selectedSessionId) withBusy(event.currentTarget, () => fetchCoPilotSuggestion(selectedSessionId));
    });
    byId("btn-use-suggestion").addEventListener("click", () => {
        if (!copilotSuggestion) return;
        const input = byId("agent-chat-input");
        input.value = copilotSuggestion;
        input.focus();
    });
}

function initTransferControlEvents() {
    const button = byId("btn-transfer-chat");
    const select = byId("select-transfer-dept");

    button.addEventListener("click", () => withBusy(button, async () => {
        const department = select.value;
        const sessionId = selectedSessionId;
        if (!department || !sessionId) return;

        const res = await api(`/chat/transfer-session/${encodeURIComponent(sessionId)}`, { method: "POST", body: { department } });
        select.value = "";
        if (!res.ok) { notify(messageFromResponse(res), "error"); return; }

        const label = select.querySelector(`option[value="${CSS.escape(department)}"]`);
        notify(t("agent.transferred", { dept: label ? label.textContent : department }), "success");
        if (selectedSessionId === sessionId) resetAgentChatView();
        loadActiveSessions();
    }));
}

function initAgentPanel() {
    byId("user-display").textContent = currentUser.fullName || currentUser.username || "";
    onLanguageChange(() => {
        renderActiveSessions();
        if (selectedSessionId) {
            const session = activeSessions.find(item => item.id === selectedSessionId);
            if (session) byId("agent-chat-session-id").textContent = t("agent.sessionId", { id: session.id });
        }
    });

    initAgentEvents();
    initCoPilotEvents();
    initTransferControlEvents();
    initAgentStatusControl();
    loadActiveSessions();
    loadAgentMetrics();
}

/* ==========================================================================
   Live market rates (index.html login page and the exchange widget on the dashboard)
   Polling uses a chained timeout: paused while the tab is hidden, backs off while the API is unreachable,
   and the list is only redrawn when the numbers changed.
   ========================================================================== */
const market = { timer: null, loading: false, failures: 0, signature: "", previous: {} };
const MARKET_POLL_MS = 5000;

function rateDigits(code) {
    return code === "USD" || code === "EUR" ? 4 : 2;
}

function renderMarketRates(rates) {
    const listEl = byId("market-rates-list");
    if (!listEl) return;
    clearChildren(listEl);

    rates.forEach(rate => {
        const key = rate.code;
        const previous = market.previous[key];
        market.previous[key] = rate.sell;

        let direction = "";
        let symbol = "•";
        let flash = "";
        if (previous !== undefined && rate.sell !== previous) {
            direction = rate.sell > previous ? "up" : "down";
            flash = rate.sell > previous ? "flash-green" : "flash-red";
        } else if (rate.change > 0) {
            direction = "up";
        } else if (rate.change < 0) {
            direction = "down";
        }
        if (direction === "up") symbol = "▲";
        else if (direction === "down") symbol = "▼";

        const icon = rate.code === "USD" ? "💵" : rate.code === "EUR" ? "💶" : rate.code === "XAU" ? "🪙" : "🥈";
        const displayName = (currentLanguage === "tr" ? rate.name : rate.nameEn) || rate.name || rate.code;
        const digits = rateDigits(rate.code);

        listEl.append(h("div", { class: "rate-row" },
            h("div", { class: "rate-info" },
                h("div", { class: `rate-symbol-badge ${cssToken(rate.code)}`, "aria-hidden": "true", text: icon }),
                h("div", { class: "rate-name-wrapper" },
                    h("span", { class: "rate-code", text: rate.code }),
                    h("span", { class: "rate-name", text: displayName }),
                    rate.isFallback ? h("span", { class: "indicative-badge", text: t("market.indicative") }) : null)),
            h("div", { class: "rate-prices" },
                h("div", { class: "price-box" },
                    h("span", { class: "price-label", text: t("market.buy") }),
                    h("span", { class: `price-val ${flash}`.trim(), text: formatNumber(rate.buy, digits, digits) })),
                h("div", { class: "price-box" },
                    h("span", { class: "price-label", text: t("market.sell") }),
                    h("span", { class: `price-val ${flash}`.trim(), text: formatNumber(rate.sell, digits, digits) }))),
            h("div", { class: "rate-trend" },
                h("span", { class: `trend-badge ${direction}`.trim(), text: `${symbol} ${formatPercent(Math.abs(rate.change))}` }))));
    });
}

function setMarketOffline(offline) {
    const retry = byId("btn-market-retry");
    const updated = byId("txt-rates-updated");
    if (retry) retry.classList.toggle("hidden", !offline);
    if (offline && updated) updated.textContent = t("market.offline");

    const listEl = byId("market-rates-list");
    if (offline && listEl && !listEl.querySelector(".rate-row")) {
        clearChildren(listEl);
        listEl.append(h("div", { class: "text-muted text-center py-4", text: t("market.unavailable") }));
    }
}

function applyMarketRates(rates) {
    activeMarketRates = rates;
    updateExchangeRateDisplay();

    const updated = byId("txt-rates-updated");
    if (updated) updated.textContent = `${t("market.updated")} ${new Date().toLocaleTimeString(locale())}`;

    const signature = `${currentLanguage}|${JSON.stringify(rates.map(rate => [rate.code, rate.buy, rate.sell, rate.change, !!rate.isFallback]))}`;
    if (signature !== market.signature && byId("market-rates-list")) {
        market.signature = signature;
        renderMarketRates(rates);
    }
}

function scheduleMarketPoll(delay) {
    clearTimeout(market.timer);
    market.timer = setTimeout(pollMarketRates, delay);
}

async function pollMarketRates() {
    clearTimeout(market.timer); // a manual Retry must not leave the scheduled poll running next to the new chain
    market.timer = null;
    if (document.hidden || market.loading) return; // the visibilitychange handler restarts polling
    market.loading = true;

    const res = await api("/market/rates", { auth: false, timeoutMs: 10000 });
    market.loading = false;

    if (res.ok && Array.isArray(res.data)) {
        market.failures = 0;
        setMarketOffline(false);
        applyMarketRates(res.data);
        scheduleMarketPoll(MARKET_POLL_MS);
    } else {
        market.failures += 1;
        setMarketOffline(true);
        scheduleMarketPoll(Math.min(MARKET_POLL_MS * 2 ** market.failures, 60000));
    }
}

function startMarketRates() {
    document.addEventListener("visibilitychange", () => {
        if (document.hidden) {
            clearTimeout(market.timer);
            market.timer = null;
        } else if (!market.timer && !market.loading) {
            pollMarketRates();
        }
    });
    const retry = byId("btn-market-retry");
    if (retry) retry.addEventListener("click", () => { market.failures = 0; pollMarketRates(); });
    onLanguageChange(() => { if (activeMarketRates.length) applyMarketRates(activeMarketRates); });
    pollMarketRates();
}

/* ==========================================================================
   Start-up: language toggle, sign-out button and the route guards of each page
   ========================================================================== */
document.addEventListener("DOMContentLoaded", () => {
    translatePage();
    ensureToastRegion();

    const langBtn = byId("lang-toggle");
    if (langBtn) langBtn.addEventListener("click", () => setLanguage(currentLanguage === "en" ? "tr" : "en"));
    const logoutBtn = byId("btn-logout");
    if (logoutBtn) logoutBtn.addEventListener("click", () => logout());

    if (PAGE === "dashboard") {
        if (!currentToken || !currentUser) { logout({ remote: false }); return; }
        if (currentUser.role === "Agent") { window.location.replace("agent.html"); return; }
        initDashboard();
    } else if (PAGE === "agent") {
        if (!currentToken || !currentUser) { logout({ remote: false }); return; }
        if (currentUser.role !== "Agent") {
            // Not a support agent: nothing here would load anyway (the API refuses), so do not show the page.
            window.location.replace("dashboard.html");
            return;
        }
        initAgentPanel();
    } else if (PAGE === "login") {
        if (currentToken && currentUser) { redirectByUserRole(); return; }
        initAuthEvents();
        startMarketRates();
    }
});
