/* ==========================================================================
   SmartBank Core Application Logic - app.js
   Handles Authentication, Banking API calls, and TR/EN Client Localization
   ========================================================================== */

const API_URL = window.location.hostname === "localhost" || window.location.hostname === "127.0.0.1"
    ? "http://localhost:5038/api"
    : "https://smartbank-fintech-api.onrender.com/api";

// Localization Dictionary
const i18n = {
    en: {
        // Auth page
        "login-title": "Sign In",
        "login-subtitle": "Access your financial dashboard",
        "lbl-username-login": "T.C. Identity Number",
        "lbl-password-login": "Password (6 Digits)",
        "btn-login-submit": "Login",
        "txt-no-account": "Don't have an account?",
        "link-to-register": "Register here",
        "register-title": "Create Account",
        "register-subtitle": "Register today using your T.C. Identity Number",
        "lbl-firstname": "First Name",
        "lbl-lastname": "Last Name",
        "lbl-username-reg": "Username",
        "lbl-email": "T.C. Identity Number",
        "lbl-password-reg": "Password (6 Digits)",
        "btn-register-submit": "Register",
        "txt-has-account": "Already have an account?",
        "link-to-login": "Sign In",

        // Customer Dashboard
        "title-accounts": "Accounts",
        "title-transfer": "Transfer Funds",
        "transfer-desc": "Send money instantly using account number",
        "lbl-source-acc": "Source Account",
        "lbl-dest-acc": "Destination Account Number",
        "lbl-amount": "Amount",
        "lbl-desc": "Description",
        "btn-transfer-submit": "Execute Transfer",
        "title-history": "Transaction History",
        "history-desc": "Recent financial movements",
        "th-date": "Date",
        "th-type": "Type",
        "th-desc": "Description",
        "th-amount": "Amount",
        "txt-no-tx": "Select an account to view history",
        "chat-welcome": "Welcome! Need help with your accounts, transfers, or card limits?",
        "btn-start-chat": "Start Session",
        "chat-toggle-label": "Live Support",
        "chat-input-placeholder": "Type a message...",

        // Agent Dashboard
        "title-active-chats": "Active Support Chats",
        "agent-chat-title": "Chatting with Guest",
        "btn-close-session": "Close Session",
        "txt-select-chat": "Select a Support Session",
        "txt-select-chat-desc": "Click on a chat session on the left sidebar to start helping customers.",
        "agent-chat-input-placeholder": "Type support message...",
        "txt-no-active-chats": "No active chats at the moment",

        // API Localization Keys
        "UsernameAlreadyExists": "Username is already taken.",
        "EmailAlreadyExists": "Email is already registered.",
        "InvalidCredentials": "Invalid username/email or password.",
        "InsufficientFunds": "Insufficient funds in the source account.",
        "SourceAccountNotFound": "Source account was not found.",
        "DestinationAccountNotFound": "Destination account was not found.",
        "CannotTransferToSelf": "Cannot transfer money to the same account.",
        "CurrencyMismatch": "Exchange transfers not supported in this version.",
        "UnauthorizedSessionAccess": "Access denied to this chat session.",
        "InvalidAmount": "Amount must be greater than zero.",
        "TransferSuccess": "Transfer executed successfully!",
        "TcknNotFound": "T.C. Identity Number is not registered.",
        "PasswordResetSuccess": "Password reset successfully!",
        "link-to-forgot": "Forgot Password?",
        "forgot-title": "Reset Password",
        "forgot-subtitle": "Reset your password using your T.C. Identity Number",
        "lbl-email-forgot": "T.C. Identity Number",
        "lbl-new-password": "New Password (6 Digits)",
        "btn-forgot-submit": "Reset Password",
        "link-forgot-to-login": "Back to Sign In",
        
        // SignalR Status
        "StatusAI": "SmartBank AI",
        "StatusAgent": "Live Agent",
        "SessionClosed": "Session has been closed.",

        // Phase 2 i18n keys
        "title-analytics": "Financial Analytics",
        "title-spending-chart": "Spendings Distribution",
        "title-trend-chart": "Balance History",
        "title-card-customizer": "Card & Security Workspace",
        "desc-card-customizer": "Custom card style & 2FA security",
        "lbl-select-theme": "Choose Theme:",
        "lbl-2fa-title": "2FA Security",
        "lbl-2fa-desc": "Requires verification code for transfers over 1000 TRY",
        "otp-modal-title": "Security Verification",
        "otp-modal-desc": "Please enter the 6-digit verification code sent to your registered device to approve this transfer.",
        "btn-submit-otp": "Confirm Code",
        
        "InvalidOtpCode": "Invalid or expired verification code.",
        "SuspectedFraudDuplicate": "Suspicious Activity: Same transfer submitted within 30 seconds.",
        "SuspectedFraudHighValue": "Suspicious Activity: Transfer amount exceeds standard limit.",
        "Requires2FA": "Two-Factor Verification Required",

        "lbl-metric-resolved": "Resolved Chats",
        "lbl-metric-time": "Avg Response Time",
        "lbl-metric-csat": "CSAT Score",
        "lbl-metric-status": "Your Status",
        "opt-status-active": "Active",
        "opt-status-busy": "Busy",
        "opt-status-break": "Break",
        
        "lbl-copilot-title": "✨ AI Co-Pilot Recommendation",
        "btn-use-suggestion": "Use Recommendation",
        "opt-transfer-select": "Transfer to...",
        "opt-dept-general": "General Support",
        "opt-dept-loans": "Loans Department",
        "opt-dept-cards": "Card Services",
        "opt-dept-investments": "Investment Advisory",
        "title-market-rates": "Live Market Rates",
        "txt-rates-updated": "Last Update: ",
        "title-credit-cards": "My Credit Cards",
        "stmt-modal-title": "Credit Card Statement",
        "lbl-stmt-period": "Billing Period",
        "lbl-stmt-debt": "Statement Debt",
        "lbl-stmt-min": "Minimum Payment",
        "lbl-stmt-due": "Due Date",
        "stmt-status-paid": "Fully Paid",
        "stmt-status-unpaid": "Unpaid",
        "lbl-stmt-tx-title": "Statement Transactions",
        "lbl-pay-debt-title": "Pay Credit Card Debt",
        "lbl-pay-source": "Payment Account",
        "lbl-pay-amount": "Amount to Pay",
        "btn-pay-submit": "Execute Payment",
        "btn-create-account": "+ New Account",
        "CreditCardNotFound": "Credit card not found.",
        "PaymentSuccess": "Payment completed successfully!",
        "PaymentFailed": "Payment failed.",
        "create-acc-modal-title": "Open New Account",
        "lbl-select-acc-type": "Select Account Type:",
        "btn-submit-create-acc": "Open Account"
    },
    tr: {
        // Auth page
        "login-title": "Giriş Yap",
        "login-subtitle": "Finansal panelinize erişin",
        "lbl-username-login": "T.C. Kimlik Numarası",
        "lbl-password-login": "Şifre (6 Haneli)",
        "btn-login-submit": "Giriş Yap",
        "txt-no-account": "Hesabınız yok mu?",
        "link-to-register": "Buradan kaydolun",
        "register-title": "Hesap Oluştur",
        "register-subtitle": "T.C. Kimlik numaranız ile hemen kaydolun",
        "lbl-firstname": "Adı",
        "lbl-lastname": "Soyadı",
        "lbl-username-reg": "Kullanıcı Adı",
        "lbl-email": "T.C. Kimlik Numarası",
        "lbl-password-reg": "Şifre (6 Haneli)",
        "btn-register-submit": "Kaydol",
        "txt-has-account": "Zaten hesabınız var mı?",
        "link-to-login": "Giriş Yap",

        // Müşteri Dashboard
        "title-accounts": "Hesaplarım",
        "title-transfer": "Para Gönder",
        "transfer-desc": "Hesap numarasını kullanarak anında para transferi yapın",
        "lbl-source-acc": "Kaynak Hesap",
        "lbl-dest-acc": "Alıcı Hesap Numarası",
        "lbl-amount": "Miktar",
        "lbl-desc": "Açıklama",
        "btn-transfer-submit": "Transferi Gerçekleştir",
        "title-history": "Hesap Hareketleri",
        "history-desc": "Son finansal işlemleriniz",
        "th-date": "Tarih",
        "th-type": "Tür",
        "th-desc": "Açıklama",
        "th-amount": "Miktar",
        "txt-no-tx": "İşlem geçmişini görüntülemek için bir hesap seçin",
        "chat-welcome": "Merhaba! Hesaplarınız, transferleriniz veya kart limitleriniz hakkında yardıma mı ihtiyacınız var?",
        "btn-start-chat": "Sohbeti Başlat",
        "chat-toggle-label": "Canlı Destek",
        "chat-input-placeholder": "Mesajınızı yazın...",

        // Temsilci Dashboard
        "title-active-chats": "Aktif Destek Talepleri",
        "agent-chat-title": "Misafir ile Görüşülüyor",
        "btn-close-session": "Oturumu Kapat",
        "txt-select-chat": "Bir Sohbet Odası Seçin",
        "txt-select-chat-desc": "Müşterilere yardımcı olmaya başlamak için sol paneldeki aktif sohbet odalarından birine tıklayın.",
        "agent-chat-input-placeholder": "Destek mesajı yazın...",
        "txt-no-active-chats": "Şu anda aktif destek talebi bulunmuyor",

        // API Localization Keys
        "UsernameAlreadyExists": "Bu kullanıcı adı zaten alınmış.",
        "TcknAlreadyExists": "Bu T.C. Kimlik Numarası zaten kayıtlı.",
        "InvalidCredentials": "Hatalı T.C. Kimlik Numarası veya şifre.",
        "InsufficientFunds": "Gönderen hesapta yetersiz bakiye.",
        "SourceAccountNotFound": "Kaynak hesap bulunamadı.",
        "DestinationAccountNotFound": "Alıcı hesap bulunamadı.",
        "CannotTransferToSelf": "Kendi hesabınıza para transferi yapamazsınız.",
        "CurrencyMismatch": "Farklı para birimlerine transfer bu sürümde desteklenmiyor.",
        "UnauthorizedSessionAccess": "Bu destek odasına erişim yetkiniz yok.",
        "InvalidAmount": "Miktar sıfırdan büyük olmalıdır.",
        "TransferSuccess": "Para transferi başarıyla gerçekleştirildi!",
        "TcknNotFound": "Bu T.C. Kimlik Numarası sistemde kayıtlı değil.",
        "PasswordResetSuccess": "Şifreniz başarıyla güncellendi!",
        "link-to-forgot": "Şifremi Unuttum?",
        "forgot-title": "Şifreyi Sıfırla",
        "forgot-subtitle": "T.C. Kimlik numaranız ile şifrenizi kolayca sıfırlayın",
        "lbl-email-forgot": "T.C. Kimlik Numarası",
        "lbl-new-password": "Yeni Şifre (6 Haneli)",
        "btn-forgot-submit": "Şifreyi Sıfırla",
        "link-forgot-to-login": "Giriş Ekranına Dön",

        // SignalR Status
        "StatusAI": "SmartBank Yapay Zeka",
        "StatusAgent": "Müşteri Temsilcisi",
        "SessionClosed": "Görüşme sonlandırılmıştır.",

        // Phase 2 i18n keys
        "title-analytics": "Finansal Analiz",
        "title-spending-chart": "Harcama Dağılımı",
        "title-trend-chart": "Bakiye Değişimi",
        "title-card-customizer": "Kart ve Güvenlik Paneli",
        "desc-card-customizer": "Kart stili ve 2FA güvenlik ayarı",
        "lbl-select-theme": "Tema Seçin:",
        "lbl-2fa-title": "2FA Güvenliği",
        "lbl-2fa-desc": "1000 TRY üzerindeki transferler için doğrulama kodu ister.",
        "otp-modal-title": "Güvenlik Doğrulaması",
        "otp-modal-desc": "Lütfen işlemi onaylamak için kayıtlı cihazınıza gönderilen 6 haneli doğrulama kodunu girin.",
        "btn-submit-otp": "Kodu Doğrula",

        "InvalidOtpCode": "Geçersiz veya süresi dolmuş doğrulama kodu.",
        "SuspectedFraudDuplicate": "Şüpheli İşlem: 30 saniye içinde mükerrer transfer denemesi.",
        "SuspectedFraudHighValue": "Şüpheli İşlem: Transfer tutarı standart limitleri aşmaktadır.",
        "Requires2FA": "İki Aşamalı Güvenlik Doğrulaması",

        "lbl-metric-resolved": "Çözülen Sohbetler",
        "lbl-metric-time": "Ort. Yanıt Süresi",
        "lbl-metric-csat": "CSAT Skoru",
        "lbl-metric-status": "Durumunuz",
        "opt-status-active": "Aktif",
        "opt-status-busy": "Meşgul",
        "opt-status-break": "Mola",
        
        "lbl-copilot-title": "✨ AI Co-Pilot Önerisi",
        "btn-use-suggestion": "Öneriyi Kullan",
        "opt-transfer-select": "Aktar...",
        "opt-dept-general": "Genel Destek",
        "opt-dept-loans": "Kredi Departmanı",
        "opt-dept-cards": "Kart Hizmetleri",
        "opt-dept-investments": "Yatırım Danışmanlığı",
        "title-market-rates": "Canlı Piyasalar",
        "txt-rates-updated": "Son Güncelleme: ",
        "title-credit-cards": "Kredi Kartlarım",
        "stmt-modal-title": "Kredi Kartı Ekstresi",
        "lbl-stmt-period": "Hesap Dönemi",
        "lbl-stmt-debt": "Dönem Borcu",
        "lbl-stmt-min": "Asgari Ödeme",
        "lbl-stmt-due": "Son Ödeme Tarihi",
        "stmt-status-paid": "Ödendi",
        "stmt-status-unpaid": "Ödenmedi",
        "lbl-stmt-tx-title": "Dönem İçi Hareketler",
        "lbl-pay-debt-title": "Borç Ödeme",
        "lbl-pay-source": "Ödeme Yapılacak Hesap",
        "lbl-pay-amount": "Ödenecek Tutar",
        "btn-pay-submit": "Ödemeyi Gerçekleştir",
        "btn-create-account": "+ Yeni Hesap Aç",
        "CreditCardNotFound": "Kredi kartı bulunamadı.",
        "PaymentSuccess": "Borç ödeme işlemi başarıyla tamamlandı!",
        "PaymentFailed": "Ödeme işlemi başarısız oldu.",
        "create-acc-modal-title": "Yeni Hesap Aç",
        "lbl-select-acc-type": "Hesap Türü Seçiniz:",
        "btn-submit-create-acc": "Hesap Aç"
    }
};

// State Management
let currentLanguage = localStorage.getItem("lang") || "en";
let currentUser = JSON.parse(localStorage.getItem("user")) || null;
let currentToken = localStorage.getItem("token") || null;
let activeAccountId = null; // Currently selected account on dashboard
let showAllTransactions = false; // Transaction list collapse state
let activeCreditCardId = null; // Currently selected credit card on dashboard
let spendingChartInstance = null;
let trendChartInstance = null;
let activeMarketRates = [];
let savedContacts = [];
let standingOrders = [];


// Page Translation Engine
function translatePage() {
    const dict = i18n[currentLanguage];
    document.querySelectorAll("[id]").forEach(el => {
        if (dict[el.id]) {
            if (el.tagName === "INPUT" || el.tagName === "TEXTAREA") {
                el.placeholder = dict[el.id];
            } else {
                el.textContent = dict[el.id];
            }
        }
    });

    // Handle inputs placeholders dynamically
    const chatInput = document.getElementById("chat-input");
    if (chatInput) chatInput.placeholder = dict["chat-input-placeholder"];
    
    const agentInput = document.getElementById("agent-chat-input");
    if (agentInput) agentInput.placeholder = dict["agent-chat-input-placeholder"];

    // Update language toggle button label
    const langBtn = document.getElementById("lang-toggle");
    if (langBtn) {
        langBtn.textContent = currentLanguage === "en" ? "TR" : "EN";
    }
}

// Translate error keys or fallback to default English message
function getLocalizedText(key, defaultFallbackText) {
    const dict = i18n[currentLanguage];
    return dict[key] || defaultFallbackText;
}

// Auth Helpers
function saveAuth(token, user) {
    localStorage.setItem("token", token);
    localStorage.setItem("user", JSON.stringify(user));
    currentToken = token;
    currentUser = user;
}

function logout() {
    localStorage.clear();
    window.location.href = "index.html";
}

// Initialize Language Switch Event
document.addEventListener("DOMContentLoaded", () => {
    const langBtn = document.getElementById("lang-toggle");
    if (langBtn) {
        langBtn.addEventListener("click", () => {
            currentLanguage = currentLanguage === "en" ? "tr" : "en";
            localStorage.setItem("lang", currentLanguage);
            translatePage();
            
            // If the chat session is active, update history rendering with localized fallback text
            if (typeof renderMessages === "function") {
                renderMessages();
            }
        });
    }

    const logoutBtn = document.getElementById("btn-logout");
    if (logoutBtn) {
        logoutBtn.addEventListener("click", logout);
    }

    // Run translation
    translatePage();

    // Route Guards & Page Loader
    const path = window.location.pathname;
    if (path.includes("dashboard.html")) {
        if (!currentToken) {
            window.location.href = "index.html";
            return;
        }
        document.getElementById("user-display").textContent = currentUser.fullName;
        loadAccounts();
        loadCreditCards();
        initDashboardEvents();
        initCardCustomizer();
        load2FAStatus();
        init2FASettings();
        initOTPModalEvents();
        initCreditCardEvents();
        initCreateAccountEvent();
        initTabNavigation();
        initExchangeWidget();
        initMarketRates();
        initStandingOrders();
        initSavedContacts();
        initQrSimulators();
        initSidebar();
    } else if (path.includes("agent.html")) {
        if (!currentToken) {
            window.location.href = "index.html";
            return;
        }
        document.getElementById("user-display").textContent = currentUser.fullName;
        loadActiveSessions();
        initAgentEvents();
        loadAgentMetrics();
        initAgentStatusControl();
        initCoPilotEvents();
        initTransferControlEvents();
    } else if (path.includes("index.html") || path === "/" || path.endsWith("Web/")) {
        if (currentToken && currentUser) {
            redirectByUserRole();
        }
        initAuthEvents();
        initMarketRates();
    }
});

function redirectByUserRole() {
    if (currentUser.username.toLowerCase().includes("agent")) {
        window.location.href = "agent.html";
    } else {
        window.location.href = "dashboard.html";
    }
}

/* ==========================================================================
   AUTHENTICATION LOGIC (index.html)
   ========================================================================== */
function initAuthEvents() {
    const loginCard = document.getElementById("login-card");
    const registerCard = document.getElementById("register-card");
    const forgotCard = document.getElementById("forgot-card");
    
    const linkToRegister = document.getElementById("link-to-register");
    const linkToLogin = document.getElementById("link-to-login");
    const linkToForgot = document.getElementById("link-to-forgot");
    const linkForgotToLogin = document.getElementById("link-forgot-to-login");

    if (linkToRegister) {
        linkToRegister.addEventListener("click", (e) => {
            e.preventDefault();
            loginCard.classList.add("hidden");
            registerCard.classList.remove("hidden");
            if (forgotCard) forgotCard.classList.add("hidden");
            document.getElementById("register-error").classList.add("hidden");
        });
    }

    if (linkToLogin) {
        linkToLogin.addEventListener("click", (e) => {
            e.preventDefault();
            registerCard.classList.add("hidden");
            if (forgotCard) forgotCard.classList.add("hidden");
            loginCard.classList.remove("hidden");
            document.getElementById("login-error").classList.add("hidden");
        });
    }

    if (linkToForgot) {
        linkToForgot.addEventListener("click", (e) => {
            e.preventDefault();
            loginCard.classList.add("hidden");
            registerCard.classList.add("hidden");
            if (forgotCard) {
                forgotCard.classList.remove("hidden");
                const errorDiv = document.getElementById("forgot-error");
                const successDiv = document.getElementById("forgot-success");
                if (errorDiv) errorDiv.classList.add("hidden");
                if (successDiv) successDiv.classList.add("hidden");
            }
            const emailInput = document.getElementById("forgot-email");
            const passInput = document.getElementById("forgot-new-password");
            if (emailInput) emailInput.value = "";
            if (passInput) passInput.value = "";
        });
    }

    if (linkForgotToLogin) {
        linkForgotToLogin.addEventListener("click", (e) => {
            e.preventDefault();
            if (forgotCard) forgotCard.classList.add("hidden");
            registerCard.classList.add("hidden");
            loginCard.classList.remove("hidden");
            document.getElementById("login-error").classList.add("hidden");
        });
    }

    // Handle Login Form
    const loginForm = document.getElementById("login-form");
    if (loginForm) {
        loginForm.addEventListener("submit", async (e) => {
            e.preventDefault();
            const tckn = document.getElementById("login-tckn").value;
            const password = document.getElementById("login-password").value;
            const twoFaGroup = document.getElementById("login-2fa-group");
            const codeInput = document.getElementById("login-2fa-code");
            const btnSubmit = document.getElementById("btn-login-submit");
            const errorDiv = document.getElementById("login-error");

            errorDiv.classList.add("hidden");

            const is2FaStep = twoFaGroup && !twoFaGroup.classList.contains("hidden");

            if (is2FaStep) {
                const code = codeInput.value.trim();
                if (code.length !== 6 || isNaN(code)) {
                    errorDiv.textContent = currentLanguage === "tr" ? "Lütfen 6 haneli doğrulama kodunu girin." : "Please enter the 6-digit verification code.";
                    errorDiv.classList.remove("hidden");
                    return;
                }

                try {
                    const response = await fetch(`${API_URL}/auth/verify-2fa`, {
                        method: "POST",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify({ tckn, code })
                    });

                    const data = await response.json();

                    if (!response.ok) {
                        errorDiv.textContent = getLocalizedText(data.errorKey, data.message || "2FA verification failed");
                        errorDiv.classList.remove("hidden");
                        return;
                    }

                    saveAuth(data.token, { id: data.userId, username: data.username, tckn: data.tckn, fullName: data.fullName });
                    redirectByUserRole();
                } catch (err) {
                    errorDiv.textContent = getLocalizedText("ConnectionError", "Connection to server failed.");
                    errorDiv.classList.remove("hidden");
                }
            } else {
                try {
                    const response = await fetch(`${API_URL}/auth/login`, {
                        method: "POST",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify({ tckn, password })
                    });

                    const data = await response.json();

                    if (!response.ok) {
                        if (data.errorKey === "Requires2FA") {
                            // Split simulated OTP from message if present
                            const otpCode = (data.message || "").split("|OTP:")[1] || "123456";
                            
                            // Show simulated SMS Toast
                            showMockSMSToast(otpCode);
                            
                            // Transform login card to 2FA verification mode
                            document.getElementById("login-tckn").readOnly = true;
                            document.getElementById("login-password").readOnly = true;
                            twoFaGroup.classList.remove("hidden");
                            btnSubmit.textContent = currentLanguage === "tr" ? "Doğrula ve Giriş Yap" : "Verify & Sign In";
                            codeInput.focus();
                            return;
                        }

                        errorDiv.textContent = getLocalizedText(data.errorKey, data.message || "Login failed");
                        errorDiv.classList.remove("hidden");
                        return;
                    }

                    saveAuth(data.token, { id: data.userId, username: data.username, tckn: data.tckn, fullName: data.fullName });
                    redirectByUserRole();
                } catch (err) {
                    errorDiv.textContent = getLocalizedText("ConnectionError", "Connection to server failed.");
                    errorDiv.classList.remove("hidden");
                }
            }
        });
    }

    // Handle Register Form
    const registerForm = document.getElementById("register-form");
    if (registerForm) {
        registerForm.addEventListener("submit", async (e) => {
            e.preventDefault();
            const firstName = document.getElementById("reg-firstname").value.trim();
            const lastName = document.getElementById("reg-lastname").value.trim();
            const username = document.getElementById("reg-username").value.trim();
            const tckn = document.getElementById("reg-tckn").value.trim();
            const email = document.getElementById("reg-email").value.trim();
            const password = document.getElementById("reg-password").value.trim();
            const errorDiv = document.getElementById("register-error");

            errorDiv.classList.add("hidden");

            // Letter only validation for first name and last name
            const lettersOnlyRegex = /^[a-zA-ZçğıöşüÇĞİÖŞÜ\s]+$/;
            const lettersOnlyNoSpaceRegex = /^[a-zA-ZçğıöşüÇĞİÖŞÜ]+$/;
            
            if (!lettersOnlyRegex.test(firstName)) {
                errorDiv.textContent = currentLanguage === "tr" ? "İsim alanı sadece harf içerebilir." : "First name can only contain letters.";
                errorDiv.classList.remove("hidden");
                return;
            }
            if (!lettersOnlyNoSpaceRegex.test(lastName)) {
                errorDiv.textContent = currentLanguage === "tr" ? "Soyisim alanı sadece harf içerebilir (boşluksuz)." : "Last name can only contain letters (no spaces).";
                errorDiv.classList.remove("hidden");
                return;
            }

            try {
                const response = await fetch(`${API_URL}/auth/register`, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ firstName, lastName, username, email, tckn, password })
                });

                const data = await response.json();

                if (!response.ok) {
                    // If validation array comes back
                    if (data.errors) {
                        const validationMsg = Object.values(data.errors).flat().join(" ");
                        errorDiv.textContent = validationMsg;
                    } else {
                        errorDiv.textContent = getLocalizedText(data.errorKey, data.message || "Registration failed");
                    }
                    errorDiv.classList.remove("hidden");
                    return;
                }

                saveAuth(data.token, { id: data.userId, username: data.username, tckn: data.tckn, fullName: data.fullName });
                redirectByUserRole();
            } catch (err) {
                errorDiv.textContent = getLocalizedText("ConnectionError", "Connection to server failed.");
                errorDiv.classList.remove("hidden");
            }
        });
    }

    // Handle Forgot Password Form
    const forgotForm = document.getElementById("forgot-form");
    if (forgotForm) {
        forgotForm.addEventListener("submit", async (e) => {
            e.preventDefault();
            const tckn = document.getElementById("forgot-tckn").value;
            const newPassword = document.getElementById("forgot-new-password").value;
            const errorDiv = document.getElementById("forgot-error");
            const successDiv = document.getElementById("forgot-success");

            if (errorDiv) errorDiv.classList.add("hidden");
            if (successDiv) successDiv.classList.add("hidden");

            try {
                const response = await fetch(`${API_URL}/auth/forgot-password`, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ tckn, newPassword })
                });

                const data = await response.json();

                if (!response.ok) {
                    let errMsg = "Password reset failed";
                    if (data.errors) {
                        errMsg = Object.values(data.errors).flat().join(" ");
                    } else if (data.errorKey) {
                        errMsg = getLocalizedText(data.errorKey, data.message || errMsg);
                    } else if (data.message) {
                        errMsg = data.message;
                    }
                    if (errorDiv) {
                        errorDiv.textContent = errMsg;
                        errorDiv.classList.remove("hidden");
                    }
                    return;
                }

                if (successDiv) {
                    successDiv.textContent = getLocalizedText("PasswordResetSuccess", "Password reset successfully!");
                    successDiv.classList.remove("hidden");
                }
                
                // Clear fields
                const emailInput = document.getElementById("forgot-email");
                const passInput = document.getElementById("forgot-new-password");
                if (emailInput) emailInput.value = "";
                if (passInput) passInput.value = "";
            } catch (err) {
                if (errorDiv) {
                    errorDiv.textContent = getLocalizedText("ConnectionError", "Connection to server failed.");
                    errorDiv.classList.remove("hidden");
                }
            }
        });
    }
}

/* ==========================================================================
   CUSTOMER DASHBOARD LOGIC (dashboard.html)
   ========================================================================== */
async function loadAccounts() {
    const listEl = document.getElementById("accounts-list");
    listEl.innerHTML = '<div class="loading-spinner">Loading account details...</div>';

    try {
        const response = await fetch(`${API_URL}/banking/accounts`, {
            headers: { "Authorization": `Bearer ${currentToken}` }
        });

        if (!response.ok) {
            if (response.status === 401) {
                logout();
                return;
            }
            listEl.innerHTML = '<div class="alert alert-danger">Failed to load accounts.</div>';
            return;
        }

        const accounts = await response.json();
        window.latestAccountsList = accounts;
        listEl.innerHTML = "";

        // Populate Source Account Dropdown in Transfer Card
        const sourceSelect = document.getElementById("transfer-source");
        sourceSelect.innerHTML = "";

        if (accounts.length === 0) {
            listEl.innerHTML = '<div class="text-muted">No accounts active.</div>';
            return;
        }

        accounts.forEach(acc => {
            // Render Card
            const card = document.createElement("div");
            card.className = `account-card glassmorphism ${activeAccountId === acc.id ? 'active' : ''}`;
            
            let cardTitle = "SmartSavings";
            if (acc.accountType === "TimeDeposit") {
                cardTitle = "SmartDeposit (Vadeli)";
            } else if (acc.currency === "XAU") {
                cardTitle = "SmartGold (Altın)";
            } else if (acc.currency === "XAG") {
                cardTitle = "SmartSilver (Gümüş)";
            } else {
                cardTitle = currentLanguage === "tr" ? "Vadesiz Hesap" : "SmartSavings";
            }

            let balanceStr = "";
            if (acc.currency === "XAU" || acc.currency === "XAG") {
                balanceStr = `${acc.balance.toFixed(2)} Gr`;
            } else if (acc.currency === "USD") {
                balanceStr = `$${acc.balance.toFixed(2)}`;
            } else if (acc.currency === "EUR") {
                balanceStr = `€${acc.balance.toFixed(2)}`;
            } else {
                balanceStr = `${acc.balance.toFixed(2)} TRY`;
            }

            let extraHtml = "";
            if (acc.accountType === "TimeDeposit") {
                const interestRateVal = acc.interestRate ? acc.interestRate.toFixed(2) : "48.00";
                extraHtml = `
                    <div style="font-size: 0.7rem; color: #00f260; margin-top: 0.35rem; font-weight: 600;">
                        %${interestRateVal} Faiz | Vade: 30 Gün
                    </div>
                `;
            }

            card.innerHTML = `
                <div class="account-header">
                    <span>${cardTitle}</span>
                    <span class="account-currency">${acc.currency}</span>
                </div>
                <div class="account-balance">${balanceStr}</div>
                <div class="account-number">${acc.accountNumber}</div>
                <div style="font-size: 0.7rem; color: var(--text-muted); margin-top: 0.25rem;">Hesap Kodu: <span style="font-weight: 600; color: var(--text-main);">${acc.accountCode || '-'}</span></div>
                ${extraHtml}
                <div class="account-actions" style="display: flex; gap: 0.5rem; margin-top: 0.75rem; border-top: 1px solid rgba(255,255,255,0.06); padding-top: 0.5rem;">
                    <button class="btn btn-danger btn-xs btn-acc-delete" data-accid="${acc.id}" style="padding: 0.2rem 0.5rem; font-size: 0.7rem; background: rgba(255, 75, 92, 0.1); border-color: rgba(255, 75, 92, 0.2); color: #ff4b5c; font-weight: 600; margin-left: auto;">Sil</button>
                </div>
            `;

            card.addEventListener("click", () => {
                document.querySelectorAll(".account-card").forEach(c => c.classList.remove("active"));
                card.classList.add("active");
                activeAccountId = acc.id;
                
                // Deselect credit cards visually when clicking bank accounts
                const creditCards = document.querySelectorAll("#credit-cards-list .account-card");
                creditCards.forEach(cc => cc.classList.remove("active"));
                activeCreditCardId = null;

                loadTransactions(acc.id);
            });

            listEl.appendChild(card);

            // Bind Stop-Propagated Action Event Listeners
            const deleteBtn = card.querySelector(".btn-acc-delete");
            if (deleteBtn) {
                deleteBtn.addEventListener("click", async (e) => {
                    e.stopPropagation();

                    const executeAccountDeletion = async (accountId, targetAccountId) => {
                        try {
                            const url = `${API_URL}/banking/accounts/${accountId}${targetAccountId ? '?targetAccountId=' + targetAccountId : ''}`;
                            const res = await fetch(url, {
                                method: "DELETE",
                                headers: { "Authorization": `Bearer ${currentToken}` }
                            });
                            if (!res.ok) {
                                const errData = await res.json();
                                alert(getLocalizedText(errData.errorKey, errData.message || "Hesap silinemedi."));
                                return;
                            }
                            activeAccountId = null;
                            await loadAccounts();
                        } catch (err) {
                            alert("Hata oluştu.");
                        }
                    };

                    if (acc.balance > 0) {
                        const otherAccounts = (window.latestAccountsList || []).filter(a => a.id !== acc.id);
                        
                        if (otherAccounts.length === 0) {
                            alert(currentLanguage === "tr" ? 
                                "Hesapta bakiye bulunmaktadır ve aktarabileceğiniz başka bir hesabınız yoktur. Silme işlemi yapılamaz." : 
                                "This account has a balance, but you have no other accounts to transfer it to. Cannot delete.");
                            return;
                        }

                        // Create modern dynamic modal
                        const modalId = "delete-acc-transfer-modal";
                        const existingModal = document.getElementById(modalId);
                        if (existingModal) existingModal.remove();

                        const modalEl = document.createElement("div");
                        modalEl.id = modalId;
                        modalEl.style = `
                            position: fixed;
                            top: 0;
                            left: 0;
                            width: 100%;
                            height: 100%;
                            background: rgba(0,0,0,0.85);
                            backdrop-filter: blur(8px);
                            display: flex;
                            align-items: center;
                            justify-content: center;
                            z-index: 10000;
                            transition: all 0.3s ease;
                        `;

                        const optionsHtml = otherAccounts.map(a => `<option value="${a.id}">${a.accountNumber} (${a.balance.toFixed(2)} ${a.currency})</option>`).join("");

                        modalEl.innerHTML = `
                            <div class="card glassmorphism" style="width: 440px; padding: 2rem; border: 1px solid rgba(255,255,255,0.08); border-radius: 20px; background: rgba(15, 23, 42, 0.98); box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.5); animation: modalFadeIn 0.3s ease;">
                                <h3 style="margin-bottom: 0.5rem; font-size: 1.25rem; font-weight: 700; color: #fff; letter-spacing: -0.025em;">
                                    ${currentLanguage === "tr" ? "Hesap Kapatma Bakiye Aktarımı" : "Account Closure Balance Transfer"}
                                </h3>
                                <p style="font-size: 0.85rem; color: var(--text-muted); margin-bottom: 1.5rem; line-height: 1.5;">
                                    ${currentLanguage === "tr" ? `Silmek istediğiniz hesapta <strong>${acc.balance.toFixed(2)} ${acc.currency}</strong> bakiye bulunmaktadır. Silmeden önce bakiyenizin aktarılacağı diğer hesabınızı seçin:` : `The account you want to delete has a balance of <strong>${acc.balance.toFixed(2)} ${acc.currency}</strong>. Please select the target account to transfer your balance:`}
                                </p>
                                <div class="form-group" style="margin-bottom: 1.5rem;">
                                    <label style="font-size: 0.75rem; font-weight: 600; color: var(--text-muted); display: block; margin-bottom: 0.5rem; text-transform: uppercase; letter-spacing: 0.05em;">
                                        ${currentLanguage === "tr" ? "Bakiyenin Aktarılacağı Hedef Hesap" : "Target Account For Transfer"}
                                    </label>
                                    <select id="delete-acc-target-select" class="form-control" style="width: 100%; padding: 0.75rem; background: #0f172a !important; border: 1px solid rgba(255,255,255,0.15) !important; color: #fff !important; border-radius: 10px; font-size: 0.85rem; outline: none !important; box-shadow: none !important;">
                                        ${optionsHtml}
                                    </select>
                                </div>
                                <div style="display: flex; gap: 1rem; justify-content: flex-end;">
                                    <button id="delete-acc-cancel-btn" class="btn btn-secondary" style="padding: 0.6rem 1.2rem; font-size: 0.8rem; border-radius: 10px; font-weight: 600;">
                                        ${currentLanguage === "tr" ? "Vazgeç" : "Cancel"}
                                    </button>
                                    <button id="delete-acc-confirm-btn" class="btn btn-primary" style="padding: 0.6rem 1.2rem; font-size: 0.8rem; border-radius: 10px; font-weight: 600; background: linear-gradient(135deg, #00f260, #0575e6); border: none; color: #fff; cursor: pointer;">
                                        ${currentLanguage === "tr" ? "Aktar ve Hesabı Kapat" : "Transfer & Close Account"}
                                    </button>
                                </div>
                            </div>
                        `;
                        
                        document.body.appendChild(modalEl);

                        document.getElementById("delete-acc-cancel-btn").addEventListener("click", () => {
                            modalEl.remove();
                        });

                        document.getElementById("delete-acc-confirm-btn").addEventListener("click", async () => {
                            const targetId = document.getElementById("delete-acc-target-select").value;
                            modalEl.remove();
                            await executeAccountDeletion(acc.id, targetId);
                        });
                    } else {
                        const confirmationMsg = currentLanguage === "tr" ? 
                            "Bu hesabı kalıcı olarak kapatmak istediğinize emin misiniz?" : 
                            "Are you sure you want to permanently close this account?";
                        if (!confirm(confirmationMsg)) return;
                        await executeAccountDeletion(acc.id, null);
                    }
                });
            }



            // Populate Dropdown option
            const opt = document.createElement("option");
            opt.value = acc.accountNumber;
            opt.textContent = `${acc.accountNumber} (${balanceStr})`;
            sourceSelect.appendChild(opt);
        });

        // Auto-select first account to load transactions if none active
        if (!activeAccountId && accounts.length > 0) {
            activeAccountId = accounts[0].id;
            const firstCard = document.querySelectorAll("#accounts-list .account-card")[0];
            if (firstCard) firstCard.classList.add("active");
            loadTransactions(activeAccountId);
        }

        // No sync card preview for active account anymore (only syncs for credit cards)
    } catch (err) {
        listEl.innerHTML = '<div class="alert alert-danger">Server connection failed.</div>';
    }
}

async function loadTransactions(accountId) {
    const bodyEl = document.getElementById("transactions-body");
    const loadMoreBtn = document.getElementById("btn-tx-load-more");
    bodyEl.innerHTML = '<tr><td colspan="4" class="text-center">Loading transactions...</td></tr>';

    try {
        const response = await fetch(`${API_URL}/banking/transactions/${accountId}`, {
            headers: { "Authorization": `Bearer ${currentToken}` }
        });

        if (!response.ok) {
            bodyEl.innerHTML = '<tr><td colspan="4" class="text-center text-danger">Failed to load history.</td></tr>';
            return;
        }

        const transactions = await response.json();
        bodyEl.innerHTML = "";

        const activeCardEl = document.querySelector(".account-card.active");
        const activeCardNo = activeCardEl ? activeCardEl.querySelector(".account-number").textContent : "";
        const activeCardBalanceText = activeCardEl ? activeCardEl.querySelector(".account-balance").textContent : "0";
        const runningBalance = parseFloat(activeCardBalanceText);

        // Helper function for category icons
        const getCategoryIcon = (category, desc) => {
            const d = (desc || "").toLowerCase();
            const c = (category || "").toLowerCase();
            if (c === "market" || d.includes("market") || d.includes("yemek") || d.includes("gıda") || d.includes("grocery") || d.includes("shop") || d.includes("bakkal") || d.includes("kahve") || d.includes("coffee") || d.includes("starbucks")) {
                return "🛒";
            }
            if (c === "fatura" || d.includes("fatura") || d.includes("bill") || d.includes("elektrik") || d.includes("su") || d.includes("doğalgaz") || d.includes("dogalgaz") || d.includes("internet") || d.includes("telefon") || d.includes("tlf")) {
                return "📄";
            }
            if (c === "eğlence" || d.includes("eğlence") || d.includes("eglence") || d.includes("sinema") || d.includes("netflix") || d.includes("game") || d.includes("oyun") || d.includes("steam") || d.includes("spotify") || d.includes("music")) {
                return "🍿";
            }
            if (c === "yatırım" || d.includes("yatırım") || d.includes("yatirim") || d.includes("hisse") || d.includes("stock") || d.includes("crypto") || d.includes("btc") || d.includes("altın") || d.includes("altin")) {
                return "📈";
            }
            return "💸";
        };

        // Handle load more button visibility and text
        if (loadMoreBtn) {
            if (transactions.length > 5) {
                loadMoreBtn.style.display = "inline-block";
                if (showAllTransactions) {
                    loadMoreBtn.textContent = currentLanguage === "tr" ? "Daha Az Göster ▲" : "Show Less ▲";
                } else {
                    loadMoreBtn.textContent = currentLanguage === "tr" ? `Daha Fazla Göster (${transactions.length - 5} işlem daha) ▼` : `Show More (${transactions.length - 5} more) ▼`;
                }
                loadMoreBtn.onclick = () => {
                    showAllTransactions = !showAllTransactions;
                    loadTransactions(accountId);
                };
            } else {
                loadMoreBtn.style.display = "none";
            }
        }

        const visibleTransactions = showAllTransactions ? transactions : transactions.slice(0, 5);

        // 1. Render transaction rows
        if (transactions.length === 0) {
            bodyEl.innerHTML = `<tr><td colspan="4" class="text-center text-muted" id="txt-no-tx">${getLocalizedText("txt-no-tx", "Select an account to view history")}</td></tr>`;
        } else {
            visibleTransactions.forEach(tx => {
                const date = new Date(tx.createdAt).toLocaleDateString(currentLanguage === "tr" ? "tr-TR" : "en-US", {
                    month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit'
                });

                let isOutgoing = false;
                if (tx.type === "Deposit" || tx.type === "DepositMoney") {
                    isOutgoing = false;
                } else if (tx.type === "Transfer") {
                    if (tx.destinationAccountNumber === activeCardNo) {
                        isOutgoing = false;
                    } else if (tx.sourceAccountNumber === activeCardNo) {
                        isOutgoing = true;
                    }
                } else if (tx.type === "Exchange" || tx.type === "ExchangeMoney") {
                    if (tx.sourceAccountNumber === activeCardNo && tx.destinationAccountNumber !== activeCardNo) {
                        isOutgoing = true;
                    } else if (tx.destinationAccountNumber === activeCardNo && tx.sourceAccountNumber !== activeCardNo) {
                        isOutgoing = false;
                    } else {
                        isOutgoing = tx.description.toLowerCase().includes("alımı") || tx.description.toLowerCase().includes("buy");
                    }
                } else {
                    isOutgoing = tx.sourceAccountNumber === activeCardNo;
                }
                const amountPrefix = isOutgoing ? "-" : "+";
                const amountClass = isOutgoing ? "tx-amount-negative" : "tx-amount-positive";
                const icon = getCategoryIcon(tx.category, tx.description);

                const row = document.createElement("tr");
                row.style.cursor = "pointer";
                row.innerHTML = `
                    <td>${date}</td>
                    <td><span class="badge-role">${getLocalizedText(tx.type, tx.type)}</span></td>
                    <td><span style="margin-right: 0.5rem; font-size: 1.1rem;">${icon}</span>${tx.description || "-"}</td>
                    <td class="text-right ${amountClass}">${amountPrefix}${tx.amount.toFixed(2)}</td>
                `;
                row.addEventListener("click", () => {
                    showTransactionSlip(tx);
                });
                bodyEl.appendChild(row);
            });
        }

        // 2. Calculate category statistics (Doughnut Chart)
        let categoriesSum = {
            Market: 0,
            Fatura: 0,
            Eğlence: 0,
            Yatırım: 0,
            Diğer: 0
        };

        transactions.forEach(tx => {
            let isOutgoing = false;
            if (tx.type === "Deposit" || tx.type === "DepositMoney") {
                isOutgoing = false;
            } else if (tx.type === "Transfer") {
                isOutgoing = (tx.sourceAccountNumber === activeCardNo && tx.destinationAccountNumber !== activeCardNo);
            } else if (tx.type === "Exchange" || tx.type === "ExchangeMoney") {
                isOutgoing = (tx.sourceAccountNumber === activeCardNo && tx.destinationAccountNumber !== activeCardNo) || 
                             (tx.sourceAccountNumber === activeCardNo && (tx.description.toLowerCase().includes("alımı") || tx.description.toLowerCase().includes("buy")));
            } else {
                isOutgoing = tx.sourceAccountNumber === activeCardNo;
            }
            if (isOutgoing) {
                const desc = (tx.description || "").toLowerCase();
                if (desc.includes("market") || desc.includes("yemek") || desc.includes("gıda") || desc.includes("grocery") || desc.includes("shop") || desc.includes("bakkal")) {
                    categoriesSum.Market += tx.amount;
                } else if (desc.includes("fatura") || desc.includes("bill") || desc.includes("elektrik") || desc.includes("su") || desc.includes("doğalgaz") || desc.includes("dogalgaz") || desc.includes("internet") || desc.includes("telefon") || desc.includes("tlf")) {
                    categoriesSum.Fatura += tx.amount;
                } else if (desc.includes("eğlence") || desc.includes("eglence") || desc.includes("sinema") || desc.includes("netflix") || desc.includes("game") || desc.includes("oyun") || desc.includes("steam") || desc.includes("spotify") || desc.includes("music")) {
                    categoriesSum.Eğlence += tx.amount;
                } else if (desc.includes("yatırım") || desc.includes("yatirim") || desc.includes("hisse") || desc.includes("stock") || desc.includes("crypto") || desc.includes("btc") || desc.includes("altın") || desc.includes("altin")) {
                    categoriesSum.Yatırım += tx.amount;
                } else {
                    categoriesSum.Diğer += tx.amount;
                }
            }
        });

        // 3. Calculate balance trend history (Line Chart)
        let balanceHistory = [runningBalance];
        let labels = [currentLanguage === "tr" ? "Güncel" : "Current"];
        
        let tempBalance = runningBalance;
        transactions.forEach(tx => {
            const isOutgoing = tx.sourceAccountNumber === activeCardNo;
            if (isOutgoing) {
                tempBalance += tx.amount;
            } else {
                tempBalance -= tx.amount;
            }
            balanceHistory.unshift(tempBalance);
            
            const date = new Date(tx.createdAt).toLocaleDateString(currentLanguage === "tr" ? "tr-TR" : "en-US", {
                month: 'short', day: 'numeric'
            });
            labels.unshift(date);
        });

        if (balanceHistory.length > 7) {
            balanceHistory = balanceHistory.slice(-7);
            labels = labels.slice(-7);
        }

        if (balanceHistory.length <= 1) {
            const mockMonths = currentLanguage === "tr" ? ["20 Haz", "21 Haz", "22 Haz", "23 Haz", "24 Haz"] : ["Jun 20", "Jun 21", "Jun 22", "Jun 23", "Jun 24"];
            for (let i = 4; i >= 0; i--) {
                labels.unshift(mockMonths[i]);
                balanceHistory.unshift(runningBalance - (i + 1) * 100);
            }
        }

        // 4. Update Charts
        updateSpendingChart(categoriesSum);
        updateTrendChart(labels, balanceHistory);

    } catch (err) {
        bodyEl.innerHTML = '<tr><td colspan="4" class="text-center text-danger">Server connection failed.</td></tr>';
    }
}

function updateSpendingChart(categoriesSum) {
    const ctx = document.getElementById('spendingChart');
    if (!ctx) return;

    if (spendingChartInstance) {
        spendingChartInstance.destroy();
    }

    const labels = Object.keys(categoriesSum);
    const data = Object.values(categoriesSum);
    const total = data.reduce((a, b) => a + b, 0);

    let chartData, chartLabels, chartColors;

    if (total === 0) {
        chartData = [1];
        chartLabels = [currentLanguage === 'tr' ? 'Harcama Yok' : 'No Spendings'];
        chartColors = ['rgba(255, 255, 255, 0.1)'];
    } else {
        chartData = data;
        chartLabels = labels.map(l => currentLanguage === 'tr' ? l : (l === 'Eğlence' ? 'Leisure' : l === 'Yatırım' ? 'Investment' : l === 'Diğer' ? 'Others' : l));
        chartColors = [
            '#00f260', // Market
            '#0575e6', // Fatura
            '#f5af19', // Eğlence
            '#8a2be2', // Yatırım
            '#ff4b5c'  // Diğer
        ];
    }

    spendingChartInstance = new Chart(ctx, {
        type: 'doughnut',
        data: {
            labels: chartLabels,
            datasets: [{
                data: chartData,
                backgroundColor: chartColors,
                borderWidth: 1,
                borderColor: 'rgba(255,255,255,0.05)'
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
                legend: {
                    position: 'bottom',
                    labels: {
                        color: 'hsl(215, 20%, 65%)',
                        font: { size: 10, family: 'Inter' },
                        boxWidth: 12
                    }
                }
            },
            cutout: '60%'
        }
    });
}

function updateTrendChart(labels, balanceHistory) {
    const ctx = document.getElementById('trendChart');
    if (!ctx) return;

    if (trendChartInstance) {
        trendChartInstance.destroy();
    }

    trendChartInstance = new Chart(ctx, {
        type: 'line',
        data: {
            labels: labels,
            datasets: [{
                label: currentLanguage === 'tr' ? 'Bakiye (TRY)' : 'Balance (TRY)',
                data: balanceHistory,
                borderColor: '#00f260',
                backgroundColor: 'rgba(0, 242, 96, 0.15)',
                borderWidth: 2,
                fill: true,
                tension: 0.4,
                pointBackgroundColor: '#00f260',
                pointBorderColor: 'rgba(255,255,255,0.8)',
                pointRadius: 4
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
                legend: { display: false }
            },
            scales: {
                x: {
                    grid: { color: 'rgba(255, 255, 255, 0.03)' },
                    ticks: { color: 'hsl(215, 20%, 65%)', font: { size: 9 } }
                },
                y: {
                    grid: { color: 'rgba(255, 255, 255, 0.03)' },
                    ticks: { color: 'hsl(215, 20%, 65%)', font: { size: 9 } }
                }
            }
        }
    });
}

function updateCardPreview(cardNumber, cvv, expiryDate, theme, typeText) {
    const previewNum = document.getElementById("preview-card-number");
    const previewExpiry = document.getElementById("preview-card-expiry");
    const previewCvv = document.getElementById("preview-card-cvv");
    const previewType = document.getElementById("preview-card-type-badge");
    const previewTheme = document.getElementById("debit-card-preview");

    if (previewNum) {
        let formatted = cardNumber ? cardNumber.replace(/\s?/g, '').replace(/(\d{4})/g, '$1 ').trim() : "**** **** **** ****";
        if (typeText === "CREDIT CARD" && cardNumber && cardNumber.replace(/\s?/g, '').length >= 12) {
            const raw = cardNumber.replace(/\s?/g, '');
            formatted = "**** **** **** " + raw.slice(-4);
        }
        previewNum.textContent = formatted;
    }
    if (previewExpiry) {
        previewExpiry.textContent = "EXP " + (expiryDate || "12/31");
    }
    if (previewCvv) {
        previewCvv.textContent = cvv || "000";
    }
    if (previewType) {
        previewType.textContent = typeText || "DEBIT";
    }
    if (previewTheme) {
        previewTheme.className = "debit-card card-front " + (theme || "theme-neon-blue");
        
        const previewThemeBack = document.getElementById("debit-card-preview-back");
        if (previewThemeBack) {
            previewThemeBack.className = "debit-card card-back " + (theme || "theme-neon-blue");
        }

        // Sync active state in customizer buttons
        const themeButtons = document.querySelectorAll(".theme-btn");
        themeButtons.forEach(btn => {
            if (btn.getAttribute("data-theme") === theme) {
                btn.classList.add("active");
            } else {
                btn.classList.remove("active");
            }
        });
    }
}

function initCardCustomizer() {
    const cardPreview = document.getElementById("debit-card-preview");
    const cardHolder = document.getElementById("preview-card-holder");
    const themeButtons = document.querySelectorAll(".theme-btn");

    if (cardHolder && currentUser) {
        const name = currentUser.fullName.toUpperCase();
        cardHolder.textContent = name;
        if (name.length > 20) {
            cardHolder.style.fontSize = "0.55rem";
        } else if (name.length > 15) {
            cardHolder.style.fontSize = "0.62rem";
        } else {
            cardHolder.style.fontSize = "0.75rem";
        }
    }

    themeButtons.forEach(btn => {
        btn.addEventListener("click", () => {
            themeButtons.forEach(b => b.classList.remove("active"));
            btn.classList.add("active");

            const newTheme = btn.getAttribute("data-theme");
            if (cardPreview) {
                cardPreview.className = "debit-card " + newTheme;
            }
        });
    });

    if (cardPreview) {
        cardPreview.addEventListener("mousemove", (e) => {
            const rect = cardPreview.getBoundingClientRect();
            const x = e.clientX - rect.left;
            const y = e.clientY - rect.top;

            const normX = (x / rect.width) - 0.5;
            const normY = (y / rect.height) - 0.5;

            const maxRotate = 15;
            const rotateY = normX * maxRotate;
            const rotateX = -normY * maxRotate;

            cardPreview.style.transform = `rotateY(${rotateY}deg) rotateX(${rotateX}deg)`;
            cardPreview.style.setProperty('--mouse-x', `${(x / rect.width) * 100}%`);
            cardPreview.style.setProperty('--mouse-y', `${(y / rect.height) * 100}%`);
        });

        cardPreview.addEventListener("mouseleave", () => {
            cardPreview.style.transform = "rotateY(0deg) rotateX(0deg)";
        });
    }
}

function initDashboardEvents() {
    document.getElementById("transfer-form").addEventListener("submit", async (e) => {
        e.preventDefault();
        const sourceAccountNumber = document.getElementById("transfer-source").value;
        const destinationAccountNumber = document.getElementById("transfer-dest").value.trim();
        const amount = parseFloat(document.getElementById("transfer-amount").value);
        const description = document.getElementById("transfer-desc-input").value.trim();
        const category = document.getElementById("transfer-category-input").value;
        const msgEl = document.getElementById("transfer-message");

        msgEl.className = "alert hidden";

        try {
            const response = await fetch(`${API_URL}/banking/transfer`, {
                method: "POST",
                headers: { 
                    "Content-Type": "application/json",
                    "Authorization": `Bearer ${currentToken}`
                },
                body: JSON.stringify({ sourceAccountNumber, destinationAccountNumber, amount, description, category })
            });

            const data = await response.json();

            if (!response.ok) {
                // Check if OTP verification is required
                if (data.errorKey === "Requires2FA" || 
                    data.errorKey === "SuspectedFraudDuplicate" || 
                    data.errorKey === "SuspectedFraudHighValue") {
                    
                    // Parse OTP code from message (format: "message|OTP:######")
                    let reasonMsg = data.message || "";
                    let otpCode = "";
                    if (reasonMsg.includes("|OTP:")) {
                        const parts = reasonMsg.split("|OTP:");
                        reasonMsg = parts[0];
                        otpCode = parts[1];
                    }

                    // Localize the reason key or fall back to parsed reasonMsg
                    const localizedReason = getLocalizedText(data.errorKey, reasonMsg);

                    // Show Simulated SMS Toast containing the OTP code
                    showMockSMSToast(otpCode);

                    // Show OTP Modal
                    showOTPModal(localizedReason, async (codeEntered) => {
                        // Submit code to execute transfer
                        try {
                            const secondResponse = await fetch(`${API_URL}/banking/transfer`, {
                                method: "POST",
                                headers: { 
                                    "Content-Type": "application/json",
                                    "Authorization": `Bearer ${currentToken}`
                                },
                                body: JSON.stringify({ 
                                    sourceAccountNumber, 
                                    destinationAccountNumber, 
                                    amount, 
                                    description,
                                    category,
                                    otpCode: codeEntered 
                                })
                            });

                            const secondData = await secondResponse.json();

                            if (!secondResponse.ok) {
                                return {
                                    success: false,
                                    message: getLocalizedText(secondData.errorKey, secondData.message || "Verification failed.")
                                };
                            }

                            // Success!
                            msgEl.textContent = getLocalizedText("TransferSuccess", "Transfer executed successfully!");
                            msgEl.className = "alert alert-success";
                            if (typeof handleSaveContactAfterTransfer === "function") {
                                handleSaveContactAfterTransfer(destinationAccountNumber);
                            }

                            // Reset inputs
                            document.getElementById("transfer-dest").value = "";
                            document.getElementById("transfer-amount").value = "";
                            document.getElementById("transfer-desc-input").value = "";

                            // Refresh cards and transactions
                            loadAccounts();
                            if (activeAccountId) {
                                loadTransactions(activeAccountId);
                            }
                            
                            // Celebrate!
                            if (typeof confetti === "function") {
                                confetti({
                                    particleCount: 120,
                                    spread: 80,
                                    origin: { y: 0.6 }
                                });
                            }

                            return { success: true };
                        } catch (err) {
                            return { success: false, message: "Connection to server failed." };
                        }
                    });

                    return;
                }

                msgEl.textContent = getLocalizedText(data.errorKey, data.message || "Transfer failed");
                msgEl.className = "alert alert-danger";
                return;
            }

            msgEl.textContent = getLocalizedText("TransferSuccess", "Transfer executed successfully!");
            msgEl.className = "alert alert-success";
            if (typeof handleSaveContactAfterTransfer === "function") {
                handleSaveContactAfterTransfer(destinationAccountNumber);
            }

            // Reset inputs
            document.getElementById("transfer-dest").value = "";
            document.getElementById("transfer-amount").value = "";
            document.getElementById("transfer-desc-input").value = "";

            // Refresh cards and transactions
            loadAccounts();
            if (activeAccountId) {
                loadTransactions(activeAccountId);
            }
        } catch (err) {
            msgEl.textContent = "Server connection failed.";
            msgEl.className = "alert alert-danger";
        }
    });
}

/* ==========================================================================
   AGENT DASHBOARD LOGIC (agent.html)
   ========================================================================== */
let selectedSessionId = null;

async function loadActiveSessions() {
    const listEl = document.getElementById("active-sessions-list");
    listEl.innerHTML = '<div class="loading-spinner">Loading chats...</div>';

    try {
        const response = await fetch(`${API_URL}/chat/active-sessions`, {
            headers: { "Authorization": `Bearer ${currentToken}` }
        });

        if (!response.ok) {
            listEl.innerHTML = '<div class="alert alert-danger">Failed to load active chats.</div>';
            return;
        }

        const sessions = await response.json();
        listEl.innerHTML = "";

        if (sessions.length === 0) {
            listEl.innerHTML = `<div class="text-muted text-center py-4" id="txt-no-active-chats">${getLocalizedText("txt-no-active-chats", "No active chats")}</div>`;
            return;
        }

        sessions.forEach(sess => {
            const item = document.createElement("div");
            item.className = `session-item ${selectedSessionId === sess.id ? 'active' : ''}`;
            
            const date = new Date(sess.createdAt).toLocaleTimeString(currentLanguage === "tr" ? "tr-TR" : "en-US", {
                hour: '2-digit', minute: '2-digit'
            });

            item.innerHTML = `
                <h5>${sess.title}</h5>
                <p>User: <strong>${sess.username}</strong> | ${date}</p>
            `;

            item.addEventListener("click", () => {
                document.querySelectorAll(".session-item").forEach(i => i.classList.remove("active"));
                item.classList.add("active");
                loadAgentChat(sess.id, sess.title);
            });

            listEl.appendChild(item);
        });
    } catch (err) {
        listEl.innerHTML = '<div class="alert alert-danger">Server connection failed.</div>';
    }
}

async function loadAgentChat(sessionId, title) {
    selectedSessionId = sessionId;

    const placeholder = document.getElementById("agent-placeholder");
    if (placeholder) placeholder.classList.add("hidden");

    document.getElementById("agent-chat-header").classList.remove("hidden");
    document.getElementById("agent-chat-form").classList.remove("hidden");
    
    document.getElementById("agent-chat-title").textContent = title;
    document.getElementById("agent-chat-session-id").textContent = `Session ID: ${sessionId}`;

    const msgContainer = document.getElementById("agent-chat-messages");
    msgContainer.innerHTML = '<div class="loading-spinner">Loading conversation...</div>';

    try {
        const response = await fetch(`${API_URL}/chat/messages/${sessionId}`, {
            headers: { "Authorization": `Bearer ${currentToken}` }
        });

        if (!response.ok) {
            msgContainer.innerHTML = '<div class="alert alert-danger">Failed to load message history.</div>';
            return;
        }

        const messages = await response.json();
        msgContainer.innerHTML = "";

        messages.forEach(msg => {
            const bubble = document.createElement("div");
            // Roles: "User", "AI", "Agent" -> maps to css class
            const roleClass = msg.sender.toLowerCase();
            bubble.className = `message-bubble ${roleClass}`;
            
            const time = new Date(msg.createdAt).toLocaleTimeString(currentLanguage === "tr" ? "tr-TR" : "en-US", {
                hour: '2-digit', minute: '2-digit'
            });

            bubble.innerHTML = `
                ${msg.content}
                <span class="message-timestamp">${time}</span>
            `;
            msgContainer.appendChild(bubble);
        });

        // Scroll to bottom
        msgContainer.scrollTop = msgContainer.scrollHeight;

        // Initialize/Join SignalR connection for this room
        if (typeof joinAgentChatSession === "function") {
            joinAgentChatSession(sessionId);
        }

        // Fetch AI suggestion
        fetchCoPilotSuggestion(sessionId);
    } catch (err) {
        msgContainer.innerHTML = '<div class="alert alert-danger">Server connection failed.</div>';
    }
}

function initAgentEvents() {
    document.getElementById("btn-refresh-sessions").addEventListener("click", loadActiveSessions);

    document.getElementById("btn-close-session").addEventListener("click", async () => {
        if (!selectedSessionId) return;

        try {
            // Tell Hub to close session
            if (typeof signalRConnection !== "undefined" && signalRConnection.state === "Connected") {
                await signalRConnection.invoke("CloseSessionAsync", selectedSessionId);
            }

            // Close session via API just to be sure
            await loadActiveSessions();
            
            // Clean interface
            document.getElementById("agent-chat-header").classList.add("hidden");
            document.getElementById("agent-chat-form").classList.add("hidden");
            document.getElementById("agent-chat-messages").innerHTML = `
                <div class="agent-chat-placeholder" id="agent-placeholder">
                    <span class="chat-placeholder-icon">📥</span>
                    <h3 id="txt-select-chat">${getLocalizedText("txt-select-chat", "Select a Support Session")}</h3>
                    <p class="text-muted" id="txt-select-chat-desc">${getLocalizedText("txt-select-chat-desc", "Click sidebar...")}</p>
                </div>
            `;
            selectedSessionId = null;
        } catch (err) {
            alert("Could not close session.");
        }
    });

    document.getElementById("agent-chat-form").addEventListener("submit", async (e) => {
        e.preventDefault();
        const inputEl = document.getElementById("agent-chat-input");
        const text = inputEl.value.trim();
        if (!text || !selectedSessionId) return;

        try {
            if (typeof signalRConnection !== "undefined" && signalRConnection.state === "Connected") {
                await signalRConnection.invoke("SendMessageAsync", selectedSessionId, text);
                inputEl.value = "";
            } else {
                alert("SignalR Connection is offline.");
            }
        } catch (err) {
            alert("Failed to send message.");
        }
    });
}

/* ==========================================================================
   Phase 3 Security, 2FA, OTP & Mock SMS Helpers
   ========================================================================== */

async function load2FAStatus() {
    const switch2Fa = document.getElementById("switch-2fa");
    if (!switch2Fa) return;

    try {
        const response = await fetch(`${API_URL}/auth/2fa-status`, {
            headers: { "Authorization": `Bearer ${currentToken}` }
        });

        if (response.ok) {
            const data = await response.json();
            switch2Fa.checked = data.enabled;
        }
    } catch (err) {
        console.error("Failed to load 2FA status:", err);
    }
}

function init2FASettings() {
    const switch2Fa = document.getElementById("switch-2fa");
    if (!switch2Fa) return;

    switch2Fa.addEventListener("change", async () => {
        const enable = switch2Fa.checked;
        try {
            const response = await fetch(`${API_URL}/auth/toggle-2fa`, {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "Authorization": `Bearer ${currentToken}`
                },
                body: JSON.stringify({ enable })
            });

            if (!response.ok) {
                // Revert switch status on error
                switch2Fa.checked = !enable;
                alert("Failed to update 2FA status.");
            }
        } catch (err) {
            switch2Fa.checked = !enable;
            alert("Connection error.");
        }
    });
}

function showMockSMSToast(otpCode) {
    const toast = document.getElementById("mock-sms-toast");
    const textEl = document.getElementById("sms-text");
    if (!toast || !textEl) return;

    const messageTemplate = currentLanguage === "tr" 
        ? `SmartBank SMS: Güvenlik doğrulama kodunuz: ${otpCode}. Lütfen bu kodu kimseyle paylaşmayın.` 
        : `SmartBank SMS: Your security verification code is ${otpCode}. Do not share it.`;

    textEl.textContent = messageTemplate;
    toast.classList.remove("hidden");
    
    // Trigger animation
    setTimeout(() => {
        toast.classList.add("show");
    }, 50);

    // Hide after 8 seconds
    setTimeout(() => {
        toast.classList.remove("show");
        setTimeout(() => {
            toast.classList.add("hidden");
        }, 500);
    }, 8000);
}

let currentOtpCallback = null;

function showOTPModal(reasonMessage, confirmCallback) {
    const modal = document.getElementById("otp-modal");
    const descEl = document.getElementById("otp-modal-desc");
    const inputEl = document.getElementById("otp-code-input");
    const errorEl = document.getElementById("otp-error-msg");
    const submitBtn = document.getElementById("btn-submit-otp");

    if (!modal || !descEl || !inputEl || !errorEl || !submitBtn) return;

    descEl.textContent = reasonMessage;
    inputEl.value = "";
    errorEl.classList.add("hidden");
    errorEl.textContent = "";
    submitBtn.disabled = false;
    
    currentOtpCallback = confirmCallback;

    modal.classList.remove("hidden");
    inputEl.focus();
}

function initOTPModalEvents() {
    const modal = document.getElementById("otp-modal");
    const closeBtn = document.getElementById("btn-close-otp");
    const submitBtn = document.getElementById("btn-submit-otp");
    const inputEl = document.getElementById("otp-code-input");
    const errorEl = document.getElementById("otp-error-msg");

    if (!modal) return;

    const closeModal = () => {
        modal.classList.add("hidden");
        currentOtpCallback = null;
    };

    if (closeBtn) {
        closeBtn.addEventListener("click", closeModal);
    }

    // Modal click outer close
    modal.addEventListener("click", (e) => {
        if (e.target === modal) {
            closeModal();
        }
    });

    const submitOtp = async () => {
        const code = inputEl.value.trim();
        if (code.length !== 6) {
            errorEl.textContent = currentLanguage === "tr" ? "Lütfen 6 haneli kodu girin." : "Please enter a 6-digit code.";
            errorEl.classList.remove("hidden");
            return;
        }

        submitBtn.disabled = true;
        errorEl.classList.add("hidden");

        if (currentOtpCallback) {
            const result = await currentOtpCallback(code);
            if (result.success) {
                closeModal();
            } else {
                errorEl.textContent = result.message || "Verification failed.";
                errorEl.classList.remove("hidden");
                submitBtn.disabled = false;
            }
        }
    };

    if (submitBtn) {
        submitBtn.addEventListener("click", submitOtp);
    }

    if (inputEl) {
        inputEl.addEventListener("keypress", (e) => {
            if (e.key === "Enter") {
                submitOtp();
            }
        });
    }
}

/* ==========================================================================
   Phase 4 Agent Cockpit & AI Co-Pilot Helpers
   ========================================================================== */

async function loadAgentMetrics() {
    const valResolved = document.getElementById("val-metric-resolved");
    const valTime = document.getElementById("val-metric-time");
    const valCsat = document.getElementById("val-metric-csat");

    if (!valResolved) return;

    try {
        const response = await fetch(`${API_URL}/chat/agent-metrics`, {
            headers: { "Authorization": `Bearer ${currentToken}` }
        });

        if (response.ok) {
            const data = await response.json();
            valResolved.textContent = data.resolvedCount;
            valTime.textContent = data.avgResponseTime;
            valCsat.textContent = data.csatScore;
        }
    } catch (err) {
        console.error("Failed to load agent metrics:", err);
    }
}

function initAgentStatusControl() {
    const select = document.getElementById("agent-status-select");
    const dot = document.getElementById("status-indicator-dot");

    if (!select || !dot) return;

    select.addEventListener("change", () => {
        const status = select.value;
        
        // Remove existing classes
        dot.className = "status-dot";
        
        if (status === "Active") {
            dot.classList.add("online");
        } else if (status === "Busy") {
            dot.classList.add("busy");
        } else if (status === "Break") {
            dot.classList.add("break");
        }
    });
}

async function fetchCoPilotSuggestion(sessionId) {
    const container = document.getElementById("ai-copilot-container");
    const textEl = document.getElementById("ai-suggestion-text");

    if (!container || !textEl) return;

    // Show suggestion box, show loading text
    container.classList.remove("hidden");
    textEl.textContent = currentLanguage === "tr" ? "Yapay zeka önerisi oluşturuluyor..." : "Generating suggestion...";

    try {
        const response = await fetch(`${API_URL}/chat/suggest-response/${sessionId}`, {
            headers: { "Authorization": `Bearer ${currentToken}` }
        });

        if (response.ok) {
            const data = await response.json();
            textEl.textContent = data.suggestion || (currentLanguage === "tr" ? "Öneri oluşturulamadı." : "Could not generate suggestion.");
        } else {
            textEl.textContent = currentLanguage === "tr" ? "Öneri oluşturulamadı." : "Could not generate suggestion.";
        }
    } catch (err) {
        textEl.textContent = currentLanguage === "tr" ? "Bağlantı hatası." : "Connection error.";
    }
}

function initCoPilotEvents() {
    const btnRegen = document.getElementById("btn-regenerate-suggestion");
    const btnUse = document.getElementById("btn-use-suggestion");
    const inputEl = document.getElementById("agent-chat-input");

    if (btnRegen) {
        btnRegen.addEventListener("click", () => {
            if (selectedSessionId) {
                fetchCoPilotSuggestion(selectedSessionId);
            }
        });
    }

    if (btnUse) {
        btnUse.addEventListener("click", () => {
            const textEl = document.getElementById("ai-suggestion-text");
            if (textEl && inputEl) {
                const text = textEl.textContent.trim();
                // Filter out default loading/error messages
                if (text && 
                    !text.startsWith("Generating") && 
                    !text.startsWith("Yapay zeka") && 
                    !text.startsWith("Could not") && 
                    !text.startsWith("Öneri") &&
                    !text.startsWith("Click") &&
                    !text.startsWith("Bağlantı")) {
                    
                    inputEl.value = text;
                    inputEl.focus();
                }
            }
        });
    }
}

function initTransferControlEvents() {
    const btnTransfer = document.getElementById("btn-transfer-chat");
    const selectDept = document.getElementById("select-transfer-dept");

    if (!btnTransfer || !selectDept) return;

    btnTransfer.addEventListener("click", async () => {
        const dept = selectDept.value;
        if (!dept || !selectedSessionId) return;

        btnTransfer.disabled = true;
        
        try {
            const response = await fetch(`${API_URL}/chat/transfer-session/${selectedSessionId}`, {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "Authorization": `Bearer ${currentToken}`
                },
                body: JSON.stringify({ department: dept })
            });

            if (response.ok) {
                alert(currentLanguage === "tr" ? `Sohbet başarıyla ${dept} birimine aktarıldı.` : `Chat successfully transferred to ${dept}.`);
                
                // Refresh sessions list
                loadActiveSessions();
                
                // Clear active session
                selectedSessionId = null;
                document.getElementById("agent-chat-header").classList.add("hidden");
                document.getElementById("agent-chat-form").classList.add("hidden");
                document.getElementById("ai-copilot-container").classList.add("hidden");
                document.getElementById("agent-chat-messages").innerHTML = `
                    <div class="agent-chat-placeholder" id="agent-placeholder">
                        <span class="chat-placeholder-icon">📥</span>
                        <h3 id="txt-select-chat">${getLocalizedText("txt-select-chat", "Select a Support Session")}</h3>
                        <p class="text-muted" id="txt-select-chat-desc">${getLocalizedText("txt-select-chat-desc", "Click sidebar...")}</p>
                    </div>
                `;
            } else {
                alert("Failed to transfer chat.");
            }
        } catch (err) {
            alert("Connection error.");
        } finally {
            btnTransfer.disabled = false;
            selectDept.value = "";
        }
    });
}

/* ==========================================================================
   Canlı Piyasalar Ticker & Fiyat Güncelleme (index.html)
   ========================================================================== */
let previousRates = {};

async function loadMarketRates() {
    const listEl = document.getElementById("market-rates-list");
    const updatedEl = document.getElementById("txt-rates-updated");

    try {
        const response = await fetch(`${API_URL}/market/rates`);
        if (!response.ok) return;

        const rates = await response.json();
        activeMarketRates = rates;
        if (typeof updateExchangeRateDisplay === "function") {
            updateExchangeRateDisplay();
        }

        if (listEl) {
            listEl.innerHTML = "";

            const now = new Date();
            const timeString = now.toLocaleTimeString(currentLanguage === "tr" ? "tr-TR" : "en-US");
            if (updatedEl) {
                updatedEl.textContent = `${getLocalizedText("txt-rates-updated", "Son Güncelleme: ")} ${timeString}`;
            }

            rates.forEach(rate => {
                const key = rate.code;
                const prev = previousRates[key];
                previousRates[key] = rate.sell;

                let directionClass = "";
                let trendSymbol = "•";
                let flashClass = "";

                if (prev !== undefined) {
                    if (rate.sell > prev) {
                        directionClass = "up";
                        trendSymbol = "▲";
                        flashClass = "flash-green";
                    } else if (rate.sell < prev) {
                        directionClass = "down";
                        trendSymbol = "▼";
                        flashClass = "flash-red";
                    }
                }

                if (!directionClass) {
                    if (rate.change > 0) {
                        directionClass = "up";
                        trendSymbol = "▲";
                    } else if (rate.change < 0) {
                        directionClass = "down";
                        trendSymbol = "▼";
                    }
                }

                const row = document.createElement("div");
                row.className = "rate-row";
                
                const badgeClass = rate.code.toLowerCase();
                const displayName = currentLanguage === "tr" ? rate.name : rate.nameEn;

                row.innerHTML = `
                    <div class="rate-info">
                        <div class="rate-symbol-badge ${badgeClass}">
                            ${rate.code === 'USD' ? '💵' : rate.code === 'EUR' ? '💶' : rate.code === 'XAU' ? '🪙' : '🥈'}
                        </div>
                        <div class="rate-name-wrapper">
                            <span class="rate-code">${rate.code}</span>
                            <span class="rate-name">${displayName}</span>
                        </div>
                    </div>
                    <div class="rate-prices">
                        <div class="price-box">
                            <span class="price-label">${currentLanguage === "tr" ? "ALIŞ" : "BUY"}</span>
                            <span class="price-val ${flashClass}">${rate.buy.toFixed(rate.code === 'USD' || rate.code === 'EUR' ? 4 : 2)}</span>
                        </div>
                        <div class="price-box">
                            <span class="price-label">${currentLanguage === "tr" ? "SATIŞ" : "SELL"}</span>
                            <span class="price-val ${flashClass}">${rate.sell.toFixed(rate.code === 'USD' || rate.code === 'EUR' ? 4 : 2)}</span>
                        </div>
                    </div>
                    <div class="rate-trend">
                        <span class="trend-badge ${directionClass}">
                            ${trendSymbol} ${Math.abs(rate.change).toFixed(2)}%
                        </span>
                    </div>
                `;
                listEl.appendChild(row);
            });
        }
    } catch (err) {
        // Silent catch
    }
}

function initMarketRates() {
    loadMarketRates();
    setInterval(loadMarketRates, 5000);
}

/* ==========================================================================
   Kredi Kartları & Ekstre Yönetimi (dashboard.html)
   ========================================================================== */
async function loadCreditCards() {
    const listEl = document.getElementById("credit-cards-list");
    if (!listEl) return;

    listEl.innerHTML = '<div class="loading-spinner">Yükleniyor...</div>';

    try {
        const response = await fetch(`${API_URL}/banking/credit-cards`, {
            headers: { "Authorization": `Bearer ${currentToken}` }
        });

        if (!response.ok) {
            listEl.innerHTML = '<div class="alert alert-danger">Kredi kartları yüklenemedi.</div>';
            return;
        }

        const cards = await response.json();
        listEl.innerHTML = "";

        if (cards.length === 0) {
            listEl.innerHTML = currentLanguage === "tr" ? '<div class="text-muted">Aktif kredi kartınız bulunmuyor.</div>' : '<div class="text-muted">No active credit cards found.</div>';
            const noSelectedPanel = document.getElementById("cc-no-selected");
            const detailsContent = document.getElementById("cc-details-content");
            if (detailsContent) detailsContent.classList.add("hidden");
            if (noSelectedPanel) {
                noSelectedPanel.classList.remove("hidden");
                noSelectedPanel.innerHTML = `
                    <div class="text-center" style="padding: 2rem 1rem;">
                        <span style="font-size: 3rem; display: block; margin-bottom: 1rem;">💳</span>
                        <h4 style="margin: 0 0 0.5rem 0; color: var(--accent-color); font-weight: 700;">
                            ${currentLanguage === "tr" ? "Kredi Kartınız Bulunmuyor" : "No Credit Card Found"}
                        </h4>
                        <p class="text-muted" style="font-size: 0.85rem; margin-bottom: 1.5rem; max-width: 300px; margin-left: auto; margin-right: auto;">
                            ${currentLanguage === "tr" ? "Harcamalarınızı taksitlendirmek ve SmartCredit avantajlarından yararlanmak için hemen başvurun." : "Apply now to split your payments and enjoy SmartCredit benefits."}
                        </p>
                        <button id="btn-apply-cc-empty" class="btn btn-primary btn-sm" style="width: 100%; max-width: 240px; margin: 0 auto;">
                            ${currentLanguage === "tr" ? "Hemen Başvur" : "Apply Now"}
                        </button>
                    </div>
                `;
                document.getElementById("btn-apply-cc-empty").addEventListener("click", () => {
                    const applyBtn = document.getElementById("btn-apply-creditcard");
                    if (applyBtn) applyBtn.click();
                });
            }
            return;
        }

        // Restore default state of no-selected panel when cards are present
        const noSelectedPanel = document.getElementById("cc-no-selected");
        if (noSelectedPanel) {
            noSelectedPanel.innerHTML = currentLanguage === "tr" ? 
                "💳 Lütfen detaylarını ve ekstre hareketlerini görmek istediğiniz kredi kartını seçiniz." :
                "💳 Please select a credit card to view details and statements.";
        }

        cards.forEach(card => {
            const cardEl = document.createElement("div");
            cardEl.className = `account-card credit glassmorphism ${activeCreditCardId === card.id ? 'active' : ''}`;
            const maskedNo = "**** **** **** " + card.cardNumber.slice(-4);
            
            const btnText = currentLanguage === "tr" ? "Ekstre Görüntüle" : "View Statement";
            cardEl.innerHTML = `
                <div class="account-header">
                    <span>SmartCredit</span>
                    <span class="account-currency">TRY</span>
                </div>
                <div class="account-balance">${card.currentDebt.toFixed(2)} TRY</div>
                <div class="account-number">${maskedNo}</div>
                <div class="credit-limit-info">
                    <span>Limit: ${card.cardLimit.toFixed(2)} TRY</span>
                    <span>Kalan: ${card.availableLimit.toFixed(2)} TRY</span>
                </div>
                <button class="btn btn-secondary btn-xs btn-stmt-view" style="margin-top: 0.75rem; width: 100%;">${btnText}</button>
            `;

            cardEl.addEventListener("click", () => {
                document.querySelectorAll(".account-card").forEach(c => c.classList.remove("active"));
                cardEl.classList.add("active");
                activeCreditCardId = card.id;
                
                // Deselect accounts visually when clicking credit cards
                const accountsCards = document.querySelectorAll("#accounts-list .account-card");
                accountsCards.forEach(ac => ac.classList.remove("active"));
                activeAccountId = null;

                showStatementModal(card);
                updateCardPreview(card.cardNumber, card.cardCvv, card.expiryDate, card.cardTheme, "CREDIT CARD");
            });

            listEl.appendChild(cardEl);
        });

        // Sync card preview for active credit card
        if (!activeCreditCardId && cards.length > 0) {
            activeCreditCardId = cards[0].id;
        }
        const activeCc = cards.find(x => x.id === activeCreditCardId);
        if (activeCc) {
            updateCardPreview(activeCc.cardNumber, activeCc.cardCvv, activeCc.expiryDate, activeCc.cardTheme, "CREDIT CARD");
            showStatementModal(activeCc);
        }
    } catch (err) {
        listEl.innerHTML = '<div class="alert alert-danger">Sunucu bağlantısı başarısız.</div>';
    }
}

let currentStatement = null;

async function showStatementModal(card) {
    activeCreditCardId = card.id;
    const modal = document.getElementById("statement-modal");
    
    // Reset messages and forms
    const payMsg = document.getElementById("pay-debt-message");
    if (payMsg) payMsg.className = "alert hidden";
    const payAmt = document.getElementById("pay-debt-amount");
    if (payAmt) payAmt.value = "";

    const ccPayMsg = document.getElementById("cc-pay-message");
    if (ccPayMsg) ccPayMsg.className = "alert hidden";
    const ccPayAmt = document.getElementById("cc-pay-amount");
    if (ccPayAmt) ccPayAmt.value = "";

    // Toggle on-page card details panel
    const noSelectedPanel = document.getElementById("cc-no-selected");
    const detailsContent = document.getElementById("cc-details-content");
    if (noSelectedPanel) noSelectedPanel.classList.add("hidden");
    if (detailsContent) detailsContent.classList.remove("hidden");

    // Populate static card details on page
    const maskedNo = "**** **** **** " + card.cardNumber.slice(-4);
    const maskedNoEl = document.getElementById("cc-details-masked-no");
    if (maskedNoEl) maskedNoEl.textContent = maskedNo;
    const limitEl = document.getElementById("cc-details-limit");
    if (limitEl) limitEl.textContent = `${card.cardLimit.toFixed(2)} TRY`;
    const availEl = document.getElementById("cc-details-avail");
    if (availEl) availEl.textContent = `${card.availableLimit.toFixed(2)} TRY`;
    const debtEl = document.getElementById("cc-details-debt");
    if (debtEl) debtEl.textContent = `${card.currentDebt.toFixed(2)} TRY`;

    // Populate modal body tables
    const stmtTxBodyModal = document.getElementById("statement-transactions-body");
    if (stmtTxBodyModal) stmtTxBodyModal.innerHTML = '<tr><td colspan="3" class="text-center">Yükleniyor...</td></tr>';
    const stmtTxBodyPage = document.getElementById("cc-stmt-transactions-body");
    if (stmtTxBodyPage) stmtTxBodyPage.innerHTML = '<tr><td colspan="3" class="text-center">Yükleniyor...</td></tr>';
    
    try {
        const response = await fetch(`${API_URL}/banking/credit-cards/${card.id}/statements`, {
            headers: { "Authorization": `Bearer ${currentToken}` }
        });

        if (!response.ok) return;

        const statements = await response.json();
        if (statements.length === 0) {
            const noStmt = "-";
            const zeroTry = "0.00 TRY";
            
            document.getElementById("val-stmt-period").textContent = noStmt;
            document.getElementById("val-stmt-debt").textContent = zeroTry;
            document.getElementById("val-stmt-min").textContent = zeroTry;
            document.getElementById("val-stmt-due").textContent = noStmt;

            document.getElementById("val-cc-stmt-period").textContent = noStmt;
            document.getElementById("val-cc-stmt-debt").textContent = zeroTry;
            document.getElementById("val-cc-stmt-min").textContent = zeroTry;
            document.getElementById("val-cc-stmt-due").textContent = noStmt;
            return;
        }

        const stmt = statements[0];
        currentStatement = stmt;

        document.getElementById("val-stmt-period").textContent = stmt.periodName;
        document.getElementById("val-stmt-debt").textContent = `${stmt.periodDebt.toFixed(2)} TRY`;
        document.getElementById("val-stmt-min").textContent = `${stmt.minimumPayment.toFixed(2)} TRY`;

        document.getElementById("val-cc-stmt-period").textContent = stmt.periodName;
        document.getElementById("val-cc-stmt-debt").textContent = `${stmt.periodDebt.toFixed(2)} TRY`;
        document.getElementById("val-cc-stmt-min").textContent = `${stmt.minimumPayment.toFixed(2)} TRY`;
        
        const dueDate = new Date(stmt.dueDate).toLocaleDateString(currentLanguage === "tr" ? "tr-TR" : "en-US", {
            year: 'numeric', month: 'short', day: 'numeric'
        });
        document.getElementById("val-stmt-due").textContent = dueDate;
        document.getElementById("val-cc-stmt-due").textContent = dueDate;

        const statusBanner = document.getElementById("stmt-payment-status-banner");
        const statusText = document.getElementById("stmt-status-text");
        
        const remaining = Math.max(0, stmt.periodDebt - stmt.paidAmount);
        
        if (statusBanner && statusText) {
            if (stmt.isPaid || remaining <= 0) {
                statusBanner.className = "statement-payment-status paid";
                statusText.textContent = getLocalizedText("stmt-status-paid", "Paid");
            } else {
                statusBanner.className = "statement-payment-status unpaid";
                const minRemaining = Math.max(0, stmt.minimumPayment - stmt.paidAmount);
                if (minRemaining <= 0) {
                    statusText.textContent = currentLanguage === "tr" ? `Asgari Ödendi (Kalan Borç: ${remaining.toFixed(2)} TRY)` : `Min Paid (Remaining: ${remaining.toFixed(2)} TRY)`;
                } else {
                    statusText.textContent = currentLanguage === "tr" ? `Ödenmedi (Asgari Borç: ${minRemaining.toFixed(2)} TRY)` : `Unpaid (Min Debt: ${minRemaining.toFixed(2)} TRY)`;
                }
            }
        }

        if (stmtTxBodyModal) stmtTxBodyModal.innerHTML = "";
        if (stmtTxBodyPage) stmtTxBodyPage.innerHTML = "";

        if (stmt.transactions.length === 0) {
            const noSpend = `<tr><td colspan="3" class="text-center text-muted">${currentLanguage === "tr" ? "Harcama bulunmuyor" : "No spendings"}</td></tr>`;
            if (stmtTxBodyModal) stmtTxBodyModal.innerHTML = noSpend;
            if (stmtTxBodyPage) stmtTxBodyPage.innerHTML = noSpend;
        } else {
            stmt.transactions.forEach(t => {
                const date = new Date(t.createdAt).toLocaleDateString(currentLanguage === "tr" ? "tr-TR" : "en-US", {
                    month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit'
                });
                const tr = document.createElement("tr");
                tr.innerHTML = `
                    <td>${date}</td>
                    <td>${t.description}</td>
                    <td class="text-right tx-amount-negative">-${t.amount.toFixed(2)}</td>
                `;
                const trCopy = tr.cloneNode(true);
                if (stmtTxBodyModal) stmtTxBodyModal.appendChild(tr);
                if (stmtTxBodyPage) stmtTxBodyPage.appendChild(trCopy);
            });
        }

        populatePaymentAccountsSelect();

    } catch (err) {
        // error
    }
}

async function populatePaymentAccountsSelect() {
    const select = document.getElementById("pay-debt-source");
    const selectOnPage = document.getElementById("cc-pay-source");
    
    if (select) select.innerHTML = "";
    if (selectOnPage) selectOnPage.innerHTML = "";

    try {
        const response = await fetch(`${API_URL}/banking/accounts`, {
            headers: { "Authorization": `Bearer ${currentToken}` }
        });
        if (!response.ok) return;

        const accounts = await response.json();
        const tryAccounts = accounts.filter(a => a.currency === "TRY");

        if (tryAccounts.length === 0) {
            const emptyText = currentLanguage === "tr" ? "Vadesiz TL hesabınız bulunmuyor" : "No demand TRY accounts available";
            if (select) {
                const opt = document.createElement("option");
                opt.textContent = emptyText;
                select.appendChild(opt);
            }
            if (selectOnPage) {
                const opt = document.createElement("option");
                opt.textContent = emptyText;
                selectOnPage.appendChild(opt);
            }
            return;
        }

        tryAccounts.forEach(acc => {
            if (select) {
                const opt = document.createElement("option");
                opt.value = acc.accountNumber;
                opt.textContent = `${acc.accountNumber} (${acc.balance.toFixed(2)} TRY)`;
                select.appendChild(opt);
            }
            if (selectOnPage) {
                const opt = document.createElement("option");
                opt.value = acc.accountNumber;
                opt.textContent = `${acc.accountNumber} (${acc.balance.toFixed(2)} TRY)`;
                selectOnPage.appendChild(opt);
            }
        });
    } catch (err) {
        // error
    }
}

function initCreditCardEvents() {
    const closeBtn = document.getElementById("btn-close-statement");
    if (closeBtn) {
        closeBtn.addEventListener("click", () => {
            document.getElementById("statement-modal").classList.add("hidden");
            activeCreditCardId = null;
            document.querySelectorAll(".account-card").forEach(c => c.classList.remove("active"));
        });
    }

    // Modal presets
    const btnMin = document.getElementById("btn-pay-minimum");
    if (btnMin) {
        btnMin.addEventListener("click", () => {
            if (!currentStatement) return;
            const minRemaining = Math.max(0, currentStatement.minimumPayment - currentStatement.paidAmount);
            document.getElementById("pay-debt-amount").value = minRemaining.toFixed(2);
        });
    }

    const btnFull = document.getElementById("btn-pay-full");
    if (btnFull) {
        btnFull.addEventListener("click", () => {
            if (!currentStatement) return;
            const remaining = Math.max(0, currentStatement.periodDebt - currentStatement.paidAmount);
            document.getElementById("pay-debt-amount").value = remaining.toFixed(2);
        });
    }

    // On-page presets
    const btnCcMin = document.getElementById("btn-cc-pay-min");
    if (btnCcMin) {
        btnCcMin.addEventListener("click", () => {
            if (!currentStatement) return;
            const minRemaining = Math.max(0, currentStatement.minimumPayment - currentStatement.paidAmount);
            document.getElementById("cc-pay-amount").value = minRemaining.toFixed(2);
        });
    }

    const btnCcFull = document.getElementById("btn-cc-pay-full");
    if (btnCcFull) {
        btnCcFull.addEventListener("click", () => {
            if (!currentStatement) return;
            const remaining = Math.max(0, currentStatement.periodDebt - currentStatement.paidAmount);
            document.getElementById("cc-pay-amount").value = remaining.toFixed(2);
        });
    }

    // Modal Pay Submit
    const payForm = document.getElementById("pay-debt-form");
    if (payForm) {
        payForm.addEventListener("submit", async (e) => {
            e.preventDefault();
            const sourceAccountNumber = document.getElementById("pay-debt-source").value;
            const amount = parseFloat(document.getElementById("pay-debt-amount").value);
            const msgEl = document.getElementById("pay-debt-message");

            msgEl.className = "alert hidden";

            if (!activeCreditCardId) return;

            try {
                const response = await fetch(`${API_URL}/banking/credit-cards/${activeCreditCardId}/pay`, {
                    method: "POST",
                    headers: {
                        "Content-Type": "application/json",
                        "Authorization": `Bearer ${currentToken}`
                    },
                    body: JSON.stringify({ sourceAccountNumber, amount })
                });

                const data = await response.json();

                if (!response.ok) {
                    msgEl.textContent = getLocalizedText(data.errorKey, data.message || getLocalizedText("PaymentFailed", "Payment failed."));
                    msgEl.className = "alert alert-danger";
                    return;
                }

                msgEl.textContent = getLocalizedText("PaymentSuccess", "Payment completed successfully!");
                msgEl.className = "alert alert-success";

                loadAccounts();
                loadCreditCards().then(() => {
                    fetch(`${API_URL}/banking/credit-cards`, {
                        headers: { "Authorization": `Bearer ${currentToken}` }
                    }).then(r => r.json()).then(cards => {
                        const c = cards.find(x => x.id === activeCreditCardId);
                        if (c) showStatementModal(c);
                    });
                });

            } catch (err) {
                msgEl.textContent = "Server connection failed.";
                msgEl.className = "alert alert-danger";
            }
        });
    }

    // On-page Pay Submit
    const ccPayForm = document.getElementById("cc-pay-debt-form");
    if (ccPayForm) {
        ccPayForm.addEventListener("submit", async (e) => {
            e.preventDefault();
            const sourceAccountNumber = document.getElementById("cc-pay-source").value;
            const amount = parseFloat(document.getElementById("cc-pay-amount").value);
            const msgEl = document.getElementById("cc-pay-message");

            msgEl.className = "alert hidden";

            if (!activeCreditCardId) return;

            try {
                const response = await fetch(`${API_URL}/banking/credit-cards/${activeCreditCardId}/pay`, {
                    method: "POST",
                    headers: {
                        "Content-Type": "application/json",
                        "Authorization": `Bearer ${currentToken}`
                    },
                    body: JSON.stringify({ sourceAccountNumber, amount })
                });

                const data = await response.json();

                if (!response.ok) {
                    msgEl.textContent = getLocalizedText(data.errorKey, data.message || getLocalizedText("PaymentFailed", "Payment failed."));
                    msgEl.className = "alert alert-danger";
                    return;
                }

                msgEl.textContent = getLocalizedText("PaymentSuccess", "Payment completed successfully!");
                msgEl.className = "alert alert-success";

                loadAccounts();
                loadCreditCards().then(() => {
                    fetch(`${API_URL}/banking/credit-cards`, {
                        headers: { "Authorization": `Bearer ${currentToken}` }
                    }).then(r => r.json()).then(cards => {
                        const c = cards.find(x => x.id === activeCreditCardId);
                        if (c) showStatementModal(c);
                    });
                });

            } catch (err) {
                msgEl.textContent = "Server connection failed.";
                msgEl.className = "alert alert-danger";
            }
        });
    }

    // Advance Period Simulation (for both buttons)
    const handleAdvancePeriod = async () => {
        if (!activeCreditCardId) return;
        
        const advBtn = document.getElementById("btn-advance-period");
        const advCcBtn = document.getElementById("btn-cc-advance-period");
        if (advBtn) advBtn.disabled = true;
        if (advCcBtn) advCcBtn.disabled = true;

        try {
            const response = await fetch(`${API_URL}/banking/credit-cards/${activeCreditCardId}/advance-period`, {
                method: "POST",
                headers: {
                    "Authorization": `Bearer ${currentToken}`
                }
            });

            if (response.ok) {
                alert(currentLanguage === "tr" ? "Dönem atlatıldı ve faiz hesaplandı!" : "Billing period advanced and interest calculated!");
                loadAccounts();
                loadCreditCards().then(() => {
                    fetch(`${API_URL}/banking/credit-cards`, {
                        headers: { "Authorization": `Bearer ${currentToken}` }
                    }).then(r => r.json()).then(cards => {
                        const c = cards.find(x => x.id === activeCreditCardId);
                        if (c) showStatementModal(c);
                    });
                });
            } else {
                alert("Simulation failed.");
            }
        } catch (err) {
            alert("Connection failed.");
        } finally {
            if (advBtn) advBtn.disabled = false;
            if (advCcBtn) advCcBtn.disabled = false;
        }
    };

    const advBtn = document.getElementById("btn-advance-period");
    if (advBtn) {
        advBtn.addEventListener("click", handleAdvancePeriod);
    }
    const advCcBtn = document.getElementById("btn-cc-advance-period");
    if (advCcBtn) {
        advCcBtn.addEventListener("click", handleAdvancePeriod);
    }

    const applyCcBtn = document.getElementById("btn-apply-creditcard");
    if (applyCcBtn) {
        applyCcBtn.addEventListener("click", async () => {
            const confirmMsg = currentLanguage === "tr" ? "Kredi kartı başvurusunu onaylıyor musunuz?" : "Do you confirm the credit card application?";
            if (!confirm(confirmMsg)) return;

            try {
                const response = await fetch(`${API_URL}/banking/credit-cards`, {
                    method: "POST",
                    headers: {
                        "Authorization": `Bearer ${currentToken}`
                    }
                });

                if (response.ok) {
                    alert(currentLanguage === "tr" ? "Kredi kartınız başarıyla oluşturuldu!" : "Credit card successfully created!");
                    loadCreditCards();
                    if (typeof confetti === "function") confetti();
                } else {
                    const errData = await response.json();
                    alert(getLocalizedText(errData.errorKey, errData.message || "Başvuru reddedildi."));
                }
            } catch (e) {
                alert("Connection failed.");
            }
        });
    }
}

/* ==========================================================================
   Vadesiz Hesap Açma (dashboard.html)
   ========================================================================== */
function initCreateAccountEvent() {
    const btn = document.getElementById("btn-create-account");
    const modal = document.getElementById("create-account-modal");
    const closeBtn = document.getElementById("btn-close-create-acc");
    const submitBtn = document.getElementById("btn-submit-create-acc");
    const cards = document.querySelectorAll(".acc-type-card");

    if (btn && modal) {
        btn.addEventListener("click", () => {
            modal.classList.remove("hidden");
        });
    }

    if (closeBtn && modal) {
        closeBtn.addEventListener("click", () => {
            modal.classList.add("hidden");
            const tiersPanel = document.getElementById("vadeli-tiers-panel");
            if (tiersPanel) tiersPanel.classList.add("hidden");
            const calcResult = document.getElementById("calc-result");
            if (calcResult) calcResult.classList.add("hidden");
            const calcPrincipalInput = document.getElementById("calc-principal");
            if (calcPrincipalInput) calcPrincipalInput.value = "";
        });
    }

    // Handle cards click selection
    cards.forEach(card => {
        card.addEventListener("click", () => {
            cards.forEach(c => c.classList.remove("active"));
            card.classList.add("active");
            
            const radio = card.querySelector('input[type="radio"]');
            if (radio) {
                radio.checked = true;
                
                const tiersPanel = document.getElementById("vadeli-tiers-panel");
                if (tiersPanel) {
                    if (radio.value === "TimeDeposit-TRY") {
                        tiersPanel.classList.remove("hidden");
                    } else {
                        tiersPanel.classList.add("hidden");
                    }
                }
            }
        });
    });

    // Calculator logic inside modal
    const calcBtn = document.getElementById("btn-calc-interest");
    const calcPrincipalInput = document.getElementById("calc-principal");
    const calcResult = document.getElementById("calc-result");
    const calcRate = document.getElementById("calc-rate");
    const calcNetProfit = document.getElementById("calc-net-profit");

    if (calcBtn && calcPrincipalInput) {
        calcBtn.addEventListener("click", () => {
            const principal = parseFloat(calcPrincipalInput.value) || 0;
            if (principal <= 0) {
                alert(currentLanguage === "tr" ? "Lütfen geçerli bir tutar girin." : "Please enter a valid amount.");
                return;
            }

            let rate = 48.00;
            if (principal < 50000) rate = 48.00;
            else if (principal < 250000) rate = 49.50;
            else if (principal < 1000000) rate = 51.00;
            else rate = 52.50;

            const gross = principal * (rate / 100) * (30 / 365);
            const tax = gross * 0.075;
            const net = gross - tax;

            if (calcRate) calcRate.textContent = `%${rate.toFixed(2)}`;
            if (calcNetProfit) calcNetProfit.textContent = `${net.toFixed(2)} TRY`;
            if (calcResult) calcResult.classList.remove("hidden");
        });
    }

    if (submitBtn && modal) {
        submitBtn.addEventListener("click", async () => {
            const activeRadio = document.querySelector('input[name="new-acc-choice"]:checked');
            if (!activeRadio) return;

            const val = activeRadio.value; // e.g. "DemandDeposit-TRY"
            const [accountType, currency] = val.split('-');

            submitBtn.disabled = true;
            try {
                const response = await fetch(`${API_URL}/banking/accounts?currency=${currency}&accountType=${accountType}`, {
                    method: "POST",
                    headers: {
                        "Authorization": `Bearer ${currentToken}`
                    }
                });

                if (response.ok) {
                    modal.classList.add("hidden");
                    const tiersPanel = document.getElementById("vadeli-tiers-panel");
                    if (tiersPanel) tiersPanel.classList.add("hidden");
                    loadAccounts();
                } else {
                    alert(currentLanguage === "tr" ? "Yeni hesap açılamadı." : "Failed to open a new account.");
                }
            } catch (err) {
                alert(currentLanguage === "tr" ? "Sunucu bağlantı hatası." : "Connection failed.");
            } finally {
                submitBtn.disabled = false;
            }
        });
    }
}

/* ==========================================================================
   PHASE 8 NEW WIDGETS AND SERVICES (dashboard.html)
   ========================================================================== */

function initTabNavigation() {
    const tabBtns = document.querySelectorAll(".tab-btn");
    const tabContents = document.querySelectorAll(".tab-content");

    tabBtns.forEach(btn => {
        btn.addEventListener("click", () => {
            const targetTabId = btn.dataset.tab;

            // Update active class on buttons
            tabBtns.forEach(b => b.classList.remove("active"));
            btn.classList.add("active");

            // Update active class on content containers
            tabContents.forEach(content => {
                if (content.id === targetTabId) {
                    content.classList.remove("hidden");
                } else {
                    content.classList.add("hidden");
                }
            });

            // If switching to standing orders, refresh lists
            if (targetTabId === "tab-standing-orders") {
                loadStandingOrders();
            }
        });
    });
}

function initExchangeWidget() {
    const exchangeForm = document.getElementById("exchange-form");
    const actionSelect = document.getElementById("exchange-action");
    const assetSelect = document.getElementById("exchange-asset");
    const sourceSelect = document.getElementById("exchange-source");
    const amountInput = document.getElementById("exchange-amount");
    const rateDisplay = document.getElementById("exchange-current-rate");
    const totalDisplay = document.getElementById("exchange-total-cost");
    const msgEl = document.getElementById("exchange-message");

    if (!exchangeForm) return;

    const updateExchangeSourceOptions = async () => {
        try {
            const res = await fetch(`${API_URL}/banking/accounts`, {
                headers: { "Authorization": `Bearer ${currentToken}` }
            });
            if (!res.ok) return;
            const accounts = await res.json();
            sourceSelect.innerHTML = "";

            const isBuy = actionSelect.value === "buy";
            const targetCurrency = isBuy ? "TRY" : assetSelect.value;

            const filtered = accounts.filter(a => a.currency === targetCurrency);

            if (filtered.length === 0) {
                const opt = document.createElement("option");
                opt.value = "";
                opt.textContent = isBuy ? 
                    (currentLanguage === "tr" ? "TRY Hesabınız Bulunmuyor" : "No TRY account found") : 
                    (currentLanguage === "tr" ? `${assetSelect.value} Hesabınız Bulunmuyor (Alış yapınca otomatik açılır)` : `No ${assetSelect.value} account found (buying opens one)`);
                sourceSelect.appendChild(opt);
            } else {
                filtered.forEach(acc => {
                    const opt = document.createElement("option");
                    opt.value = acc.id;
                    opt.textContent = `${acc.accountNumber} (${acc.balance.toFixed(acc.currency === "TRY" ? 2 : 4)} ${acc.currency})`;
                    sourceSelect.appendChild(opt);
                });
            }
            updateExchangeCalculations();
        } catch (e) {}
    };

    window.updateExchangeRateDisplay = () => {
        if (!activeMarketRates || activeMarketRates.length === 0) return;
        const asset = assetSelect.value;
        const action = actionSelect.value;

        const rateInfo = activeMarketRates.find(r => r.code === asset);
        if (!rateInfo) return;

        const rate = action === "buy" ? rateInfo.sell : rateInfo.buy;
        if (rateDisplay) rateDisplay.textContent = `${rate.toFixed(4)} TRY`;

        const amount = parseFloat(amountInput.value) || 0;
        const total = amount * rate;
        if (totalDisplay) totalDisplay.textContent = `${total.toFixed(2)} TRY`;
    };

    const updateExchangeCalculations = () => {
        window.updateExchangeRateDisplay();
    };

    actionSelect.addEventListener("change", () => {
        updateExchangeSourceOptions();
    });
    assetSelect.addEventListener("change", () => {
        updateExchangeSourceOptions();
    });
    amountInput.addEventListener("input", updateExchangeCalculations);

    updateExchangeSourceOptions();

    exchangeForm.addEventListener("submit", async (e) => {
        e.preventDefault();
        msgEl.className = "alert hidden";

        const sourceAccountId = sourceSelect.value;
        const asset = assetSelect.value;
        const action = actionSelect.value;
        const amount = parseFloat(amountInput.value);

        if (!sourceAccountId) {
            msgEl.textContent = currentLanguage === "tr" ? "Lütfen geçerli bir hesap seçin." : "Please select a valid account.";
            msgEl.className = "alert alert-danger";
            return;
        }
        if (isNaN(amount) || amount <= 0) {
            msgEl.textContent = currentLanguage === "tr" ? "Lütfen geçerli bir miktar girin." : "Please enter a valid amount.";
            msgEl.className = "alert alert-danger";
            return;
        }

        try {
            const response = await fetch(`${API_URL}/banking/exchange`, {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "Authorization": `Bearer ${currentToken}`
                },
                body: JSON.stringify({ sourceAccountId, asset, action, amount })
            });

            const data = await response.json();

            if (!response.ok) {
                msgEl.textContent = getLocalizedText(data.errorKey, data.message || "İşlem başarısız.");
                msgEl.className = "alert alert-danger";
                return;
            }

            msgEl.textContent = currentLanguage === "tr" ? "Döviz/Maden işlemi başarıyla gerçekleştirildi!" : "Exchange transaction completed successfully!";
            msgEl.className = "alert alert-success";

            amountInput.value = "";
            updateExchangeSourceOptions();
            loadAccounts();
            if (activeAccountId) {
                loadTransactions(activeAccountId);
            }
        } catch (err) {
            msgEl.textContent = "Bağlantı hatası.";
            msgEl.className = "alert alert-danger";
        }
    });
}

function initSavedContacts() {
    const selectEl = document.getElementById("transfer-saved-contacts");
    const destInput = document.getElementById("transfer-dest");
    const checkEl = document.getElementById("save-contact-check");
    const aliasInput = document.getElementById("save-contact-alias");

    if (!selectEl) return;

    if (checkEl && aliasInput) {
        checkEl.addEventListener("change", () => {
            if (checkEl.checked) {
                aliasInput.classList.remove("hidden");
                aliasInput.required = true;
            } else {
                aliasInput.classList.add("hidden");
                aliasInput.required = false;
                aliasInput.value = "";
            }
        });
    }

    selectEl.addEventListener("change", () => {
        if (selectEl.value) {
            destInput.value = selectEl.value;
        }
    });

    window.loadSavedContacts = async () => {
        try {
            const res = await fetch(`${API_URL}/banking/contacts`, {
                headers: { "Authorization": `Bearer ${currentToken}` }
            });
            if (!res.ok) return;

            savedContacts = await res.json();
            selectEl.innerHTML = `<option value="">${currentLanguage === "tr" ? "-- Kayıtlı Alıcı Seç --" : "-- Select Saved Contact --"}</option>`;

            savedContacts.forEach(contact => {
                const opt = document.createElement("option");
                opt.value = contact.accountNumber;
                opt.textContent = `${contact.alias} (${contact.accountNumber})`;
                selectEl.appendChild(opt);
            });
        } catch (e) {}
    };

    const manageBtn = document.getElementById("btn-manage-contacts");
    if (manageBtn) {
        manageBtn.addEventListener("click", () => {
            showManageContactsModal();
        });
    }

    const showManageContactsModal = () => {
        const modalId = "manage-contacts-modal";
        const existingModal = document.getElementById(modalId);
        if (existingModal) existingModal.remove();

        const modalEl = document.createElement("div");
        modalEl.id = modalId;
        modalEl.style = `
            position: fixed;
            top: 0;
            left: 0;
            width: 100%;
            height: 100%;
            background: rgba(0,0,0,0.85);
            backdrop-filter: blur(8px);
            display: flex;
            align-items: center;
            justify-content: center;
            z-index: 10000;
        `;

        const renderContactsList = () => {
            if (savedContacts.length === 0) {
                return `<div style="text-align: center; color: var(--text-muted); font-size: 0.85rem; padding: 2rem;">
                            ${currentLanguage === "tr" ? "Kayıtlı alıcı bulunamadı." : "No saved contacts found."}
                        </div>`;
            }

            const rowsHtml = savedContacts.map(c => `
                <div style="display: flex; justify-content: space-between; align-items: center; padding: 0.75rem; background: rgba(255,255,255,0.02); border: 1px solid rgba(255,255,255,0.05); border-radius: 8px; margin-bottom: 0.5rem; gap: 1rem;">
                    <div style="display: flex; flex-direction: column; flex: 1; min-width: 0;">
                        <span style="font-weight: 700; color: #fff; font-size: 0.85rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;">${c.alias}</span>
                        <span style="font-size: 0.75rem; color: var(--text-muted); font-family: monospace;">${c.accountNumber}</span>
                    </div>
                    <div style="display: flex; gap: 0.35rem; flex-shrink: 0;">
                        <button class="btn-contact-edit btn btn-secondary btn-xs" data-accno="${c.accountNumber}" data-alias="${c.alias}" style="padding: 0.25rem 0.5rem; font-size: 0.75rem; border-radius: 6px; cursor: pointer;">✏️</button>
                        <button class="btn-contact-delete btn btn-danger btn-xs" data-id="${c.id}" style="padding: 0.25rem 0.5rem; font-size: 0.75rem; border-radius: 6px; cursor: pointer;">🗑️</button>
                    </div>
                </div>
            `).join("");

            return `<div style="max-height: 250px; overflow-y: auto; padding-right: 0.25rem;">${rowsHtml}</div>`;
        };

        const updateModalContent = () => {
            modalEl.innerHTML = `
                <div class="card glassmorphism" style="width: 420px; padding: 2rem; border: 1px solid rgba(255,255,255,0.08); border-radius: 20px; background: rgba(15, 23, 42, 0.98); box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.5); animation: modalFadeIn 0.3s ease;">
                    <h3 style="margin-bottom: 0.5rem; font-size: 1.25rem; font-weight: 700; color: #fff; letter-spacing: -0.025em;">
                        ${currentLanguage === "tr" ? "Kayıtlı Alıcıları Yönet" : "Manage Saved Contacts"}
                    </h3>
                    <p style="font-size: 0.8rem; color: var(--text-muted); margin-bottom: 1.5rem; line-height: 1.4;">
                        ${currentLanguage === "tr" ? "Kayıtlı alıcılarınızın lakaplarını düzenleyebilir veya listeden tamamen silebilirsiniz." : "You can edit contact aliases or permanently delete them from your list."}
                    </p>
                    
                    <div id="contacts-list-container" style="margin-bottom: 1.5rem;">
                        ${renderContactsList()}
                    </div>

                    <div style="display: flex; justify-content: flex-end;">
                        <button id="manage-contacts-close-btn" class="btn btn-secondary" style="padding: 0.6rem 1.2rem; font-size: 0.8rem; border-radius: 10px; font-weight: 600; width: 100%; cursor: pointer;">
                            ${currentLanguage === "tr" ? "Kapat" : "Close"}
                        </button>
                    </div>
                </div>
            `;

            // Close button listener
            document.getElementById("manage-contacts-close-btn").addEventListener("click", () => {
                modalEl.remove();
            });

            // Edit buttons listener
            modalEl.querySelectorAll(".btn-contact-edit").forEach(btn => {
                btn.addEventListener("click", async () => {
                    const accNo = btn.dataset.accno;
                    const oldAlias = btn.dataset.alias;
                    const promptMsg = currentLanguage === "tr" ? 
                        `"${oldAlias}" alıcısı için yeni bir lakap girin:` : 
                        `Enter a new alias for "${oldAlias}":`;
                    const newAlias = prompt(promptMsg, oldAlias);
                    if (newAlias === null) return; // cancel
                    const trimmed = newAlias.trim();
                    if (!trimmed) {
                        alert(currentLanguage === "tr" ? "Lakap boş bırakılamaz." : "Alias cannot be empty.");
                        return;
                    }

                    try {
                        const response = await fetch(`${API_URL}/banking/contacts`, {
                            method: "POST",
                            headers: {
                                "Content-Type": "application/json",
                                "Authorization": `Bearer ${currentToken}`
                            },
                            body: JSON.stringify({ alias: trimmed, accountNumber: accNo })
                        });
                        if (response.ok) {
                            await loadSavedContacts();
                            updateModalContent();
                        } else {
                            alert("Hata oluştu.");
                        }
                    } catch (err) {
                        alert("Sunucu bağlantı hatası.");
                    }
                });
            });

            // Delete buttons listener
            modalEl.querySelectorAll(".btn-contact-delete").forEach(btn => {
                btn.addEventListener("click", async () => {
                    const contactId = btn.dataset.id;
                    const confirmMsg = currentLanguage === "tr" ? 
                        "Bu alıcıyı kayıtlı kişilerden silmek istediğinize emin misiniz?" : 
                        "Are you sure you want to delete this contact?";
                    if (!confirm(confirmMsg)) return;

                    try {
                        const response = await fetch(`${API_URL}/banking/contacts/${contactId}`, {
                            method: "DELETE",
                            headers: { "Authorization": `Bearer ${currentToken}` }
                        });
                        if (response.ok) {
                            await loadSavedContacts();
                            updateModalContent();
                        } else {
                            alert("Silinemedi.");
                        }
                    } catch (err) {
                        alert("Hata oluştu.");
                    }
                });
            });
        };

        updateModalContent();
        document.body.appendChild(modalEl);
    };

    loadSavedContacts();
}

async function handleSaveContactAfterTransfer(destAccount) {
    const checkEl = document.getElementById("save-contact-check");
    const aliasInput = document.getElementById("save-contact-alias");
    if (checkEl && checkEl.checked) {
        let name = aliasInput ? aliasInput.value.trim() : "";
        if (!name) {
            name = (currentLanguage === "tr" ? "Kayıtlı Alıcı " : "Saved Contact ") + destAccount.slice(-4);
        }
        try {
            await fetch(`${API_URL}/banking/contacts`, {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "Authorization": `Bearer ${currentToken}`
                },
                body: JSON.stringify({ alias: name, accountNumber: destAccount })
            });
            checkEl.checked = false;
            if (aliasInput) {
                aliasInput.value = "";
                aliasInput.classList.add("hidden");
            }
            if (typeof loadSavedContacts === "function") {
                loadSavedContacts();
            }
        } catch(e){}
    }
}

function initStandingOrders() {
    const form = document.getElementById("standing-order-form");
    const typeSelect = document.getElementById("so-type");
    const txFields = document.getElementById("so-transfer-fields");
    const ccFields = document.getElementById("so-cc-fields");
    const sourceSelect = document.getElementById("so-source-acc");
    const targetCcSelect = document.getElementById("so-target-cc");
    const msgEl = document.getElementById("standing-order-message");

    if (!form) return;

    typeSelect.addEventListener("change", () => {
        if (typeSelect.value === "Transfer") {
            txFields.classList.remove("hidden");
            ccFields.classList.add("hidden");
        } else {
            txFields.classList.add("hidden");
            ccFields.classList.remove("hidden");
        }
    });

    window.populateStandingOrderSelects = async () => {
        try {
            const accRes = await fetch(`${API_URL}/banking/accounts`, {
                headers: { "Authorization": `Bearer ${currentToken}` }
            });
            if (accRes.ok) {
                const accounts = await accRes.json();
                sourceSelect.innerHTML = "";
                accounts.filter(a => a.currency === "TRY").forEach(acc => {
                    const opt = document.createElement("option");
                    opt.value = acc.accountNumber;
                    opt.textContent = `${acc.accountNumber} (${acc.balance.toFixed(2)} TRY)`;
                    sourceSelect.appendChild(opt);
                });
            }

            const ccRes = await fetch(`${API_URL}/banking/credit-cards`, {
                headers: { "Authorization": `Bearer ${currentToken}` }
            });
            if (ccRes.ok) {
                const cards = await ccRes.json();
                targetCcSelect.innerHTML = "";
                cards.forEach(card => {
                    const opt = document.createElement("option");
                    opt.value = card.id;
                    opt.textContent = `SmartCredit (**** ${card.cardNumber.slice(-4)})`;
                    targetCcSelect.appendChild(opt);
                });
            }
        } catch(e){}
    };

    window.loadStandingOrders = async () => {
        const listEl = document.getElementById("standing-orders-list");
        if (!listEl) return;
        listEl.innerHTML = '<div class="loading-spinner">Yükleniyor...</div>';

        try {
            const res = await fetch(`${API_URL}/banking/standing-orders`, {
                headers: { "Authorization": `Bearer ${currentToken}` }
            });
            if (!res.ok) return;

            standingOrders = await res.json();
            listEl.innerHTML = "";

            if (standingOrders.length === 0) {
                listEl.innerHTML = `<div class="text-muted text-center" style="padding: 2rem 0;">${currentLanguage === "tr" ? "Tanımlı talimatınız bulunmuyor." : "No standing orders defined."}</div>`;
                return;
            }

            standingOrders.forEach(order => {
                const el = document.createElement("div");
                el.className = "account-card glassmorphism";
                el.style.display = "flex";
                el.style.flexDirection = "column";
                el.style.gap = "0.5rem";

                let orderDesc = "";
                if (order.type === "CreditCardAutoPay") {
                    orderDesc = currentLanguage === "tr" ? 
                        `Kredi Kartı Son Ödeme Günü Otomatik Borç Kapama` : 
                        `Credit Card Auto Statement Settlement on Due Date`;
                } else {
                    const freqStr = order.frequency === "Daily" ? (currentLanguage === "tr" ? "Günlük" : "Daily") :
                                    order.frequency === "Weekly" ? (currentLanguage === "tr" ? "Haftalık" : "Weekly") :
                                    (currentLanguage === "tr" ? "Aylık" : "Monthly");
                    orderDesc = currentLanguage === "tr" ? 
                        `${freqStr} Düzenli Transfer (${order.amount.toFixed(2)} TRY -> ${order.destinationAccountNumber})` :
                        `${freqStr} Scheduled Transfer (${order.amount.toFixed(2)} TRY -> ${order.destinationAccountNumber})`;
                }

                el.innerHTML = `
                    <div style="font-weight: bold; color: var(--accent-color); font-size: 0.85rem;">
                        ${order.type === "CreditCardAutoPay" ? "💳 Otomatik Ekstre Ödeme" : "📅 Düzenli Para Transferi"}
                    </div>
                    <div style="font-size: 0.8rem; line-height: 1.3;">${orderDesc}</div>
                    <div style="font-size: 0.7rem; color: var(--text-muted);">
                        Kaynak: ${order.sourceAccountNumber}
                    </div>
                    <button class="btn btn-danger btn-xs btn-so-delete" data-soid="${order.id}" style="align-self: flex-end; margin-top: 0.25rem; font-size: 0.7rem; padding: 0.2rem 0.5rem;">İptal Et</button>
                `;

                el.querySelector(".btn-so-delete").addEventListener("click", async () => {
                    const confirmMsg = currentLanguage === "tr" ? "Bu talimatı iptal etmek istiyor musunuz?" : "Do you want to cancel this instruction?";
                    if (!confirm(confirmMsg)) return;

                    try {
                        const deleteRes = await fetch(`${API_URL}/banking/standing-orders/${order.id}`, {
                            method: "DELETE",
                            headers: { "Authorization": `Bearer ${currentToken}` }
                        });
                        if (deleteRes.ok) {
                            loadStandingOrders();
                        } else {
                            alert("İptal edilemedi.");
                        }
                    } catch(err) {
                        alert("Bağlantı hatası.");
                    }
                });

                listEl.appendChild(el);
            });
        } catch (e) {}
    };

    populateStandingOrderSelects();

    form.addEventListener("submit", async (e) => {
        e.preventDefault();
        msgEl.className = "alert hidden";

        const sourceAccountId = sourceSelect.value;
        const type = typeSelect.value;
        
        let destinationAccountNumber = null;
        let amount = null;
        let frequency = null;
        let creditCardId = null;

        if (!sourceAccountId) {
            msgEl.textContent = "Lütfen kaynak hesabı seçin.";
            msgEl.className = "alert alert-danger";
            return;
        }

        if (type === "Transfer") {
            destinationAccountNumber = document.getElementById("so-dest-acc").value.trim();
            amount = parseFloat(document.getElementById("so-amount").value);
            frequency = document.getElementById("so-frequency").value;

            if (!destinationAccountNumber) {
                msgEl.textContent = "Lütfen alıcı hesap numarasını girin.";
                msgEl.className = "alert alert-danger";
                return;
            }
            if (isNaN(amount) || amount <= 0) {
                msgEl.textContent = "Lütfen geçerli bir tutar girin.";
                msgEl.className = "alert alert-danger";
                return;
            }
        } else {
            creditCardId = targetCcSelect.value;
            frequency = "Monthly";
            if (!creditCardId) {
                msgEl.textContent = "Lütfen bir kredi kartı seçin.";
                msgEl.className = "alert alert-danger";
                return;
            }
        }

        try {
            const response = await fetch(`${API_URL}/banking/standing-orders`, {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "Authorization": `Bearer ${currentToken}`
                },
                body: JSON.stringify({ sourceAccountNumber: sourceAccountId, orderType: type, destinationAccountNumber, amount, frequency, creditCardId })
            });

            if (!response.ok) {
                const data = await response.json();
                msgEl.textContent = getLocalizedText(data.errorKey, data.message || "Talimat oluşturulamadı.");
                msgEl.className = "alert alert-danger";
                return;
            }

            msgEl.textContent = currentLanguage === "tr" ? "Talimat başarıyla tanımlandı!" : "Instruction registered successfully!";
            msgEl.className = "alert alert-success";

            document.getElementById("so-dest-acc").value = "";
            document.getElementById("so-amount").value = "";

            loadStandingOrders();
        } catch(err) {
            msgEl.textContent = "Bağlantı hatası.";
            msgEl.className = "alert alert-danger";
        }
    });
}

let html5QrcodeScanner = null;

function initQrSimulators() {
    return;
    const btnQrActions = document.getElementById("btn-qr-actions");
    const scannerModal = document.getElementById("qr-scanner-modal");
    const btnCloseScanner = document.getElementById("btn-close-scanner");
    const btnScanPos = document.getElementById("btn-scan-pos");
    const btnScanTransfer = document.getElementById("btn-scan-transfer");
    
    const scannerResult = document.getElementById("scanner-result-panel");
    const scanTitle = document.getElementById("lbl-scan-result-title");
    const scanMerchant = document.getElementById("scan-pos-merchant");
    const scanAmount = document.getElementById("scan-pos-amount");
    const scanSource = document.getElementById("scan-payment-source");
    const confirmPaymentBtn = document.getElementById("btn-confirm-qr-payment");
    const qrPayMsg = document.getElementById("qr-payment-message");

    const btnCloseQrCode = document.getElementById("btn-close-qr-code");
    const qrModal = document.getElementById("qr-code-modal");

    let scanType = "pos";
    let scrolledIban = "";

    const handleQrScannedValue = (text) => {
        text = (text || "").trim();
        
        // Try to parse Turkish BKM QR format first
        const parsedBkm = parseTurkishQrFormat(text);
        if (parsedBkm) {
            if (parsedBkm.amount > 0) {
                // POS payment with amount
                scanType = "pos";
                scanTitle.textContent = currentLanguage === "tr" ? "Taranan Bilgi: QR Ödeme" : "Scanned Data: QR Payment";
                scanMerchant.parentElement.style.display = "flex";
                scanMerchant.textContent = parsedBkm.recipient || "QR Ödeme";
                scanAmount.textContent = `${parsedBkm.amount.toFixed(2)} TRY`;
                scannerResult.classList.remove("hidden");
                
                window.scannedPosAmount = parsedBkm.amount;
                window.scannedPosMerchant = parsedBkm.recipient || "QR Ödeme";
                window.scannedPosIban = parsedBkm.iban;
            } else {
                // Transfer without amount (just IBAN/recipient)
                scanType = "transfer";
                scrolledIban = parsedBkm.iban;
                scanTitle.textContent = currentLanguage === "tr" ? "Taranan Bilgi: Kişiye Para Transferi" : "Scanned Data: P2P Fund Transfer";
                scanMerchant.parentElement.style.display = "none";
                scanAmount.textContent = currentLanguage === "tr" ? 
                    `Alıcı: ${parsedBkm.recipient || parsedBkm.iban} (Transfer formuna aktarılacak)` : 
                    `Recipient: ${parsedBkm.recipient || parsedBkm.iban} (Redirecting to form)`;
                scannerResult.classList.remove("hidden");
            }
        } else if (text.startsWith("TR") || text.startsWith("ACC-") || text.length > 15) {
            scanType = "transfer";
            scrolledIban = text;
            scanTitle.textContent = currentLanguage === "tr" ? "Taranan Bilgi: Kişiye Para Transferi" : "Scanned Data: P2P Fund Transfer";
            scanMerchant.parentElement.style.display = "none";
            scanAmount.textContent = currentLanguage === "tr" ? `Alıcı Hesap: ${text} (Transfer formuna aktarılacak)` : `Recipient: ${text} (Redirecting to form)`;
            scannerResult.classList.remove("hidden");
        } else {
            scanType = "pos";
            let merchant = "SmartPOS Merchant";
            let amt = 150.00;
            
            if (text.includes("merchant=") && text.includes("amount=")) {
                const urlParams = new URLSearchParams(text.replace("POS_PAYMENT:", ""));
                merchant = urlParams.get("merchant") || merchant;
                amt = parseFloat(urlParams.get("amount")) || amt;
            } else if (!isNaN(parseFloat(text))) {
                amt = parseFloat(text);
            }
            
            scanTitle.textContent = currentLanguage === "tr" ? "Taranan Bilgi: POS İşyeri Ödemesi" : "Scanned Data: POS Merchant Charge";
            scanMerchant.parentElement.style.display = "flex";
            scanMerchant.textContent = merchant;
            scanAmount.textContent = `${amt.toFixed(2)} TRY`;
            scannerResult.classList.remove("hidden");
            
            window.scannedPosAmount = amt;
            window.scannedPosMerchant = merchant;
        }
        
        if (typeof confetti === "function") confetti();
    };

    // Parse Turkish BKM QR format
    function parseTurkishQrFormat(text) {
        try {
            // Try to find IBAN (starts with TR followed by 24-26 digits)
            const ibanMatch = text.match(/TR\d{24,26}/);
            const iban = ibanMatch ? ibanMatch[0] : null;
            
            // Try to extract amount (look for numeric patterns that could be amounts)
            // Turkish QR often has amount in format like 00000150.00 (150.00 TL)
            let amount = 0;
            
            // Pattern 1: Look for amount BEFORE IBAN (common in BKM format)
            // Often appears as 0000000001500 (15.00 TL) before the IBAN
            if (iban) {
                const ibanIndex = text.indexOf(iban);
                const beforeIban = text.substring(0, ibanIndex);
                
                // First, try to find exact 1500 pattern (15 TL in kuruş)
                const exact1500Match = beforeIban.match(/1500/);
                if (exact1500Match) {
                    amount = 15.00;
                }
                
                // If not found, look for 13 digit numbers that start with zeros (kuruş format in BKM)
                if (amount === 0) {
                    const kuruşBeforeIbanMatches = beforeIban.match(/(0\d{12})/g);
                    if (kuruşBeforeIbanMatches) {
                        // Find the match that gives the most reasonable amount (prefer smaller amounts)
                        let bestAmount = 0;
                        for (const match of kuruşBeforeIbanMatches) {
                            const kuruşValue = parseInt(match, 10);
                            const tlValue = kuruşValue / 100;
                            // Prefer amounts that are reasonable (0.01 to 10,000 TL)
                            if (tlValue >= 0.01 && tlValue <= 10000) {
                                // Always prefer the smallest reasonable amount
                                if (bestAmount === 0 || tlValue < bestAmount) {
                                    bestAmount = tlValue;
                                }
                            }
                        }
                        amount = bestAmount;
                    }
                }
            }
            
            // Pattern 2: Look for amount in the specific position in BKM QR format
            // After IBAN, there's often a 12-13 digit number representing kuruş
            if (amount === 0 && iban) {
                const ibanIndex = text.indexOf(iban);
                const afterIban = text.substring(ibanIndex + iban.length);
                // Look for 12-13 digit number after IBAN (kuruş format)
                const kuruşAfterIban = afterIban.match(/^(\d{12,13})/);
                if (kuruşAfterIban) {
                    const kuruşValue = parseInt(kuruşAfterIban[1], 10);
                    const tlValue = kuruşValue / 100;
                    if (tlValue >= 0.01 && tlValue <= 1000000) {
                        amount = tlValue;
                    }
                }
            }
            
            // Pattern 3: Decimal format like 15.00 or 15,00
            if (amount === 0) {
                const decimalMatch = text.match(/(\d{1,10})[.,](\d{2})/);
                if (decimalMatch) {
                    const wholePart = parseInt(decimalMatch[1], 10);
                    const decimalPart = parseInt(decimalMatch[2], 10);
                    amount = wholePart + (decimalPart / 100);
                }
            }
            
            // Pattern 4: Look for 4-6 digit numbers that could be kuruş values (e.g., 1500 = 15.00 TL)
            // But filter out obviously large numbers that are likely account numbers
            if (amount === 0) {
                const kuruşMatch = text.match(/(\d{4,6})/g);
                if (kuruşMatch) {
                    for (const match of kuruşMatch) {
                        const kuruşValue = parseInt(match, 10);
                        const tlValue = kuruşValue / 100;
                        // Only accept if it's a reasonable amount (0.01 to 1,000 TL)
                        // This filters out large numbers like 750210 (7502.10 TL)
                        if (tlValue >= 0.01 && tlValue <= 1000) {
                            amount = tlValue;
                            break;
                        }
                    }
                }
            }
            
            // Pattern 5: Simple whole number (e.g., 15)
            if (amount === 0) {
                const wholeMatch = text.match(/\b(\d{1,6})\b/);
                if (wholeMatch) {
                    const potentialAmount = parseInt(wholeMatch[1], 10);
                    if (potentialAmount >= 1 && potentialAmount <= 100000) {
                        amount = potentialAmount;
                    }
                }
            }
            
            // Try to extract recipient name (Turkish characters)
            // Look for name-like patterns after numbers
            let recipient = null;
            
            // Extract text that looks like a name (contains letters, possibly Turkish chars)
            // Usually appears after IBAN or amount
            const nameMatch = text.match(/([A-Za-zÇĞİÖŞÜçğıöşü\s]{3,50})/);
            if (nameMatch) {
                recipient = nameMatch[1].trim();
                // Filter out common non-name words
                if (recipient.length < 3 || recipient.match(/^\d+$/)) {
                    recipient = null;
                }
            }
            
            // If we found at least an IBAN, return the parsed data
            if (iban) {
                return { iban, amount, recipient };
            }
            
            return null;
        } catch (e) {
            console.error("Error parsing Turkish QR format:", e);
            return null;
        }
    }

    const stopScanner = () => {
        if (html5QrcodeScanner) {
            try {
                if (html5QrcodeScanner.isScanning) {
                    html5QrcodeScanner.stop().catch(e => {});
                }
            } catch(e) {}
        }
        scannerModal.classList.add("hidden");
    };

    if (btnQrActions && scannerModal) {
        btnQrActions.addEventListener("click", () => {
            fetch(`${API_URL}/banking/accounts`, {
                headers: { "Authorization": `Bearer ${currentToken}` }
            }).then(r => r.json()).then(accounts => {
                scanSource.innerHTML = "";
                accounts.filter(a => a.currency === "TRY").forEach(acc => {
                    const opt = document.createElement("option");
                    opt.value = acc.accountNumber;
                    opt.textContent = `${acc.accountNumber} (${acc.balance.toFixed(2)} TRY)`;
                    scanSource.appendChild(opt);
                });
            });

            scannerResult.classList.add("hidden");
            qrPayMsg.className = "alert hidden";
            scannerModal.classList.remove("hidden");

            // Start webcam scanner
            if (typeof Html5Qrcode === "function") {
                if (html5QrcodeScanner) {
                    try { html5QrcodeScanner.clear(); } catch(e) {}
                }
                
                html5QrcodeScanner = new Html5Qrcode("qr-reader");
                
                const qrSuccessCallback = (decodedText) => {
                    html5QrcodeScanner.stop().then(() => {
                        handleQrScannedValue(decodedText);
                    }).catch(() => {
                        handleQrScannedValue(decodedText);
                    });
                };
                
                const config = { fps: 10, qrbox: { width: 220, height: 220 } };
                
                html5QrcodeScanner.start({ facingMode: "environment" }, config, qrSuccessCallback)
                    .catch(err => {
                        console.warn("Webcam not started (headless or no permission):", err);
                    });
            }
        });
    }

    if (btnCloseScanner) {
        btnCloseScanner.addEventListener("click", stopScanner);
    }

    if (btnCloseQrCode) {
        btnCloseQrCode.addEventListener("click", () => {
            qrModal.classList.add("hidden");
        });
    }

    if (btnScanPos) {
        btnScanPos.addEventListener("click", () => {
            handleQrScannedValue("POS_PAYMENT:merchant=Starbucks Coffee&amount=145.00");
        });
    }

    if (btnScanTransfer) {
        btnScanTransfer.addEventListener("click", () => {
            handleQrScannedValue("TR987654321012345678901234");
        });
    }

    if (confirmPaymentBtn) {
        confirmPaymentBtn.addEventListener("click", async () => {
            qrPayMsg.className = "alert hidden";
            const sourceAccNo = scanSource.value;

            if (!sourceAccNo) {
                qrPayMsg.textContent = "Ödeme hesabı bulunamadı.";
                qrPayMsg.className = "alert alert-danger";
                return;
            }

            if (scanType === "pos") {
                confirmPaymentBtn.disabled = true;
                const amt = window.scannedPosAmount || 145.00;
                const merch = window.scannedPosMerchant || "Starbucks Coffee";
                try {
                    const response = await fetch(`${API_URL}/banking/transfer`, {
                        method: "POST",
                        headers: {
                            "Content-Type": "application/json",
                            "Authorization": `Bearer ${currentToken}`
                        },
                        body: JSON.stringify({
                            sourceAccountNumber: sourceAccNo,
                            destinationAccountNumber: "TR000000000000000000000000",
                            amount: amt,
                            description: `POS QR Ödemesi: ${merch}`,
                            category: "Market"
                        })
                    });

                    if (response.ok) {
                        qrPayMsg.textContent = currentLanguage === "tr" ? "POS Ödemesi Başarıyla Tamamlandı!" : "POS Charge Approved!";
                        qrPayMsg.className = "alert alert-success";
                        loadAccounts();
                        setTimeout(() => {
                            stopScanner();
                        }, 1500);
                    } else {
                        qrPayMsg.textContent = "Ödeme reddedildi (Bakiye yetersiz).";
                        qrPayMsg.className = "alert alert-danger";
                    }
                } catch(err) {
                    qrPayMsg.textContent = "İşlem başarısız.";
                } finally {
                    confirmPaymentBtn.disabled = false;
                }
            } else {
                document.getElementById("transfer-dest").value = scrolledIban;
                document.getElementById("transfer-source").value = sourceAccNo;
                document.getElementById("btn-tab-accounts").click();
                stopScanner();
                document.getElementById("transfer-amount").focus();
            }
        });
    }

    window.showAccountQrCode = (accountNumber) => {
        const qrModal = document.getElementById("qr-code-modal");
        const qrImg = document.getElementById("qr-code-image");
        const qrAccNo = document.getElementById("qr-account-number");

        if (!qrModal || !qrImg || !qrAccNo) return;

        qrImg.src = `https://api.qrserver.com/v1/create-qr-code/?size=180x180&data=${encodeURIComponent(accountNumber)}`;
        qrAccNo.textContent = accountNumber;
        qrModal.classList.remove("hidden");
    };
}

function showTransactionSlip(tx) {
    const modal = document.getElementById("slip-modal");
    if (!modal) return;

    document.getElementById("slip-date").textContent = new Date(tx.createdAt).toLocaleString(currentLanguage === "tr" ? "tr-TR" : "en-US");
    document.getElementById("slip-ref-no").textContent = `TX-${tx.id.toString().padStart(8, '0')}`;
    document.getElementById("slip-type").textContent = getLocalizedText(tx.type, tx.type);
    
    document.getElementById("slip-sender-name").textContent = tx.sourceAccountOwnerName || "SmartBank Müşterisi";
    document.getElementById("slip-sender-acc").textContent = tx.sourceAccountNumber || "-";
    
    document.getElementById("slip-receiver-name").textContent = tx.destinationAccountOwnerName || "SmartBank Müşterisi";
    document.getElementById("slip-receiver-acc").textContent = tx.destinationAccountNumber || "-";
    
    document.getElementById("slip-amount").textContent = `${tx.amount.toFixed(2)} ${tx.currency || 'TRY'}`;
    document.getElementById("slip-desc").textContent = tx.description || "-";

    modal.classList.remove("hidden");
}

const closeSlipBtn = document.getElementById("btn-close-slip");
if (closeSlipBtn) {
    closeSlipBtn.addEventListener("click", () => {
        document.getElementById("slip-modal").classList.add("hidden");
    });
}

function initSidebar() {
    const sidebarToggle = document.getElementById("sidebar-toggle");
    const sidebar = document.getElementById("sidebar");
    const sidebarClose = document.getElementById("sidebar-close");
    const btnAdd1000Try = document.getElementById("btn-add-1000-try");
    const addBalanceMessage = document.getElementById("add-balance-message");

    if (sidebarToggle && sidebar) {
        sidebarToggle.addEventListener("click", () => {
            sidebar.classList.toggle("hidden");
        });
    }

    if (sidebarClose && sidebar) {
        sidebarClose.addEventListener("click", () => {
            sidebar.classList.add("hidden");
        });
    }

    if (btnAdd1000Try && addBalanceMessage) {
        btnAdd1000Try.addEventListener("click", async () => {
            try {
                // Get TRY accounts
                const accountsResponse = await fetch(`${API_URL}/banking/accounts`, {
                    headers: { "Authorization": `Bearer ${currentToken}` }
                });
                const accounts = await accountsResponse.json();
                
                // Find first TRY account
                const tryAccount = accounts.find(a => a.currency === "TRY");
                
                if (!tryAccount) {
                    addBalanceMessage.textContent = "Vadesiz TL hesabı bulunamadı.";
                    addBalanceMessage.className = "alert alert-danger";
                    addBalanceMessage.classList.remove("hidden");
                    return;
                }

                // Add 1000 TRY to the account via transfer (from same account to itself with deposit)
                const response = await fetch(`${API_URL}/banking/deposit`, {
                    method: "POST",
                    headers: {
                        "Content-Type": "application/json",
                        "Authorization": `Bearer ${currentToken}`
                    },
                    body: JSON.stringify({
                        accountNumber: tryAccount.accountNumber,
                        amount: 1000.00
                    })
                });

                if (response.ok) {
                    addBalanceMessage.textContent = "1000 TL başarıyla eklendi!";
                    addBalanceMessage.className = "alert alert-success";
                    addBalanceMessage.classList.remove("hidden");
                    loadAccounts(); // Refresh account list
                } else {
                    addBalanceMessage.textContent = "İşlem başarısız oldu.";
                    addBalanceMessage.className = "alert alert-danger";
                    addBalanceMessage.classList.remove("hidden");
                }
            } catch (error) {
                console.error("Error adding balance:", error);
                addBalanceMessage.textContent = "Bir hata oluştu.";
                addBalanceMessage.className = "alert alert-danger";
                addBalanceMessage.classList.remove("hidden");
            }
        });
    }
}

window.switchOperationsTab = (tab) => {
    const transferTab = document.getElementById("tab-content-transfer");
    const exchangeTab = document.getElementById("tab-content-exchange");
    const transferBtn = document.getElementById("tab-btn-transfer");
    const exchangeBtn = document.getElementById("tab-btn-exchange");

    if (!transferTab || !exchangeTab || !transferBtn || !exchangeBtn) return;

    if (tab === 'transfer') {
        transferTab.classList.remove("hidden");
        exchangeTab.classList.add("hidden");
        transferBtn.classList.add("active");
        transferBtn.style.background = "rgba(255,255,255,0.08)";
        transferBtn.style.color = "#fff";
        exchangeBtn.classList.remove("active");
        exchangeBtn.style.background = "transparent";
        exchangeBtn.style.color = "var(--text-muted)";
    } else {
        transferTab.classList.add("hidden");
        exchangeTab.classList.remove("hidden");
        exchangeBtn.classList.add("active");
        exchangeBtn.style.background = "rgba(255,255,255,0.08)";
        exchangeBtn.style.color = "#fff";
        transferBtn.classList.remove("active");
        transferBtn.style.background = "transparent";
        transferBtn.style.color = "var(--text-muted)";
    }
};
