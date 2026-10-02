# SmartBank v1.1 — Savunma Notları

Her görev bittikçe buraya "ne yaptım, neden, hangi alternatifi eledim" notu eklenir.
Mülakatta bu dosyadaki cümleleri **kendi kelimelerinle** anlatabilmen hedeflenir.

---

## T1 — Gizli anahtarlar repodan çıktı

**Sorun.** JWT imza anahtarı `appsettings.json` içinde ve `Program.cs` ile `AuthService.cs`'te "varsayılan" olarak yazılıydı. AES anahtarı ve IV `EncryptionHelper.cs` içindeydi. Repo herkese açık olduğu için bu değerler herkesin elindeydi. Anahtarı bilen biri, istediği kullanıcı adına geçerli bir JWT üretebilir.

**Ne yaptım.**
- `JwtSettings` sınıfı: ayarları tek yerde okur ve doğrular. Anahtar yoksa veya 32 bayttan kısaysa uygulama **başlamaz** (fail fast). Kod içinde varsayılan anahtar yok.
- `EncryptionHelper.Configure(...)`: AES anahtarını (base64, 32 bayt) başlangıçta yapılandırmadan alır. IV artık koda gömülü değil, anahtardan türetiliyor (geçici; T4'te AES-GCM + rastgele nonce ile kalkacak).
- Yerelde `scripts/dev-secrets.ps1` rastgele anahtarlar üretip `dotnet user-secrets` deposuna yazar. Üretimde (Render) ortam değişkeni: `JwtSettings__Key`, `Encryption__Key`.
- `appsettings.json`'da gizli değer yok.

**Neden dosyadan silmek yetmedi.** Git geçmişi anahtarı hâlâ içeriyor. Bu yüzden eski anahtar **sızmış sayılır**; güvenlik, yeni bir anahtar üretip Render'da onu kullanmaktan gelir. Eski anahtarla imzalanmış tüm token'lar yeni anahtarla doğrulanamaz, yani geçersiz olur.

**Elenen alternatifler.**
- *Anahtarı sadece `appsettings.json`'dan silmek:* sızıntıyı kapatmaz.
- *`appsettings.Development.json`'a geliştirme anahtarı koymak:* aynı hatayı tekrar eder (repoya anahtar girer). user-secrets bu yüzden seçildi.
- *Options pattern + `ValidateOnStart`:* uygun bir seçenek, ancak tek bir statik fabrika (`JwtSettings.From`) hem `Program.cs` hem `AuthService` için yeterli ve test etmesi daha kolay.

**Nasıl kanıtladım.** Birim testler (anahtar yok/kısa/geçerli). Elle: anahtarsız çalıştırınca uygulama net bir hata mesajıyla çıkıyor; anahtarlı çalıştırınca açılıyor; token'sız ve saldırganın uydurduğu anahtarla imzalanmış token'lı istek 401 alıyor.

**Mülakat soruları.**
- Anahtarı git geçmişinden silmek neden yetmez?
- HS256 neden en az 256 bit anahtar ister?
- "Fail fast" nedir, neden varsayılan anahtar vermedim?
- user-secrets ile ortam değişkeni arasındaki fark nedir?

---

## T2 — CI ve bağımlılık güvenliği

**Sorun.** README "testler %100 başarılı" diyordu ama bunu her değişiklikte kontrol eden bir mekanizma yoktu. Ayrıca derleme `Microsoft.OpenApi 2.0.0` için yüksek önem dereceli bir uyarı veriyordu (GHSA-v5pm-xwqc-g5wc / CVE-2026-49451).

**Ne yaptım.**
- GitHub Actions workflow'u (`.github/workflows/ci.yml`): restore, build, test (kapsam raporuyla) ve ayrı bir **bağımlılık denetimi** işi. Denetim `dotnet list package --vulnerable --include-transitive` çıktısında açık bulursa işi başarısız yapıyor. (Komut açık bulsa bile 0 döndüğü için çıktıyı `grep` ile kontrol ediyorum.)
- `Microsoft.OpenApi` için API projesine açık `2.7.5` referansı ekledim. Açık paket `Microsoft.AspNetCore.OpenApi`'nin geçişli (transitive) bağımlılığıydı, doğrudan sürüm vermek geçişli sürümü ezer.
- Dependabot: NuGet ve GitHub Actions için haftalık güncelleme PR'ları.
- CI rozeti README'de.

**Açığın gerçek etkisi (dürüst değerlendirme).** Açık, *güvenilmeyen bir OpenAPI belgesini ayrıştırırken* döngüsel şema referansıyla süreci çökertiyor (hizmet dışı bırakma). SmartBank OpenAPI belgesi yalnızca *üretiyor*, dışarıdan belge ayrıştırmıyor. Yani pratik risk düşük. Yine de "bilinen açık yok" kuralını otomatik denetlemek, her açığı tek tek yorumlamaktan daha güvenli bir politikadır.

**Elenen alternatifler.**
- *Sadece uyarıyı yok saymak (`NuGetAudit` kapatmak):* sorunu gizler.
- *`Microsoft.AspNetCore.OpenApi`'yi yükseltmek:* ilgili paketin yeni sürümü aynı geçişli bağımlılığı çekmeyebilir; açık sürüm vermek hedefi garanti ediyor.
- *`dotnet format` zorunluluğu:* mevcut kod tabanında çok sayıda stil farkı çıkardı, kapsamı şişirirdi. Sonraya bırakıldı.

**Nasıl kanıtladım.** Yerelde: açık taraması "açık yok", build ve 19 test yeşil, Development ortamında `/openapi/v1.json` 200 dönüyor (33 endpoint). GitHub'da: workflow'un dal push'unda çalışması.

**Mülakat soruları.**
- Geçişli (transitive) bağımlılık nedir, güvenlik açığı oradaysa nasıl düzeltirsin?
- CI neden sadece "test geçiyor" değil, bağımlılık denetimini de içermeli?
- Bir güvenlik açığının "gerçek etkisi" nasıl değerlendirilir? (Bu açık için neden düşük?)

---

## T3 — Güvenli rastgelelik

**Sorun.** Tek seferlik şifre (OTP), kart numarası, CVV, hesap numarası ve hesap kodu `System.Random` ile üretiliyordu (18 yerde, `new Random()`). `System.Random` istatistiksel bir üreteçtir, kriptografik değildir: dışarıdan gözlenen çıktılardan iç durumu yeniden kurulabilir ve sonraki değerler tahmin edilebilir. Bir saldırgan OTP'yi tahmin ederse 2FA anlamsızlaşır.

**Ne yaptım.**
- `SecureRandom` yardımcısı (`Core/Common`): `Next(min, max)` ve `Digits(length)`. İkisi de `RandomNumberGenerator` (işletim sisteminin kriptografik üreteci) üzerinden çalışır.
- OTP, hesap kodu, hesap numarası, kart numarası ve CVV üretimi buna taşındı. `BankingService` ve `AuthService`'te kopyalanmış üreteç metotları da kısaldı.
- Birim testler: aralık, boş aralıkta hata, dağılım düzgünlüğü, uzunluk ve rakam kontrolü, tekrar etmeme.
- CI'a koruma adımı: `MarketRateService` (demo fiyat dalgalanması) ve şablon `Program.cs` dışında `new Random(` / `Random.Shared` görülürse build kırılır. Aynı hatanın geri gelmesini otomatik engeller.

**Neden `RandomNumberGenerator.GetInt32`.** `random.Next() % n` gibi bir hesap küçük bir sapma (modulo bias) yaratır; `GetInt32(min, max)` bunu kendi içinde ele alır, aralık içinde düzgün dağılım verir.

**Bilerek bıraktıklarım.** `MarketRateService` `Random` kullanmaya devam ediyor: orada tahmin edilemezlik gerekmiyor, sadece demo fiyatlarına rastgele titreşim ekleniyor. Kart numaraları Luhn kontrol basamağı taşımıyor, bu güvenlik değil gerçekçilik meselesi ve kapsam dışı.

**Nasıl kanıtladım.** 28 test yeşil, yerelde ve CI'da koruma adımı temiz.

**Mülakat soruları.**
- `Random` ile `RandomNumberGenerator` arasındaki fark nedir, ne zaman hangisi?
- Modulo bias nedir?
- OTP'yi tahmin edilebilir üretmek 2FA'ya ne yapar? Ek olarak hangi önlem lazım? (T5: deneme sınırı, kilit.)

---

## T4 — Kart verisi ve şifreleme

**Sorun.**
1. Şifreleme AES-CBC + **sabit IV** idi: aynı kart numarası her zaman aynı şifreli metni verir. Saldırgan veritabanında aynı şifreli değerleri görüp "bu iki hesap aynı kart" diyebilir, bilinen bir düz metni şifreleyip eşleştirebilir. Üstelik CBC veriyi *doğrulamaz*: bozulmuş veri, hata yerine sessizce çöp metin verebilir.
2. Kart tekrarı kontrolü (`BankingService`) tam da bu deterministik şifrelemeye yaslanıyordu: yeni numarayı şifreleyip veritabanında arıyordu.
3. CVV saklanıyor ve her okumada istemciye geri dönüyordu.
4. `Decrypt` hata olunca `"[Decryption Error]"` metnini *döndürüyordu*. Bu metin kart numarası olarak ekrana basılabilirdi.
5. `StandingOrderDto.CreditCardNumber` alanı aslında kartın **şifreli metnini** istemciye veriyordu (frontend kullanmıyordu).

**Ne yaptım.**
- **AES-256-GCM**: her şifrelemede rastgele 12 baytlık nonce. Çıktı biçimi `v1:` + base64(nonce | etiket | şifreli metin). GCM'in doğrulama etiketi sayesinde kurcalanmış veri veya yanlış anahtar **hata fırlatır**, çöp döndürmez. `v1:` öneki ek doğrulanmış veri (AAD) olarak da bağlandı; biçim ileride değişebilir.
- **Anahtar ayrımı**: tek ana anahtardan (`Encryption:Key`) **HKDF** ile şifreleme anahtarı ve hash anahtarı türetilir. Bir kullanımdaki zayıflık diğerini sızdırmaz.
- **Tekrar kontrolü** artık `CardNumberHash` (HMAC-SHA256, hex 64 karakter, unique indeks) ile. Düz SHA-256 yetmezdi: 16 haneli, ilk hanesi sabit bir numaranın arama uzayı küçük, hash'i kaba kuvvetle çözülür. **Anahtarlı** HMAC anahtar olmadan çözülemez.
- **CVV hiç saklanmıyor.** Kart oluşturulurken yanıtta bir kez dönüyor, sonra yok (`CardCvv` boş). Kart saklayan sistemlerde doğrulama kodunun yetkilendirmeden sonra saklanması yasaktır (PCI DSS). Frontend CVV yoksa `•••` gösteriyor.
- `TryDecrypt` ile görüntüleme yolları artık bozuk/eski bir satır yüzünden çökmüyor, boş döner. `CardMasking.LastFour` ile son 4 hane maskeleme tek yerde.
- `StandingOrderDto` artık `CreditCardLast4` döndürüyor (şifreli metin sızıntısı kapandı).
- EF migration `HardenCardData` (CVV sütunları düştü, `CardNumberHash` + filtreli unique indeks) ve üretim için PostgreSQL karşılığı `docs/deploy/v1.1-postgres-upgrade.sql`.

**Neden bu seçimler.**
- *AES-CBC + HMAC (encrypt-then-MAC) yerine GCM:* tek ilkel hem gizlilik hem bütünlük sağlıyor, yanlış birleştirme riski yok, .NET'te `AesGcm` hazır.
- *Rastgele nonce:* GCM'de aynı anahtar+nonce çifti tekrar kullanılırsa güvenlik çöker. 96 bitlik rastgele nonce, bu uygulamanın ölçeğinde çakışma ihtimalini ihmal edilebilir kılar. (Çok büyük hacimde sayaç tabanlı nonce gerekir.)
- *Sütun uzunluğu:* 16 haneli numara için çıktı 60 karakter (`HasMaxLength(100)` içinde). Test, bu sınırı korur.
- *Eski veriyi taşımak yerine sıfırlamak:* eski anahtar zaten T1'de sızmış sayıldı ve değişti, dolayısıyla eski şifreli veriler zaten okunamaz. Veriler demo, taşıma çabası değer katmaz.

**Bilinen sınırlamalar.** Kredi kartı numarası API'den hâlâ tam olarak dönüyor (frontend maskeliyor). Gerçek bir sistemde sunucu tarafında maskelemek gerekir. Anahtar döndürme (rotation) için `v1:` öneki hazır, ama çoklu anahtar desteği yok.

**Nasıl kanıtladım.** 53 birim test (rastgele nonce, kurcalama, yanlış anahtar, bozuk girdi, hash anahtara bağlı, sütun uzunluğu, servis akışları, eski satır). LocalDB'de geçici bir veritabanında **tüm migration'ları** uyguladım, API'yi çalıştırdım: kayıt, giriş, hesap/kart okuma (CVV boş), yeni hesap açılışında CVV bir kez dönüyor, tekrar okununca boş. Veritabanında CVV sütunu yok, kart şifreli metinleri `v1:` ile başlıyor, kredi kartında hash dolu. Test veritabanı sonra silindi. **Doğrulanmayan:** PostgreSQL betiğini gerçek bir PostgreSQL'de çalıştırmadım.

**Mülakat soruları.**
- Sabit IV neden kötü? CBC ile GCM farkı nedir?
- GCM'de nonce tekrar kullanılırsa ne olur?
- Neden düz SHA-256 değil HMAC? Anahtar ayrımı (HKDF) neden?
- CVV neden saklanmaz? Şifreli de olsa neden saklanmaz?
- Bozuk bir şifreli değeri okurken neden hata fırlatıp, görüntüleme yolunda yakalıyoruz?
