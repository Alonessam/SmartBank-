/* ==========================================================================
   SmartBank Real-time Chat Wrapper - chat.js
   Manages the SignalR WebSocket for customers (floating widget on dashboard.html) and support agents (agent.html).
   Loaded after app.js, which provides the shared helpers (api, t, h, notify, withBusy, ...) and the page constants.
   ========================================================================== */
"use strict";

let signalRConnection = null;
let activeChatSessionId = null;    // customer widget: the session this browser tab is in
let connectPromise = null;
let reconnectTimer = null;
let reconnectDelay = 5000;
let tokenTimer = null;             // reconnects with a fresh access token shortly before the current one expires
let everConnected = false;         // true after the first successful connection: later ones backfill what was missed
let pendingChatTransfer = null;    // what the one-time code dialog confirms after a failed chat transfer
let chatCodeDialogOpen = false;
let transferWaiter = null;         // resolves with the next transfer answer (success / failure) of the open conversation

// Everything currently drawn in this page's message log, so a language switch can draw it again.
// Entries: { msg, historical, resolved } for chat messages, { kind: "note", noteKey } and { kind: "start" }.
let chatLog = [];

// Error events the hub sent: "Error" is an event, not an exception, so hubInvoke() counts them to tell a refused call from an accepted one.
const hubErrors = { count: 0, last: "", inflight: 0 };

const ALLOWED_ROLES = new Set(["user", "ai", "agent", "system"]);

// The hub answers with fixed English sentences; they are mapped to translated texts by their beginning.
const HUB_ERROR_KEYS = [
    ["You have started too many chats", "hub.tooManyChats"],
    ["Could not start support session", "hub.startFailed"],
    ["Access denied to this chat session", "hub.accessDenied"],
    ["Message cannot be empty", "hub.empty"],
    ["Message is too long", "hub.tooLong"],
    ["You are sending messages too fast", "hub.tooFast"],
    ["Could not send message", "hub.sendFailed"],
    ["This chat session has been closed", "hub.sessionClosed"],
    ["Unauthorized", "hub.unauthorized"],
    ["Too many transfer attempts", "hub.tooManyTransfers"],
    ["Could not close session", "hub.closeFailed"],
    ["Support agent role required", "hub.agentRequired"],
    ["Session expired", "hub.expired"]
];
const SESSION_CLOSED_TEXT = "This chat session has been closed";

function hubErrorText(message) {
    const text = String(message ?? "");
    const match = HUB_ERROR_KEYS.find(([prefix]) => text.includes(prefix));
    return t(match ? match[1] : "hub.generic");
}

function chatContainer() {
    return byId(IS_AGENT_PAGE ? "agent-chat-messages" : "chat-messages");
}

function viewedSessionId() {
    return IS_AGENT_PAGE ? selectedSessionId : activeChatSessionId;
}

/* ---------- connection ---------- */
function setConnectionStatus(state) {
    const el = byId(IS_AGENT_PAGE ? "agent-conn-status" : "chat-conn-status");
    if (!el) return;
    el.classList.toggle("hidden", state === "connected");
    el.textContent = state === "reconnecting" ? t("chat.reconnecting") : state === "offline" ? t("chat.offline") : "";
    el.dataset.state = state;
}

function buildConnection() {
    const connection = new signalR.HubConnectionBuilder()
        // The factory runs on every (re)connect, so a reconnect after the access token expired gets a fresh one.
        .withUrl(`${HUBS_URL}/support`, { accessTokenFactory: () => ensureFreshAccessToken() })
        .withAutomaticReconnect()
        // The default level (Information) prints the WebSocket address to the console, and the token travels in it.
        .configureLogging(signalR.LogLevel.Warning)
        .build();

    registerHandlers(connection);

    connection.onreconnecting(() => setConnectionStatus("reconnecting"));
    // Groups live on the server connection: after a reconnect this tab has to join them again and read what it missed.
    connection.onreconnected(async () => {
        setConnectionStatus("connected");
        await rejoinGroups();
        scheduleTokenReconnect();
        resyncAfterReconnect();
    });
    connection.onclose(() => {
        if (signalRConnection !== connection) return; // closed on purpose (sign-out, token renewal)
        setConnectionStatus("offline");
        scheduleReconnect();
    });
    return connection;
}

async function rejoinGroups() {
    const connection = signalRConnection;
    if (!connection || connection.state !== "Connected") return;
    try {
        if (IS_AGENT_PAGE) {
            await connection.invoke("RegisterAgentAsync");
            if (selectedSessionId) await connection.invoke("JoinSessionAsync", selectedSessionId);
        } else if (activeChatSessionId) {
            await connection.invoke("JoinSessionAsync", activeChatSessionId);
        }
    } catch (err) {
        console.error("Could not join the chat groups:", err);
    }
}

async function connectSignalR() {
    if (typeof signalR === "undefined") { setConnectionStatus("offline"); return false; } // script blocked or offline

    if (!signalRConnection) signalRConnection = buildConnection();
    const connection = signalRConnection;
    try {
        const deadline = Date.now() + 30000;
        while (connection.state !== "Connected" && Date.now() < deadline) {
            if (connection.state === "Disconnected") await connection.start();
            else await new Promise(resolve => setTimeout(resolve, 250)); // Connecting / Reconnecting: wait for the outcome
        }
        if (connection.state !== "Connected") throw new Error(`state ${connection.state}`);
        setConnectionStatus("connected");
        await rejoinGroups();
        scheduleTokenReconnect();
        const wasConnectedBefore = everConnected;
        everConnected = true;
        if (wasConnectedBefore) resyncAfterReconnect();
        return true;
    } catch (err) {
        console.error("SignalR connection failed:", err);
        setConnectionStatus("offline");
        return false;
    }
}

// One connection attempt at a time; resolves to true when connected.
function ensureConnected() {
    if (signalRConnection && signalRConnection.state === "Connected") return Promise.resolve(true);
    if (!connectPromise) connectPromise = connectSignalR().finally(() => { connectPromise = null; });
    return connectPromise;
}

function scheduleReconnect() {
    if (reconnectTimer || !signalRConnection) return;
    reconnectTimer = setTimeout(async () => {
        reconnectTimer = null;
        if (await ensureConnected()) {
            reconnectDelay = 5000;
        } else {
            reconnectDelay = Math.min(reconnectDelay * 2, 60000);
            scheduleReconnect();
        }
    }, reconnectDelay);
}

// The hub refuses calls made after the access token's expiry ("Session expired") although the socket stays open, and the token
// is only read when a connection starts. So the connection is replaced by a new one that asks the token factory again.
async function reconnectFresh() {
    if (connectPromise) { try { await connectPromise; } catch (err) { /* the outcome is checked below */ } }
    const old = signalRConnection;
    signalRConnection = null; // its onclose handler ignores a connection that is no longer the current one
    if (old) { try { await old.stop(); } catch (err) { /* already closed */ } }
    return ensureConnected();
}

// A new connection (with a fresh token) shortly before the current token expires, so an idle tab never meets "Session expired".
function scheduleTokenReconnect() {
    clearTimeout(tokenTimer);
    tokenTimer = null;
    const expiresMs = Number(safeStorage.get("tokenExpiresAt")) || 0;
    if (!expiresMs || !signalRConnection) return;

    const delay = Math.min(Math.max(expiresMs - Date.now() - 20000, 5000), 2000000000);
    tokenTimer = setTimeout(async () => {
        tokenTimer = null;
        if (!signalRConnection) return;
        if (!(await reconnectFresh())) scheduleReconnect();
    }, delay);
}

// Called by logout().
function stopSignalRConnection() {
    clearTimeout(reconnectTimer);
    clearTimeout(tokenTimer);
    reconnectTimer = null;
    tokenTimer = null;
    everConnected = false;
    if (transferWaiter) transferWaiter.resolve(null);
    const connection = signalRConnection;
    signalRConnection = null;
    activeChatSessionId = null;
    if (connection) connection.stop().catch(() => {});
}

// Calls a hub method and tells what happened: { ok: true } or { ok: false, message, raw, offline, sessionClosed }.
//  - a token that is (nearly) expired, or an answer "Session expired", replaces the connection and the call is repeated once;
//  - the hub reports refused calls (too fast, access denied, ...) with an "Error" event that arrives BEFORE the call completes
//    (one connection delivers in order): such a call counts as failed, so callers keep the typed text and the open view.
async function hubInvoke(method, ...args) {
    const offline = { ok: false, offline: true, message: t("chat.offline") };

    for (let attempt = 0; attempt < 2; attempt++) {
        const connected = signalRConnection && signalRConnection.state === "Connected";
        if (attempt === 0 && connected && tokenIsExpiring()) {
            if (!(await reconnectFresh())) return offline;
        } else if (!(await ensureConnected())) {
            return offline;
        }

        const connection = signalRConnection;
        if (!connection) return offline;

        const errorsBefore = hubErrors.count;
        hubErrors.inflight += 1;
        try {
            await connection.invoke(method, ...args);
        } catch (err) {
            const raw = String((err && err.message) || "");
            if (attempt === 0 && /Session expired/i.test(raw)) {
                if (!(await reconnectFresh())) return offline;
                continue;
            }
            return { ok: false, raw, message: hubErrorText(raw) };
        } finally {
            hubErrors.inflight -= 1;
        }

        if (hubErrors.count > errorsBefore) {
            const raw = hubErrors.last;
            return { ok: false, raw, message: hubErrorText(raw), sessionClosed: raw.includes(SESSION_CLOSED_TEXT) };
        }
        return { ok: true };
    }
    return { ok: false, message: t("hub.expired") };
}

// After a reconnect (or a replaced connection) whatever was said meanwhile is read again: the open conversation, and for an agent
// the queue of waiting customers.
async function resyncAfterReconnect() {
    if (IS_AGENT_PAGE && typeof loadActiveSessions === "function") loadActiveSessions();
    const sessionId = viewedSessionId();
    if (!sessionId) return;

    const res = await api(`/chat/messages/${encodeURIComponent(sessionId)}`);
    if (String(sessionId) !== String(viewedSessionId())) return;
    if (res.ok && Array.isArray(res.data)) renderChatHistory(res.data);
}

function hideTyping() {
    const indicator = byId("chat-typing-indicator");
    if (indicator) indicator.classList.add("hidden");
}

function markUnread(sessionId) {
    unreadSessions.add(String(sessionId));
    document.querySelectorAll("#active-sessions-list .session-item").forEach(item => {
        if (item.dataset.sessionId === String(sessionId)) item.classList.add("has-unread");
    });
}

// Customer widget: a reply that arrives while the window is closed puts a dot on the button (and says so to screen readers).
function showCustomerUnread() {
    const box = byId("chat-box");
    const dot = byId("chat-unread-dot");
    if (!box || !dot || !box.classList.contains("hidden")) return;
    dot.classList.remove("hidden");
    byId("chat-unread-live").textContent = t("chat.unread");
}

function clearCustomerUnread() {
    const dot = byId("chat-unread-dot");
    if (dot) dot.classList.add("hidden");
    const live = byId("chat-unread-live");
    if (live) live.textContent = "";
}

// The customer's conversation is over (the agent closed it, or the server says it is closed): back to the "start" state.
function endCustomerSession() {
    activeChatSessionId = null;
    safeSession.remove("activeChatSessionId");
    hideTyping();
    setupChatState(false);
    chatLog.push({ kind: "note", noteKey: "chat.sessionClosed" }, { kind: "start" });
    renderChatLog();
}

// What a transfer answer in the chat means: null for every other message.
function transferOutcomeOf(msgDto) {
    if (String(msgDto.sender || "").toLowerCase() !== "system") return null;
    const content = String(msgDto.content ?? "");
    const succeeded = content.includes("[TRANSFER_SUCCESS:");
    const failed = content.includes("[TRANSFER_FAILED:");
    if (succeeded) return { success: true };
    if (failed) {
        const key = content.match(/errorKey=([^,\s\]]+)/);
        const message = content.match(/message=([^\]]+)/);
        return { success: false, errorKey: key ? key[1] : "TransactionFailed", rawMessage: message ? message[1] : "" };
    }
    return null;
}

function needsOneTimeCode(errorKey) {
    return errorKey === "Requires2FA" || errorKey === "SuspectedFraudDuplicate" || errorKey === "SuspectedFraudHighValue";
}

function waitForTransferOutcome() {
    return new Promise(resolve => {
        const timer = setTimeout(() => { transferWaiter = null; resolve(null); }, 20000);
        transferWaiter = { resolve: value => { clearTimeout(timer); transferWaiter = null; resolve(value); } };
    });
}

// The one-time code dialog of a chat transfer. It stays open until the server has answered: a wrong code is shown in the dialog
// and can be corrected; only success closes it.
function openChatCodeDialog(errorKey, rawMessage) {
    const transfer = pendingChatTransfer;
    if (!transfer || chatCodeDialogOpen) return;
    chatCodeDialogOpen = true;
    let finished = false;

    showOtpToast(splitOtpMarker(rawMessage).otp);
    showOTPModal(errorText(errorKey), async code => {
        const answer = waitForTransferOutcome();
        const result = await hubInvoke("ConfirmTransferFromChatAsync", activeChatSessionId,
            transfer.source, transfer.destination, transfer.amount, transfer.description, code);
        if (!result.ok) {
            if (transferWaiter) transferWaiter.resolve(null);
            return { success: false, message: result.message || t("chat.confirmFailed") };
        }
        const outcome = await answer;
        if (outcome && outcome.success) { finished = true; return { success: true }; }
        return { success: false, message: outcome ? errorText(outcome.errorKey) : t("chat.confirmFailed") };
    }, () => {
        chatCodeDialogOpen = false;
        if (finished) return;
        // Closed without a completed transfer: the confirmation card can be used again.
        chatLog.forEach(entry => { if (entry.resolved === "processing") entry.resolved = null; });
        renderChatLog();
    });
}

function registerHandlers(connection) {
    // The server refuses empty or oversized messages and too many messages in a row, and says so here. A refusal that answers a
    // call of this page is reported by hubInvoke() to the caller; one that arrives on its own is shown as a message.
    connection.on("Error", message => {
        hubErrors.count += 1;
        hubErrors.last = String(message ?? "");
        if (hubErrors.inflight === 0) notify(hubErrorText(message), "error");
    });

    // Nothing to draw for these two, but without a handler the client logs a warning for each.
    connection.on("UserJoined", () => {});
    connection.on("AgentRegistered", () => {});

    connection.on("ReceiveMessage", msgDto => {
        if (!msgDto) return;

        // Only the conversation on screen is drawn. Every message carries its sessionId; a server that sends none is read as before
        // (everything belongs to the open conversation).
        const viewed = viewedSessionId();
        const messageSession = msgDto.sessionId ? String(msgDto.sessionId) : "";
        if (!viewed || (messageSession && messageSession !== String(viewed))) {
            if (IS_AGENT_PAGE && messageSession) markUnread(messageSession);
            return;
        }

        if (!IS_AGENT_PAGE && (msgDto.sender === "AI" || msgDto.sender === "Agent")) {
            hideTyping();
            showCustomerUnread();
        }

        // A transfer answer settles the confirmation card that was waiting for it. A code challenge keeps the card waiting: the
        // code dialog finishes it (done) or gives the card back (see openChatCodeDialog).
        const outcome = IS_AGENT_PAGE ? null : transferOutcomeOf(msgDto);
        if (outcome && (outcome.success || (!needsOneTimeCode(outcome.errorKey) && !chatCodeDialogOpen))) {
            chatLog.forEach(entry => { if (entry.resolved === "processing") entry.resolved = outcome.success ? "done" : null; });
        }

        addChatEntry({ msg: msgDto, historical: false });

        if (outcome) {
            if (transferWaiter) transferWaiter.resolve(outcome);
            if (!outcome.success && needsOneTimeCode(outcome.errorKey)) openChatCodeDialog(outcome.errorKey, outcome.rawMessage);
            renderChatLog(); // the cards that were "processing" are live again (or done)
        }

        // AI Co-Pilot: suggest an answer to every new customer message
        if (IS_AGENT_PAGE && msgDto.sender === "User" && typeof fetchCoPilotSuggestion === "function") {
            fetchCoPilotSuggestion(selectedSessionId);
        }
    });

    connection.on("AgentTyping", senderRole => {
        if (IS_AGENT_PAGE || (senderRole !== "AI" && senderRole !== "Agent")) return;
        const indicator = byId("chat-typing-indicator");
        if (!indicator) return;
        indicator.classList.remove("hidden");
        const box = chatContainer();
        if (box) box.scrollTop = box.scrollHeight;
    });

    connection.on("AgentStopTyping", senderRole => {
        if (!IS_AGENT_PAGE && (senderRole === "AI" || senderRole === "Agent")) hideTyping();
    });

    connection.on("SessionStarted", sessionDto => {
        activeChatSessionId = String(sessionDto.id);
        safeSession.set("activeChatSessionId", activeChatSessionId);
        setupChatState(true);
        loadSessionMessages(activeChatSessionId);
    });

    connection.on("SessionClosed", sessionId => {
        if (IS_AGENT_PAGE) {
            const closedHere = closingSessionIds.has(String(sessionId));
            if (String(sessionId) === String(selectedSessionId)) {
                resetAgentChatView();
                if (!closedHere) notify(t("chat.sessionClosed"), "info");
            }
            unreadSessions.delete(String(sessionId));
            loadActiveSessions();
            return;
        }
        if (String(sessionId) !== String(activeChatSessionId)) return;
        endCustomerSession();
        showCustomerUnread();
    });

    connection.on("NewSessionRequest", () => {
        // An agent reloads the list of sessions as soon as a customer asks for help.
        if (IS_AGENT_PAGE) loadActiveSessions();
    });
}

/* ---------- chat log ---------- */
function setupChatState(isActive) {
    const chatInput = byId("chat-input");
    const chatSendBtn = byId("chat-send-btn");
    if (!chatInput || !chatSendBtn) return;
    chatInput.disabled = !isActive;
    chatSendBtn.disabled = !isActive;
    if (isActive) chatInput.focus();
}

// Forgets what the log showed (the agent panel went back to its "select a session" state, or opens another conversation).
function clearChatLog() {
    chatLog = [];
}

function renderChatLog() {
    const box = chatContainer();
    if (!box) return;
    clearChildren(box);
    chatLog.forEach(entry => renderEntry(entry, box));
    box.scrollTop = box.scrollHeight;
}

function messageIdOf(entry) {
    return entry && entry.msg && entry.msg.id ? String(entry.msg.id) : "";
}

// Replaces the log with a stored conversation. Its cards are inert: a confirmation from the past can never be confirmed again.
// Entries that are already on screen keep their state (a live card stays live), and a message that arrived live while the history
// was being read (and is not in it yet) is kept at the end.
function renderChatHistory(messages) {
    const known = new Map();
    chatLog.forEach(entry => { const id = messageIdOf(entry); if (id) known.set(id, entry); });

    const stored = (Array.isArray(messages) ? messages : []).map(msg => known.get(String(msg.id)) || { msg, historical: true });
    const storedIds = new Set(stored.map(messageIdOf));
    const liveOnly = chatLog.filter(entry => entry.msg && !entry.historical && !storedIds.has(messageIdOf(entry)));

    chatLog = stored.concat(liveOnly);
    if (!chatLog.length) chatLog.push({ kind: "note", noteKey: "chat.started" });
    renderChatLog();
}

function addChatEntry(entry) {
    const box = chatContainer();
    if (!box) return;
    const id = messageIdOf(entry);
    if (id && chatLog.some(existing => messageIdOf(existing) === id)) return; // pushed and read from history: once is enough
    chatLog.push(entry);
    const nearBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 80;
    renderEntry(entry, box);
    if (nearBottom) box.scrollTop = box.scrollHeight;
}

async function loadSessionMessages(sessionId) {
    const box = byId("chat-messages");
    clearChatLog(); // a new conversation starts with an empty log
    clearChildren(box);
    box.append(h("div", { class: "loading-spinner", text: t("chat.loadingMessages") }));

    const res = await api(`/chat/messages/${encodeURIComponent(sessionId)}`);
    if (String(sessionId) !== String(activeChatSessionId)) return;

    if (!res.ok || !Array.isArray(res.data)) {
        clearChildren(box);
        box.append(h("div", { class: "text-muted", text: t("chat.historyFailed") }));
        return;
    }
    renderChatHistory(res.data);
}

function cardStatus(text) {
    return h("div", { class: "confirm-note", text });
}

function renderEntry(entry, container) {
    if (entry.kind === "note") {
        container.append(h("div", { class: "text-muted text-center py-2 text-xs", text: t(entry.noteKey) }));
        return;
    }
    if (entry.kind === "start") {
        container.append(h("div", { class: "chat-placeholder" },
            h("button", { type: "button", class: "btn btn-secondary btn-sm", text: t("chat.start"), onclick: event => startCustomerSession(event.currentTarget) })));
        return;
    }

    const msgDto = entry.msg;
    const time = formatTime(msgDto.createdAt);
    const content = String(msgDto.content ?? "");

    // Machine markers become cards only when they come from the right sender. A customer or agent who types
    // "[TRANSFER_SUCCESS: ...]" must get plain text, and the server also neutralises such text (see docs/DEFENSE.md, T13).
    const sender = String(msgDto.sender || "").toLowerCase();
    const fromSystem = sender === "system";
    const fromAi = sender === "ai";

    // 0. Room transfer
    if (fromSystem && content.includes("[SESSION_TRANSFERRED:")) {
        const toMatch = content.match(/to=([^\]]+)/);
        const department = toMatch ? toMatch[1].trim() : "";
        const deptKeys = {
            "General Support": "agent.deptGeneral", "Genel Destek": "agent.deptGeneral",
            "Loans Department": "agent.deptLoans", "Kredi Departmanı": "agent.deptLoans",
            "Card Services": "agent.deptCards", "Kart Hizmetleri": "agent.deptCards",
            "Investment Advisory": "agent.deptInvestments", "Yatırım Danışmanlığı": "agent.deptInvestments"
        };
        const displayDept = Object.prototype.hasOwnProperty.call(deptKeys, department) ? t(deptKeys[department]) : (department || t("agent.deptGeneral"));

        container.append(h("div", { class: "system-status-card success" },
            h("div", { class: "system-status-title" }, "🔄 ", t("chat.transferredTitle")),
            h("div", null, t("chat.transferredDesc", { dept: displayDept })),
            h("span", { class: "message-timestamp", text: time })));

        // The customer's name stays in the header; the department is added to it.
        if (IS_AGENT_PAGE && !entry.historical && String(msgDto.sessionId || selectedSessionId) === String(selectedSessionId) && !entry.titled) {
            entry.titled = true;
            byId("agent-chat-title").textContent = agentChatBaseTitle ? agentChatBaseTitle + " (" + displayDept + ")" : displayDept;
        }
        return;
    }

    // 1. Transfer confirmation widget (written by the AI assistant)
    if (fromAi && content.includes("[CONFIRM_TRANSFER:")) {
        const field = (pattern, fallback) => { const m = content.match(pattern); return m ? m[1].trim() : fallback; };
        const source = field(/source=([^,\s\]]+)/, "");
        const destination = field(/destination=([^,\s\]]+)/, "");
        const description = field(/description=([^\]]+)/, "-");
        const amountText = field(/amount=([^,\s\]]+)/, "0");
        const parsedAmount = parseAmount(amountText);

        // Only a live card on the customer's page can be confirmed, once, with an amount that is shown exactly as it is sent.
        const inert = IS_AGENT_PAGE || entry.historical || !!entry.resolved || !parsedAmount.ok;
        const yes = h("button", { type: "button", class: "btn-confirm btn-confirm-yes", text: t("chat.confirm"), disabled: inert });
        const no = h("button", { type: "button", class: "btn-confirm btn-confirm-no", text: t("common.cancel"), disabled: inert });

        const card = h("div", { class: `transfer-confirm-card${inert ? " historical" : ""}` },
            h("div", { class: "transfer-confirm-title" }, h("span", { class: "logo-icon", "aria-hidden": "true" }, "🔄"), ` ${t("chat.confirmTitle")}`),
            h("div", { class: "transfer-confirm-item" }, `${t("chat.sourceAccount")}: `, h("strong", { text: source })),
            h("div", { class: "transfer-confirm-item" }, `${t("chat.destAccount")}: `, h("strong", { text: destination })),
            h("div", { class: "transfer-confirm-item" }, `${t("transfer.description")}: `, h("strong", { text: description })),
            h("div", { class: "transfer-confirm-amount", text: parsedAmount.ok ? formatMoney(parsedAmount.value, "TRY") : `${amountText} TRY` }),
            h("div", { class: "transfer-confirm-actions" }, yes, no));

        if (entry.resolved === "cancelled") {
            card.append(cardStatus(t("chat.cancelled")));
        } else if (entry.resolved === "processing") {
            card.append(cardStatus(t("chat.processing")));
        } else if (entry.resolved === "done") {
            card.append(cardStatus(t("chat.done")));
        } else if (entry.historical && !IS_AGENT_PAGE) {
            card.append(cardStatus(t("chat.confirmExpired")));
        } else if (!parsedAmount.ok && !IS_AGENT_PAGE) {
            card.append(cardStatus(t("chat.confirmInvalid")));
        }

        if (!inert) {
            yes.addEventListener("click", async () => {
                entry.resolved = "processing";
                yes.disabled = true;
                no.disabled = true;
                yes.textContent = t("chat.processing");
                pendingChatTransfer = { source, destination, amount: parsedAmount.value, description };

                // The last argument is the one-time code (none yet): the hub has six parameters and SignalR does not fill in defaults.
                const result = await hubInvoke("ConfirmTransferFromChatAsync", activeChatSessionId, source, destination, parsedAmount.value, description, null);
                if (!result.ok) {
                    // Give the customer the buttons back.
                    entry.resolved = null;
                    yes.disabled = false;
                    no.disabled = false;
                    yes.textContent = t("chat.confirm");
                    notify(result.offline ? t("chat.confirmFailed") : (result.message || t("chat.confirmFailed")), "error");
                }
            });
            no.addEventListener("click", () => {
                entry.resolved = "cancelled";
                yes.disabled = true;
                no.disabled = true;
                card.classList.add("historical");
                card.append(cardStatus(t("chat.cancelled")));
            });
        }

        container.append(card);
        return;
    }

    // 2. Transfer success
    if (fromSystem && content.includes("[TRANSFER_SUCCESS:")) {
        const amountMatch = content.match(/amount=([^,\s\]]+)/);
        const destMatch = content.match(/destination=([^,\s\]]+)/);
        const amount = amountMatch ? amountMatch[1] : "0";
        const destination = destMatch ? destMatch[1] : "";
        const parsed = parseAmount(amount);

        container.append(h("div", { class: "system-status-card success" },
            h("div", { class: "system-status-title" }, "✅ ", t("chat.successTitle")),
            h("div", null, t("chat.successDesc", { amount: parsed.ok ? formatMoney(parsed.value, "TRY") : `${amount} TRY`, dest: destination })),
            h("span", { class: "message-timestamp", text: time })));

        // Once per message: drawing the log again (language switch) must not celebrate again.
        if (!IS_AGENT_PAGE && !entry.historical && !entry.celebrated) {
            entry.celebrated = true;
            celebrate();
            accountsStore.refresh(true).then(() => { if (activeAccountId) loadTransactions(activeAccountId); });
        }
        return;
    }

    // 3. Transfer failed
    if (fromSystem && content.includes("[TRANSFER_FAILED:")) {
        const errorKeyMatch = content.match(/errorKey=([^,\s\]]+)/);
        const errorKey = errorKeyMatch ? errorKeyMatch[1] : "TransactionFailed";

        // A live "needs a one-time code" answer is handled by the code dialog (opened when the message arrived): no red card for it.
        if (needsOneTimeCode(errorKey) && !IS_AGENT_PAGE && !entry.historical) return;

        container.append(h("div", { class: "system-status-card failed" },
            h("div", { class: "system-status-title" }, "❌ ", t("chat.failedTitle")),
            h("div", { text: errorText(errorKey) }),
            h("span", { class: "message-timestamp", text: time })));
        return;
    }

    // 4. Default message bubble (the role becomes a CSS class only from an allow-list)
    const role = ALLOWED_ROLES.has(sender) ? sender : "system";
    container.append(h("div", { class: `message-bubble ${role}` }, content, h("span", { class: "message-timestamp", text: time })));
}

/* ---------- customer widget ---------- */
async function startCustomerSession(button) {
    await withBusy(button, async () => {
        // The first connection can take a while when the free host is asleep: after a few seconds the person is told why.
        const result = await withWakeHint(() => hubInvoke("StartSessionAsync", "Customer Support"));
        if (!result.ok) notify(result.offline ? t("chat.offline") : (result.message || t("chat.startFailed")), "error");
    });
}

// Opening the widget again after a page reload: the stored session is checked with the API first.
async function restoreChatSession() {
    const saved = safeSession.get("activeChatSessionId");
    if (!saved) return;

    const res = await api(`/chat/messages/${encodeURIComponent(saved)}`);
    if (!res.ok || !Array.isArray(res.data)) {
        safeSession.remove("activeChatSessionId"); // not this user's session any more
        return;
    }
    activeChatSessionId = saved;
    setupChatState(true);
    renderChatHistory(res.data);

    // Join the session group so replies arrive live again, then read the history once more: what was said between the first
    // read and the join would otherwise be lost.
    if (await ensureConnected()) {
        const again = await api(`/chat/messages/${encodeURIComponent(saved)}`);
        if (String(saved) === String(activeChatSessionId) && again.ok && Array.isArray(again.data)) renderChatHistory(again.data);
    }
}

function initCustomerChat() {
    const toggle = byId("chat-toggle");
    const box = byId("chat-box");
    const closeButton = byId("chat-close");
    const form = byId("chat-form");
    let restored = false;

    const setOpen = async (open) => {
        box.classList.toggle("hidden", !open);
        toggle.setAttribute("aria-expanded", open ? "true" : "false");
        if (!open) { toggle.focus(); return; }
        clearCustomerUnread();
        if (!restored) { restored = true; await restoreChatSession(); }
        const input = byId("chat-input");
        const focusTarget = !input.disabled ? input : box.querySelector("#btn-start-chat, .chat-placeholder button");
        if (focusTarget) focusTarget.focus();
    };

    toggle.addEventListener("click", () => setOpen(box.classList.contains("hidden")));
    closeButton.addEventListener("click", () => setOpen(false));
    box.addEventListener("keydown", event => {
        if (event.key === "Escape" && !modalStack.length && !box.classList.contains("hidden")) setOpen(false);
    });
    byId("btn-start-chat").addEventListener("click", event => startCustomerSession(event.currentTarget));

    form.addEventListener("submit", event => {
        event.preventDefault();
        withBusy(byId("chat-send-btn"), async () => {
            const input = byId("chat-input");
            const text = input.value.trim();
            if (!text || !activeChatSessionId) return;

            const result = await hubInvoke("SendMessageAsync", activeChatSessionId, text);
            if (!result.ok) {
                // The typed text stays in the box. A conversation the server calls closed goes back to the start state.
                if (result.sessionClosed) endCustomerSession();
                notify(result.offline ? t("chat.offline") : (result.message || t("chat.sendFailed")), "error");
                return;
            }
            input.value = "";
            input.focus();
        });
    });
}

// The agent panel joins the room of the session it opens and leaves the room of the one it puts down (chat.js draws only the
// open one, and the server no longer pushes the others). Resolves to true when the join worked.
async function joinAgentChatSession(sessionId) {
    const result = await hubInvoke("JoinSessionAsync", sessionId);
    if (!result.ok) console.error("Could not join the session:", result.raw || result.message);
    return result.ok;
}

// Failures are ignored: a server without LeaveSessionAsync still works (messages of other sessions are filtered by sessionId).
async function leaveAgentChatSession(sessionId) {
    try { await hubInvoke("LeaveSessionAsync", sessionId); } catch (err) { /* nothing to undo */ }
}

document.addEventListener("DOMContentLoaded", () => {
    onLanguageChange(() => {
        if (chatLog.length) renderChatLog();
        const state = byId(IS_AGENT_PAGE ? "agent-conn-status" : "chat-conn-status");
        if (state && state.dataset.state) setConnectionStatus(state.dataset.state);
    });

    if (IS_DASHBOARD_PAGE && currentToken) initCustomerChat();

    // The agent panel connects at once: new customer requests arrive through the "Agents" group.
    if (IS_AGENT_PAGE && currentToken && currentUser && currentUser.role === "Agent") {
        ensureConnected().then(ok => { if (!ok) scheduleReconnect(); });
    }
});
