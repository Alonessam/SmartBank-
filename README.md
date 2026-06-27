# ❖ SmartBank - Premium Fintech & Digital Banking Portal

SmartBank is a high-fidelity, feature-rich digital banking and fintech portal built on **.NET 10.0 (ASP.NET Core Web API)** and a modern **Vanilla HTML5/CSS3/JS** frontend. It features multi-currency asset management, real-time market rates simulation, an AI-powered customer support chatbot (with RAG), secure credit card pipelines, and advanced anti-fraud transaction workflows.

*(Türkçe açıklama için sayfanın altına kaydırabilirsiniz / Scroll down for the Turkish version)*

---

## 🚀 Key Features & Capabilities

### 1. Multi-Currency Asset & Wealth Management
* **Diverse Wallets:** Manage fiat accounts (**TRY, USD, EUR**) alongside precious metals (**Gold - XAU, Silver - XAG**).
* **Smart Account Deletion (with Balance Transfer):** Closing an account with an active balance prompts the user to select a destination account. The system automatically converts the remaining funds based on live market rates and closes the account seamlessly.
* **No-Zero Policy:** Ensures active users always keep at least one default account.

### 2. Live Forex & Metals Trading (Buy/Sell)
* **Dynamic Calculations:** The exchange rate and final cost/yield update dynamically in real time as the user types the purchase or sale amount.
* **Automatic Wallet Opening:** Purchasing a foreign currency or metal automatically spawns the corresponding asset wallet for the user if it doesn't already exist.

### 3. Credit Card Workspace
* **Single-Card Rule:** Users are restricted to having a maximum of one credit card.
* **Visual Card Customizer:** Interactive card interface featuring a custom neon glow theme selection (Default, Midnight Black, Emerald Green, Neon Blue). Cardholder name auto-scaling prevents layout overflow.
* **Debt & Statement Operations:** Real-time billing cycle statements, minimum payment tracking, and automated debt payment via selected accounts.
* **Automatic Standing Orders:** Automatically schedules automatic bill payments or card debt settlement.

### 4. Saved Contacts & Quick Transfers
* **Saved Contacts Directory:** Add new recipients by account number (IBAN), manage aliases, edit contact names, or delete them.
* **Quick Fill:** Select a saved contact from a dropdown list to instantly populate transfer details.

### 5. AI Support Chatbot & Agent Co-Pilot (SignalR)
* **Hybrid Support System:** Real-time chat powered by **SignalR**. The chat is handled by a Local AI bot (via **RAG & Ollama/Gemini**) and can be escalated to a live human support agent.
* **Agent Workspace:** Dedicated dashboard for support agents showing live chat queues, average response times, resolution metrics, and status toggles.
* **AI Co-Pilot Recommendations:** The AI automatically scans user messages and suggests quick action scripts or template responses to the live agent.

---

## 🏛️ Enterprise Architecture & Design Patterns (New)

The application has been upgraded with industry-standard design patterns and corporate bank-level architectures:

### 1. Caching Pattern (Decorator & Memory Cache)
* **Design:** Implemented using the **Decorator Pattern**. The HTTP-based `MarketRateService` is wrapped inside `CachedMarketRateService` without altering existing client code (Open-Closed Principle).
* **Behavior:** Serbest piyasa rates are cached in the application's memory (`IMemoryCache`) for **5 minutes**, drastically reducing external network latency and protecting the system against third-party API rate limits or IP bans.

### 2. Hosted Background Services (Worker / Cron Job)
* **Design:** Built using .NET's built-in **`BackgroundService` (IHostedService)**.
* **Behavior:** The `StandingOrderExecutionWorker` runs otonomously every 30 seconds in the background. It scans pending, active standing orders, executes transfers in a secure DB transaction, writes audit logs, and shifts the execution date to the next period (Daily, Weekly, Monthly).

### 3. Immutable Audit Trail (Audit Logs)
* **Design:** Designed for security audit compliance.
* **Behavior:** Every critical user action—Registration, Login, Password Reset, Fund Transfers, Exchange Transactions, and Account Closures—is logged with specific detail payloads, IP addresses, and timestamps into an immutable `AuditLogs` table.

### 4. Global Exception Middleware & RFC 7807 (Problem Details)
* **Design:** Centralized error handler built as an ASP.NET Core middleware.
* **Behavior:** Fledgling exceptions are caught at the pipeline root. The client receives standardized, structured JSON objects complying with the **RFC 7807 (Problem Details for HTTP APIs)** standard, keeping controllers clean of boilerplate try-catch blocks.

### 5. Validation Pipeline (FluentValidation)
* **Design:** Separates model validation rules from business logic.
* **Behavior:** `RegisterDtoValidator` and `TransferRequestDtoValidator` perform strict validations (TCKN 11-digit checks, 6-digit PIN checks, positive amount checks) in the API request lifecycle.

### 6. Automated Unit Tests (xUnit & Moq)
* **Design:** Implemented inside `SmartBank.Tests` project using `xUnit` and `Moq`.
* **Behavior:** Utilizes an EF Core `InMemory` database (with transaction warning overrides) to run isolation tests for the core fund transfer logic (Sufficient Balance, Insufficient Funds, and Transfer-to-Self edge cases) with 100% success rate.

---

## 🛠️ Technology Stack

* **Backend:** .NET 10.0 (C#), EF Core, MS SQL Server, SignalR, BCrypt.NET
* **Frontend:** Semantic HTML5, Vanilla CSS3 (Custom Variables, Keyframes, Glassmorphism), ES6+ JavaScript, Chart.js
* **AI:** Ollama (Llama 3/Local LLM) and Gemini API with semantic RAG retrieval
* **Testing:** xUnit, Moq, Microsoft.EntityFrameworkCore.InMemory

---

## 🌐 Live Demo & Deployment

The application is fully deployed and accessible on the cloud:
* **Frontend Web App (GitHub Pages):** [https://alonessam.github.io/SmartBank-/](https://alonessam.github.io/SmartBank-/)
* **Backend REST API (Render Docker):** `https://smartbank-fintech-api.onrender.com`
* **Database (Supabase PostgreSQL):** Configured via Session Connection Pooler for maximum security and scalability.

---

## ⚙️ Setup & Configuration

### 1. Database Initialization
Before running the API, verify your connection string in `appsettings.json` (defaults to SQL Server LocalDB) and run migrations:
```bash
dotnet ef database update --project src/SmartBank.Infrastructure --startup-project src/SmartBank.API
```

### 2. Configure API Keys
Open `src/SmartBank.API/appsettings.json` and customize the configuration keys:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=SmartBankDb;..."
  },
  "JwtSettings": {
    "Key": "YOUR_SUPER_SECRET_JWT_KEY_HERE"
  },
  "GeminiSettings": {
    "ApiKey": "YOUR_GEMINI_API_KEY_HERE"
  }
}
```

### 3. Run the Backend API
```bash
dotnet run --project src/SmartBank.API --launch-profile http
```

### 4. Run the Client Portal
Open `src/SmartBank.Web/index.html` directly in a browser or host it with any static web server (such as VS Code's Live Server).

---

## 🧪 Testing Credentials (Fresh Database Setup)
Since the database has been fully reset to a clean state, please register a new user using the **Register** tab:
1. Navigate to the login page and click **Register here**.
2. Fill in your T.C. Identity Number (TCKN), Name, Email, and a 6-digit PIN password.
3. Upon registration, you can log in immediately. Your default TRY bank account will be automatically opened.

---
---

# 🇹🇷 SmartBank - Premium Fintech & Dijital Bankacılık Portalı

SmartBank, modern kullanıcı arayüzü (UI/UX), güvenlik standartları, canlı piyasa verileri, yapay zeka kabiliyetleri ve gelişmiş finansal iş akışlarını harmanlayan kurumsal düzeyde bir dijital bankacılık ve fintech portalıdır. Proje, **.NET 10.0 (ASP.NET Core Web API)** ve **Vanilla HTML5/CSS3/JS** altyapısıyla geliştirilmiştir.

---

## 🚀 Öne Çıkan Özellikler

### 1. Çoklu Hesap ve Varlık Yönetimi
* **Çeşitlendirilmiş Cüzdanlar:** TRY, USD, EUR vadesiz hesapları ile XAU (Altın) ve XAG (Gümüş) varlık hesaplarının anlık takibi.
* **Bakiye Aktarımlı Hesap Kapatma:** Bir hesabı kapatırken, içerisindeki bakiye canlı kur dönüşümleri ile diğer hesabınıza tek tıkla aktarılır ve hesap silme işlemi güvenli şekilde tamamlanır.
* **Hesap Sınırı:** Aktif kullanıcıların sistemde daima en az bir adet hesabı olması zorunlu kılınmıştır.

### 2. Canlı Döviz & Kıymetli Maden Alım Satımı
* **Dinamik Hesaplama:** Kullanıcı alacağı veya satacağı tutarı yazarken, işlem kuru ve toplam karşılık tutarı sayfa yenilenmeden dinamik olarak hesaplanır.
* **Otomatik Hesap Açma:** Satın alınan döviz veya maden cinsine ait bir cüzdan/hesap kullanıcının hesabında yoksa, sistem bunu otomatik olarak açar.

### 3. Kredi Kartı İşlemleri Paneli
* **Tek Kart Kuralı:** Kullanıcılar güvenlik ve sadelik adına en fazla 1 adet kredi kartına sahip olabilir.
* **Görsel Kart Özelleştirici:** Neon ışıma temalı (Midnight Black, Emerald Green, Neon Blue) interaktif kredi kartı önizlemesi.
* **Borç ve Ekstre Yönetimi:** Faturalandırma dönemi ekstre borcu, asgari ödeme tutarları ve tanımlı hesaplardan borç ödeme sistemi.

### 4. Kayıtlı Alıcılar & Hızlı Transferler
* **Kayıtlı Alıcı Rehberi:** Sık para gönderilen kişileri IBAN ile ekleme, rumuz (alias) düzenleme ve rehberden kaldırma.
* **Hızlı Doldur:** Transfer yaparken kayıtlı kişiyi seçerek tüm bilgilerin formu otomatik doldurmasını sağlama.

### 5. Yapay Zeka Destekli Canlı Destek & SignalR
* **Hibrit Destek Hattı:** **SignalR** tabanlı anlık sohbet. Destek talepleri önce RAG (Retrieval-Augmented Generation) kullanan yerel yapay zeka botu tarafından yanıtlanır, gerektiğinde canlı temsilci paneline aktarılır.
* **Temsilci Çalışma Alanı:** Destek temsilcileri için aktif sohbet kuyrukları, ortalama yanıt süreleri ve AI Co-Pilot öneri widget'ları sunan gelişmiş dashboard.

---

## 🏛️ Kurumsal Mimari ve Tasarım Kalıpları (Yeni)

Yazılım mimarisi standartlarına uygun olarak projeye eklenen kurumsal altyapılar:

### 1. Önbellek Yapısı (Decorator & Memory Cache)
* **Tasarım:** **Decorator Tasarım Kalıbı** kullanılmıştır. `MarketRateService` sınıfı, mevcut istemci kodları değiştirilmeden `CachedMarketRateService` ile sarmalanmıştır (Açık-Kapalı Prensibi).
* **Davranış:** Canlı döviz kurları sunucu belleğinde (`IMemoryCache`) **5 dakika** boyunca saklanır. Bu sayede API yanıt süreleri kısalır ve dış servisin rate-limit engellemelerine takılması önlenir.

### 2. Arka Plan Servisleri (Hosted Services / Worker)
* **Tasarım:** .NET yerleşik **`BackgroundService` (IHostedService)** altyapısı kullanılmıştır.
* **Davranış:** `StandingOrderExecutionWorker` arka planda her 30 saniyede bir otonom olarak çalışır. Vadesi gelmiş otomatik ödeme talimatlarını veritabanı transaction'ı içinde işler, audit log yazar ve sonraki periyoda günceller.

### 3. Değiştirilemez Denetim Günlüğü (Audit Trail)
* **Tasarım:** BDDK ve finansal güvenlik denetim standartlarına uygundur.
* **Davranış:** Kullanıcı Kaydı, Giriş, Şifre Sıfırlama, Para Transferi, Döviz İşlemleri ve Hesap Kapatma gibi tüm hassas işlemler detayları ve IP adresleriyle birlikte değiştirilemez `AuditLogs` tablosuna kaydedilir.

### 4. Global Hata Yakalama & RFC 7807 (Problem Details)
* **Tasarım:** Hata yönetimini merkezileştiren ASP.NET Core middleware yapısı.
* **Davranış:** API genelinde oluşan tüm beklenmeyen hatalar yakalanarak istemciye **RFC 7807 (Problem Details)** standardında JSON formatında dönülür. Kod genelinde gereksiz try-catch bloklarının önüne geçilir.

### 5. Validasyon Pipeline'ı (FluentValidation)
* **Tasarım:** Model doğrulama kurallarını iş mantığından ayırır.
* **Davranış:** `RegisterDtoValidator` ve `TransferRequestDtoValidator` sınıfları TCKN, 6 haneli PIN şifresi ve transfer tutarlarını API istek hattı üzerinde sıkı doğrulamalara tabi tutar.

### 6. Otomasyonlu Birim Testleri (xUnit & Moq)
* **Tasarım:** `SmartBank.Tests` projesi altında `xUnit` ve `Moq` kütüphaneleriyle kurgulanmıştır.
* **Davranış:** EF Core `InMemory` veritabanı sağlayıcısı kullanılarak para transferi iş mantığının tüm başarı ve başarısızlık (yetersiz bakiye, kendine transfer) senaryoları %100 başarı oranıyla test edilir.

---

## 🛠️ Kullanılan Teknolojiler

* **Backend:** .NET 10.0 (C#), EF Core, MS SQL Server, SignalR, BCrypt.NET
* **Frontend:** HTML5, Vanilla CSS3 (Neon Glow & Glassmorphism), Javascript (ES6+), Chart.js
* **Yapay Zeka:** Ollama (Llama 3/Yerel LLM) ve Gemini API ile RAG entegrasyonu
* **Test:** xUnit, Moq, Microsoft.EntityFrameworkCore.InMemory

---

## 🌐 Canlı Demo & Dağıtım

Uygulama bulut altyapısı üzerinde canlıya alınmıştır ve test edilebilir durumdadır:
* **Canlı Arayüz (GitHub Pages):** [https://alonessam.github.io/SmartBank-/](https://alonessam.github.io/SmartBank-/)
* **Canlı API Sunucusu (Render Docker):** `https://smartbank-fintech-api.onrender.com`
* **Veritabanı (Supabase PostgreSQL):** Maksimum performans ve güvenlik için Session Connection Pooler üzerinden yapılandırılmıştır.

---

## ⚙️ Kurulum ve Çalıştırma

### 1. Veritabanı Migrasyonları & Seed Verileri
API sunucusunu çalıştırmadan önce `appsettings.json` içindeki bağlantı dizesini kontrol edin ve migrasyonları uygulayın:
```bash
dotnet ef database update --project src/SmartBank.Infrastructure --startup-project src/SmartBank.API
```

### 2. API Anahtarlarını Yapılandırın
`src/SmartBank.API/appsettings.json` dosyasını açarak ilgili anahtarları yerleştirin:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=SmartBankDb;..."
  },
  "JwtSettings": {
    "Key": "YOUR_SUPER_SECRET_JWT_KEY_HERE"
  },
  "GeminiSettings": {
    "ApiKey": "YOUR_GEMINI_API_KEY_HERE"
  }
}
```

### 3. API Sunucusunu Başlatın
```bash
dotnet run --project src/SmartBank.API --launch-profile http
```
API sunucusu `http://localhost:5038` portunda çalışacaktır.

### 4. Arayüzü Açın
`src/SmartBank.Web/index.html` dosyasını tarayıcınızda doğrudan açarak ya da bir Local Web Server (Live Server vb.) üzerinden uygulamayı görüntüleyebilirsiniz.

---
Developed with premium design aesthetics and enterprise-ready C# practices. © 2026 SmartBank Team.
