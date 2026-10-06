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
let pendingChatTransfer = null;    // what the one-time code dialog confirms after a failed chat transfer

// Everything currently drawn in this page's message log, so a language switch can draw it again.
// Entries: { msg, historical, resolved } for chat messages, { kind: "note", noteKey } and { kind: "start" }.
let chatLog = [];

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
    ["Unauthorized", "hub.unauthorized"],
    ["Too many transfer attempts", "hub.tooManyTransfers"],
    ["Could not close session", "hub.closeFailed"],
    ["Support agent role required", "hub.agentRequired"]
];

function hubErrorText(message) {
    const text = String(message ?? "");
    const match = HUB_ERROR_KEYS.find(([prefix]) => text.startsWith(prefix));
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
        .build();

    registerHandlers(connection);

    connection.onreconnecting(() => setConnectionStatus("reconnecting"));
    // Groups live on the server connection: after a reconnect this tab has to join them again.
    connection.onreconnected(() => {
        setConnectionStatus("connected");
        rejoinGroups();
    });
    connection.onclose(() => {
        if (signalRConnection !== connection) return; // closed on purpose (sign-out)
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

// Called by logout().
function stopSignalRConnection() {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
    const connection = signalRConnection;
    signalRConnection = null;
    activeChatSessionId = null;
    if (connection) connection.stop().catch(() => {});
}

function hideTyping() {
    const indicator = byId("chat-typing-indicator");
    if (indicator) indicator.classList.add("hidden");
}

function markUnread(sessionId) {
    document.querySelectorAll("#active-sessions-list .session-item").forEach(item => {
        if (item.dataset.sessionId === String(sessionId)) item.classList.add("has-unread");
    });
}

function registerHandlers(connection) {
    // The server refuses empty or oversized messages and too many messages in a row, and says so here.
    connection.on("Error", message => {
        const box = chatContainer();
        if (!box) return;
        box.append(h("div", { class: "system-status-card failed", role: "alert", text: hubErrorText(message) }));
        box.scrollTop = box.scrollHeight;
    });

    connection.on("ReceiveMessage", msgDto => {
        if (!msgDto) return;

        // Only the conversation on screen is drawn: a message for another session never lands in this window.
        const viewed = viewedSessionId();
        if (!viewed || (msgDto.sessionId && String(msgDto.sessionId) !== String(viewed))) {
            if (IS_AGENT_PAGE && msgDto.sessionId) markUnread(msgDto.sessionId);
            return;
        }

        if (!IS_AGENT_PAGE && (msgDto.sender === "AI" || msgDto.sender === "Agent")) hideTyping();
        addChatEntry({ msg: msgDto, historical: false });

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
            if (String(sessionId) === String(selectedSessionId)) {
                resetAgentChatView();
                notify(t("chat.sessionClosed"), "info");
            }
            loadActiveSessions();
            return;
        }
        if (String(sessionId) !== String(activeChatSessionId)) return;
        activeChatSessionId = null;
        safeSession.remove("activeChatSessionId");
        hideTyping();
        setupChatState(false);
        chatLog.push({ kind: "note", noteKey: "chat.sessionClosed" }, { kind: "start" });
        renderChatLog();
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

// Forgets what the log showed (the agent panel went back to its "select a session" state).
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

// Replaces the log with a stored conversation. Its cards are inert: a confirmation from the past can never be confirmed again.
function renderChatHistory(messages) {
    chatLog = (Array.isArray(messages) ? messages : []).map(msg => ({ msg, historical: true }));
    if (!chatLog.length) chatLog.push({ kind: "note", noteKey: "chat.started" });
    renderChatLog();
}

function addChatEntry(entry) {
    const box = chatContainer();
    if (!box) return;
    chatLog.push(entry);
    const nearBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 80;
    renderEntry(entry, box);
    if (nearBottom) box.scrollTop = box.scrollHeight;
}

async function loadSessionMessages(sessionId) {
    const box = byId("chat-messages");
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

        if (IS_AGENT_PAGE && !entry.historical && String(msgDto.sessionId) === String(selectedSessionId)) {
            byId("agent-chat-title").textContent = displayDept;
        }
        return;
    }

    // 1. Transfer confirmation widget (written by the AI assistant)
    if (fromAi && content.includes("[CONFIRM_TRANSFER:")) {
        const field = (pattern, fallback) => { const m = content.match(pattern); return m ? m[1].trim() : fallback; };
        const source = field(/source=([^,\s\]]+)/, "");
        const destination = field(/destination=([^,\s\]]+)/, "");
        const description = field(/description=([^\]]+)/, "AI Transfer");
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
                try {
                    if (!(await ensureConnected())) throw new Error("offline");
                    await signalRConnection.invoke("ConfirmTransferFromChatAsync", activeChatSessionId, source, destination, parsedAmount.value, description);
                } catch (err) {
                    // Give the customer the buttons back.
                    entry.resolved = null;
                    yes.disabled = false;
                    no.disabled = false;
                    yes.textContent = t("chat.confirm");
                    notify(t("chat.confirmFailed"), "error");
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

        if (!IS_AGENT_PAGE && !entry.historical) {
            celebrate();
            accountsStore.refresh(true).then(() => { if (activeAccountId) loadTransactions(activeAccountId); });
        }
        return;
    }

    // 3. Transfer failed
    if (fromSystem && content.includes("[TRANSFER_FAILED:")) {
        const errorKeyMatch = content.match(/errorKey=([^,\s\]]+)/);
        const messageMatch = content.match(/message=([^\]]+)/);
        const errorKey = errorKeyMatch ? errorKeyMatch[1] : "TransactionFailed";
        const rawMessage = messageMatch ? messageMatch[1] : "";

        // A live "needs a one-time code" answer opens the code dialog for the transfer the customer just confirmed.
        const needsCode = errorKey === "Requires2FA" || errorKey === "SuspectedFraudDuplicate" || errorKey === "SuspectedFraudHighValue";
        if (needsCode && !IS_AGENT_PAGE && !entry.historical && pendingChatTransfer) {
            const transfer = pendingChatTransfer;
            showOtpToast(splitOtpMarker(rawMessage).otp);
            showOTPModal(errorText(errorKey), async code => {
                try {
                    if (!(await ensureConnected())) return { success: false, message: t("chat.offline") };
                    await signalRConnection.invoke("ConfirmTransferFromChatAsync", activeChatSessionId,
                        transfer.source, transfer.destination, transfer.amount, transfer.description, code);
                    return { success: true };
                } catch (err) {
                    return { success: false, message: t("chat.confirmFailed") };
                }
            });
        }

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
        if (!(await ensureConnected())) { notify(t("chat.offline"), "error"); return; }
        try {
            await signalRConnection.invoke("StartSessionAsync", "Customer Support");
        } catch (err) {
            notify(t("chat.startFailed"), "error");
        }
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
    ensureConnected(); // joins the session group, so replies arrive live again
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

            if (!(await ensureConnected())) { notify(t("chat.offline"), "error"); return; }
            try {
                await signalRConnection.invoke("SendMessageAsync", activeChatSessionId, text);
                input.value = "";
                input.focus();
            } catch (err) {
                notify(t("chat.sendFailed"), "error");
            }
        });
    });
}

// The agent panel joins the room of the session it opens (the server has no "leave", so chat.js only draws the open one).
async function joinAgentChatSession(sessionId) {
    if (!(await ensureConnected())) return;
    try {
        await signalRConnection.invoke("JoinSessionAsync", sessionId);
    } catch (err) {
        console.error("Could not join the session:", err);
    }
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
