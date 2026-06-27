/* ==========================================================================
   SmartBank Real-time Chat Wrapper - chat.js
   Manages SignalR WebSockets for Customers and Customer Support Agents
   ========================================================================== */

const HUBS_URL = window.location.hostname === "localhost" || window.location.hostname === "127.0.0.1"
    ? "http://localhost:5038/hubs"
    : "https://smartbank-fintech-api.onrender.com/hubs";
let signalRConnection = null;
let activeChatSessionId = null;

// Initialize SignalR Connection with Access Token
async function startSignalRConnection(token) {
    if (signalRConnection) {
        await signalRConnection.stop();
    }

    signalRConnection = new signalR.HubConnectionBuilder()
        .withUrl(`${HUBS_URL}/support?access_token=${token}`)
        .withAutomaticReconnect()
        .build();

    // Setup incoming events
    signalRConnection.on("ReceiveMessage", (msgDto) => {
        // Hide typing indicator when receiving a message from AI or Agent
        if (msgDto.sender === "AI" || msgDto.sender === "Agent") {
            const indicator = document.getElementById("chat-typing-indicator");
            if (indicator) indicator.classList.add("hidden");
        }
        appendMessage(msgDto);

        // Phase 4: AI Co-Pilot trigger on new customer message
        const path = window.location.pathname;
        const isAgentPanel = path.includes("agent.html");
        if (isAgentPanel && msgDto.sender === "User" && typeof fetchCoPilotSuggestion === "function") {
            if (selectedSessionId && selectedSessionId === msgDto.sessionId) {
                fetchCoPilotSuggestion(selectedSessionId);
            }
        }
    });

    signalRConnection.on("AgentTyping", (senderRole) => {
        const path = window.location.pathname;
        const isAgentPanel = path.includes("agent.html");
        
        if (!isAgentPanel && (senderRole === "AI" || senderRole === "Agent")) {
            const indicator = document.getElementById("chat-typing-indicator");
            if (indicator) {
                indicator.classList.remove("hidden");
                const container = document.getElementById("chat-messages");
                if (container) container.scrollTop = container.scrollHeight;
            }
        }
    });

    signalRConnection.on("AgentStopTyping", (senderRole) => {
        const path = window.location.pathname;
        const isAgentPanel = path.includes("agent.html");
        
        if (!isAgentPanel && (senderRole === "AI" || senderRole === "Agent")) {
            const indicator = document.getElementById("chat-typing-indicator");
            if (indicator) indicator.classList.add("hidden");
        }
    });

    signalRConnection.on("SessionStarted", (sessionDto) => {
        activeChatSessionId = sessionDto.id;
        sessionStorage.setItem("activeChatSessionId", activeChatSessionId);
        setupChatState(true);
        loadSessionMessages(activeChatSessionId);
    });

    signalRConnection.on("SessionClosed", (sessionId) => {
        appendSystemMessage(getLocalizedText("SessionClosed", "This session has been closed."));
        setupChatState(false);
        sessionStorage.removeItem("activeChatSessionId");
        activeChatSessionId = null;
        
        // If agent panel, refresh sidebar
        const path = window.location.pathname;
        if (path.includes("agent.html") && typeof loadActiveSessions === "function") {
            loadActiveSessions();
        }
    });

    signalRConnection.on("NewSessionRequest", (sessionDto) => {
        // If we are an agent, reload the sessions list immediately
        const path = window.location.pathname;
        if (path.includes("agent.html") && typeof loadActiveSessions === "function") {
            loadActiveSessions();
        }
    });

    try {
        await signalRConnection.start();
        console.log("SignalR connected successfully.");
        
        // If agent, register to Agents group to listen to new notifications
        const path = window.location.pathname;
        if (path.includes("agent.html")) {
            await signalRConnection.invoke("RegisterAgentAsync");
        }
    } catch (err) {
        console.error("SignalR Connection failed: ", err);
    }
}

// Start customer support session from floating chat widget
async function initiateCustomerSupportSession() {
    if (!signalRConnection || signalRConnection.state !== "Connected") {
        await startSignalRConnection(currentToken);
    }

    if (signalRConnection.state === "Connected") {
        await signalRConnection.invoke("StartSessionAsync", "Customer Support");
    }
}

// Agent joins customer session group
async function joinAgentChatSession(sessionId) {
    if (!signalRConnection || signalRConnection.state !== "Connected") {
        await startSignalRConnection(currentToken);
    }

    if (signalRConnection.state === "Connected") {
        await signalRConnection.invoke("JoinSessionAsync", sessionId);
    }
}

// Load messages for customer chat widget via API
async function loadSessionMessages(sessionId) {
    const chatMsgsEl = document.getElementById("chat-messages");
    chatMsgsEl.innerHTML = '<div class="loading-spinner">Loading messages...</div>';

    try {
        const response = await fetch(`${API_URL}/chat/messages/${sessionId}`, {
            headers: { "Authorization": `Bearer ${currentToken}` }
        });

        if (!response.ok) {
            chatMsgsEl.innerHTML = '<div class="text-muted">Could not load history.</div>';
            return;
        }

        const messages = await response.json();
        chatMsgsEl.innerHTML = "";

        if (messages.length === 0) {
            appendSystemMessage("Chat session started.");
            return;
        }

        messages.forEach(msg => appendMessage(msg));
    } catch (err) {
        chatMsgsEl.innerHTML = '<div class="text-muted">Connection failed.</div>';
    }
}

// Append Chat Bubble
function appendMessage(msgDto) {
    // Check if customer panel or agent panel
    const path = window.location.pathname;
    const isAgentPanel = path.includes("agent.html");
    
    const messagesContainer = document.getElementById(isAgentPanel ? "agent-chat-messages" : "chat-messages");
    if (!messagesContainer) return;

    // Remove placeholders
    const placeholder = messagesContainer.querySelector(".chat-placeholder, .agent-chat-placeholder");
    if (placeholder) {
        placeholder.remove();
    }

    const time = new Date(msgDto.createdAt).toLocaleTimeString(currentLanguage === "tr" ? "tr-TR" : "en-US", {
        hour: '2-digit', minute: '2-digit'
    });

    const content = msgDto.content;

    // 0. Check if it's a Room Transfer message
    if (content.includes("[SESSION_TRANSFERRED:")) {
        const toMatch = content.match(/to=([^\]]+)/);
        const department = toMatch ? toMatch[1] : "General";

        const card = document.createElement("div");
        card.className = "system-status-card success";
        
        const titleText = currentLanguage === "tr" ? "Oda Transfer Edildi" : "Session Transferred";
        
        let displayDept = department;
        if (currentLanguage === "tr") {
            if (department === "General Support") displayDept = "Genel Destek";
            else if (department === "Loans Department") displayDept = "Kredi Departmanı";
            else if (department === "Card Services") displayDept = "Kart Hizmetleri";
            else if (department === "Investment Advisory") displayDept = "Yatırım Danışmanlığı";
        } else {
            if (department === "Genel Destek") displayDept = "General Support";
            else if (department === "Kredi Departmanı") displayDept = "Loans Department";
            else if (department === "Kart Hizmetleri") displayDept = "Card Services";
            else if (department === "Yatırım Danışmanlığı") displayDept = "Investment Advisory";
        }

        const descText = currentLanguage === "tr"
            ? `Sohbet başarıyla <strong>${displayDept}</strong> birimine aktarıldı.`
            : `Sohbet has been transferred to <strong>${displayDept}</strong> department.`;

        card.innerHTML = `
            <div class="system-status-title">🔄 ${titleText}</div>
            <div>${descText}</div>
            <span class="message-timestamp">${time}</span>
        `;

        messagesContainer.appendChild(card);
        messagesContainer.scrollTop = messagesContainer.scrollHeight;
        
        if (isAgentPanel && typeof selectedSessionId !== "undefined" && selectedSessionId === msgDto.sessionId) {
            const chatTitleEl = document.getElementById("agent-chat-title");
            if (chatTitleEl) chatTitleEl.textContent = displayDept;
        }
        
        return;
    }

    // 1. Check if it's a Transfer Confirmation Widget
    if (content.includes("[CONFIRM_TRANSFER:")) {
        const sourceMatch = content.match(/source=([^,\s\]]+)/);
        const destMatch = content.match(/destination=([^,\s\]]+)/);
        const amountMatch = content.match(/amount=([^,\s\]]+)/);
        const descMatch = content.match(/description=([^\]]+)/);

        const source = sourceMatch ? sourceMatch[1] : "";
        const destination = destMatch ? destMatch[1] : "";
        const amount = amountMatch ? amountMatch[1] : "0";
        const description = descMatch ? descMatch[1] : "AI Transfer";

        const card = document.createElement("div");
        card.className = "transfer-confirm-card animate-bounce-short";
        
        const titleText = currentLanguage === "tr" ? "Para Transferi Onayı" : "Transfer Confirmation";
        const srcLabel = currentLanguage === "tr" ? "Kaynak Hesap" : "Source Account";
        const destLabel = currentLanguage === "tr" ? "Alıcı Hesap" : "Recipient Account";
        const descLabel = currentLanguage === "tr" ? "Açıklama" : "Description";
        const confirmBtnText = currentLanguage === "tr" ? "Onayla" : "Confirm";
        const cancelBtnText = currentLanguage === "tr" ? "İptal Et" : "Cancel";

        card.innerHTML = `
            <div class="transfer-confirm-title">
                <i class="logo-icon font-semibold">🔄</i> ${titleText}
            </div>
            <div class="transfer-confirm-item">${srcLabel}: <strong>${source}</strong></div>
            <div class="transfer-confirm-item">${destLabel}: <strong>${destination}</strong></div>
            <div class="transfer-confirm-item">${descLabel}: <strong>${description}</strong></div>
            <div class="transfer-confirm-amount">${amount} TRY</div>
            <div class="transfer-confirm-actions">
                <button class="btn-confirm btn-confirm-yes" ${isAgentPanel ? "disabled" : ""}>${confirmBtnText}</button>
                <button class="btn-confirm btn-confirm-no" ${isAgentPanel ? "disabled" : ""}>${cancelBtnText}</button>
            </div>
        `;

        if (!isAgentPanel) {
            const yesBtn = card.querySelector(".btn-confirm-yes");
            const noBtn = card.querySelector(".btn-confirm-no");

            yesBtn.addEventListener("click", async () => {
                yesBtn.disabled = true;
                noBtn.disabled = true;
                yesBtn.textContent = currentLanguage === "tr" ? "İşleniyor..." : "Processing...";
                try {
                    if (signalRConnection && signalRConnection.state === "Connected") {
                        await signalRConnection.invoke("ConfirmTransferFromChatAsync", activeChatSessionId, source, destination, parseFloat(amount), description);
                    }
                } catch (err) {
                    console.error("SignalR ConfirmTransfer failed:", err);
                }
            });

            noBtn.addEventListener("click", () => {
                card.style.opacity = 0.5;
                yesBtn.disabled = true;
                noBtn.disabled = true;
                noBtn.textContent = currentLanguage === "tr" ? "İptal Edildi" : "Cancelled";
            });
        }

        messagesContainer.appendChild(card);
        messagesContainer.scrollTop = messagesContainer.scrollHeight;
        return;
    }

    // 2. Check if it's a Transfer Success message
    if (content.includes("[TRANSFER_SUCCESS:")) {
        const amountMatch = content.match(/amount=([^,\s\]]+)/);
        const destMatch = content.match(/destination=([^,\s\]]+)/);
        const amount = amountMatch ? amountMatch[1] : "0";
        const destination = destMatch ? destMatch[1] : "";

        const card = document.createElement("div");
        card.className = "system-status-card success";
        
        const titleText = currentLanguage === "tr" ? "İşlem Başarılı" : "Transfer Successful";
        const descText = currentLanguage === "tr" 
            ? `${amount} TRY, ${destination} numaralı hesaba başarıyla gönderildi.` 
            : `${amount} TRY has been successfully sent to ${destination}.`;

        card.innerHTML = `
            <div class="system-status-title">✅ ${titleText}</div>
            <div>${descText}</div>
            <span class="message-timestamp">${time}</span>
        `;

        messagesContainer.appendChild(card);
        messagesContainer.scrollTop = messagesContainer.scrollHeight;
        
        if (!isAgentPanel) {
            if (typeof confetti === "function") {
                confetti({
                    particleCount: 120,
                    spread: 80,
                    origin: { y: 0.6 }
                });
            }
            if (typeof loadAccounts === "function") {
                loadAccounts();
            }
        }
        return;
    }

    // 3. Check if it's a Transfer Failed message
    if (content.includes("[TRANSFER_FAILED:")) {
        const errorKeyMatch = content.match(/errorKey=([^,\s\]]+)/);
        const messageMatch = content.match(/message=([^\]]+)/);
        const errorKey = errorKeyMatch ? errorKeyMatch[1] : "TransferFailed";
        const rawMessage = messageMatch ? messageMatch[1] : "Transfer failed.";
        
        // Intercept 2FA or Fraud OTP trigger
        if (errorKey === "Requires2FA" || 
            errorKey === "SuspectedFraudDuplicate" || 
            errorKey === "SuspectedFraudHighValue") {
            
            let reasonMsg = rawMessage;
            let otpCode = "";
            if (reasonMsg.includes("|OTP:")) {
                const parts = reasonMsg.split("|OTP:");
                reasonMsg = parts[0];
                otpCode = parts[1];
            }

            // Localize the reason key or fall back to parsed reasonMsg
            const localizedReason = getLocalizedText(errorKey, reasonMsg);

            // We only trigger this if the current user is NOT an agent
            if (!isAgentPanel) {
                // Show Simulated SMS Toast containing the OTP code
                if (typeof showMockSMSToast === "function") {
                    showMockSMSToast(otpCode);
                }

                // Retrieve the transfer parameters from the preceding confirm-transfer card
                const confirmCards = document.querySelectorAll(".transfer-confirm-card");
                if (confirmCards.length > 0) {
                    const lastCard = confirmCards[confirmCards.length - 1];
                    const items = lastCard.querySelectorAll("strong");
                    const amountText = lastCard.querySelector(".transfer-confirm-amount").textContent;
                    
                    const source = items[0] ? items[0].textContent : "";
                    const destination = items[1] ? items[1].textContent : "";
                    const description = items[2] ? items[2].textContent : "";
                    const amount = parseFloat(amountText);

                    // Show OTP Modal
                    if (typeof showOTPModal === "function") {
                        showOTPModal(localizedReason, async (codeEntered) => {
                            try {
                                if (signalRConnection && signalRConnection.state === "Connected") {
                                    // Call the SignalR method again, passing the otpCode
                                    await signalRConnection.invoke("ConfirmTransferFromChatAsync", activeChatSessionId, source, destination, amount, description, codeEntered);
                                    return { success: true };
                                } else {
                                    return { success: false, message: "SignalR offline." };
                                }
                            } catch (err) {
                                return { success: false, message: "Failed to confirm transfer." };
                            }
                        });
                    }
                }
            }
        }

        const translatedMsg = getLocalizedText(errorKey, rawMessage.split("|OTP:")[0]);

        const card = document.createElement("div");
        card.className = "system-status-card failed";
        
        const titleText = currentLanguage === "tr" ? "İşlem Başarısız" : "Transfer Failed";

        card.innerHTML = `
            <div class="system-status-title">❌ ${titleText}</div>
            <div>${translatedMsg}</div>
            <span class="message-timestamp">${time}</span>
        `;

        messagesContainer.appendChild(card);
        messagesContainer.scrollTop = messagesContainer.scrollHeight;
        return;
    }

    // 4. Default message bubble
    const bubble = document.createElement("div");
    const roleClass = msgDto.sender.toLowerCase(); // "user", "ai", "agent", "system"
    bubble.className = `message-bubble ${roleClass}`;

    bubble.innerHTML = `
        ${msgDto.content}
        <span class="message-timestamp">${time}</span>
    `;

    messagesContainer.appendChild(bubble);
    messagesContainer.scrollTop = messagesContainer.scrollHeight;
}

// Append System Message
function appendSystemMessage(text) {
    const path = window.location.pathname;
    const isAgentPanel = path.includes("agent.html");
    const container = document.getElementById(isAgentPanel ? "agent-chat-messages" : "chat-messages");
    if (!container) return;

    const sysMsg = document.createElement("div");
    sysMsg.className = "text-muted text-center py-2 text-xs";
    sysMsg.textContent = text;
    
    container.appendChild(sysMsg);
    container.scrollTop = container.scrollHeight;
}

// Adjust UI Chat forms when active or closed
function setupChatState(isActive) {
    const chatInput = document.getElementById("chat-input");
    const chatSendBtn = document.getElementById("chat-send-btn");
    
    if (chatInput && chatSendBtn) {
        chatInput.disabled = !isActive;
        chatSendBtn.disabled = !isActive;
        if (isActive) {
            chatInput.focus();
        }
    }
}

/* ==========================================================================
   INITIALIZATION & FLOATING CHAT BOX EVENTS (dashboard.html)
   ========================================================================== */
document.addEventListener("DOMContentLoaded", () => {
    const path = window.location.pathname;
    
    // Setup Customer Floating chat widget
    if (path.includes("dashboard.html")) {
        const toggleBtn = document.getElementById("chat-toggle");
        const chatBox = document.getElementById("chat-box");
        const closeBtn = document.getElementById("chat-close");
        const startChatBtn = document.getElementById("btn-start-chat");
        const chatForm = document.getElementById("chat-form");

        // Toggle Expand
        toggleBtn.addEventListener("click", async () => {
            chatBox.classList.toggle("hidden");
            
            // If chat opened and connection not started, connect
            if (!chatBox.classList.contains("hidden") && !signalRConnection) {
                await startSignalRConnection(currentToken);
                
                // If there's an active session from sessionStorage, load it
                const savedSessionId = sessionStorage.getItem("activeChatSessionId");
                if (savedSessionId) {
                    activeChatSessionId = savedSessionId;
                    setupChatState(true);
                    loadSessionMessages(activeChatSessionId);
                }
            }
        });

        // Close Chat Box
        closeBtn.addEventListener("click", () => {
            chatBox.classList.add("hidden");
        });

        // Start Chat Session Button
        startChatBtn.addEventListener("click", async () => {
            await initiateCustomerSupportSession();
        });

        // Send Message submit
        chatForm.addEventListener("submit", async (e) => {
            e.preventDefault();
            const inputEl = document.getElementById("chat-input");
            const text = inputEl.value.trim();
            if (!text || !activeChatSessionId) return;

            try {
                if (signalRConnection && signalRConnection.state === "Connected") {
                    await signalRConnection.invoke("SendMessageAsync", activeChatSessionId, text);
                    inputEl.value = "";
                }
            } catch (err) {
                console.error("Failed to send message: ", err);
            }
        });
    }

    // Auto-connect SignalR when Representative Agent page opens
    if (path.includes("agent.html")) {
        if (currentToken) {
            startSignalRConnection(currentToken);
        }
    }
});
