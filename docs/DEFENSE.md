# SmartBank — Mühendislik Notları (v1.1–v1.2)

Her değişiklik için aynı düzen: **sorun**, **ne yaptım**, **neden bu seçim (ve eledikler)**, **bilinen sınırlamalar**.
Bölüm numaraları (T1, T2, ...) bu dosyaya özgüdür; `CHANGELOG.md` bu numaralara atıfta bulunur.
Test sayıları, ilgili görev bittiği andaki sayılardır; güncel paket birkaç yüz test içerir.

> **English summary.** This file is the engineering record of the v1.1 and v1.2 releases, written in Turkish. Each section follows the same
> pattern: the problem found, what was changed, why (and which alternatives were rejected), and the known limitations that remain.
> The sections: T1 secrets out of the repository, T2 CI and dependency audit, T3 secure randomness, T4 card encryption (AES-GCM, no CVV),
> T5 account takeover and brute force, T6 concurrent money movements (optimistic concurrency), T7 clean-up and hardening, T8 role-based
> authorization and integration tests, T9 README honesty, T10 stored XSS, T11 e-mail through an HTTPS API, T12 short-lived access tokens with
> rotating refresh tokens, T13 support-chat limits and forged transfer cards, T14 production schema drift, T15 small hardening (log noise,
> security headers, T.C. Kimlik No check digits). The README summarises the result; `CHANGELOG.md` lists the changes per release; `docs/ARCHITECTURE.md` shows how the parts fit together.

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

**Nasıl kanıtladım.** 53 birim test (rastgele nonce, kurcalama, yanlış anahtar, bozuk girdi, hash anahtara bağlı, sütun uzunluğu, servis akışları, eski satır). LocalDB'de geçici bir veritabanında **tüm migration'ları** uyguladım, API'yi çalıştırdım: kayıt, giriş, hesap/kart okuma (CVV boş), yeni hesap açılışında CVV bir kez dönüyor, tekrar okununca boş. Veritabanında CVV sütunu yok, kart şifreli metinleri `v1:` ile başlıyor, kredi kartında hash dolu. Test veritabanı sonra silindi. **Not:** PostgreSQL betiği T4'te henüz gerçek bir PostgreSQL'de çalıştırılmamıştı; T6'da `PostgresUpgradeScriptTests` ile doğrulandı (eski şema → modelle aynı sütunlar, ikinci çalıştırma değişiklik yapmıyor).


---

## T5 — Hesap ele geçirme ve kaba kuvvet koruması

**Sorun.** Planda "kaba kuvvet" diye başlayan bu görev, kodu okurken üç katı daha ciddi açık çıkardı:
1. **Parola sıfırlama kimlik doğrulamasızdı.** `forgot-password` yalnızca T.C. Kimlik Numarası + yeni PIN alıp parolayı doğrudan değiştiriyordu. TCKN'yi bilen herkes hesabı ele geçirebilirdi. Kaba kuvvet gerekmiyordu bile.
2. **2FA kodu istemciye geri veriliyordu.** Giriş ve transfer yanıtlarının mesajına `|OTP:123456` ekleniyor, ayrıca sunucu konsoluna yazılıyordu. PIN'i bilen biri ikinci faktörü de yanıtın içinden okuyordu: 2FA fiilen yoktu.
3. **Transfer kodu işleme bağlı değildi.** Bir transfer için üretilen kod, 5 dakika içinde *başka bir tutar veya alıcı* için de geçerliydi.
4. Kaba kuvvet: PIN 6 haneli (1.000.000 olasılık), OTP 6 haneli, deneme sınırı yok. BCrypt her denemeyi yavaşlatır ama saldırganı durdurmaz.
5. Hata mesajları ve yanıt süresi hangi TCKN'nin kayıtlı olduğunu sızdırıyordu (`TcknNotFound`, `UserNotFound`, bilinmeyen TCKN'de BCrypt çalışmadığı için daha hızlı yanıt).

**Ne yaptım.**
- **`LoginLockout`**: 5 yanlış PIN'de hesap 15 dakika kilitlenir. Kilitliyken PIN kontrol edilmez, ek denemeler kilidi uzatmaz. Başarılı girişte sayaç sıfırlanır.
- **`OtpManager`**: kod kriptografik üretilir, 5 dakika geçerli, **tek kullanımlık**, **amaca bağlı** (giriş/transfer/parola sıfırlama; giriş kodu parola sıfırlamada kullanılamaz), **5 yanlış denemede yok edilir** (900.000 olasılıktan 5 tahmin), karşılaştırma **sabit zamanlı**.
- **Transfer kodu işleme bağlı** (dynamic linking): kod; kaynak hesap, hedef hesap ve tutarın SHA-256 özetine bağlı. Tutar veya alıcı değişirse kod geçersiz.
- **Parola sıfırlama iki adımlı**: (1) `forgot-password` e-postaya kod gönderir, TCKN kayıtlı olsun olmasın aynı yanıtı verir, 60 sn'lik bekleme süresiyle posta kutusu bombardımanını önler; (2) `reset-password` kodu ister. Doğrulanmış sıfırlama kilidi de kaldırır.
- **Kod yanıtta dönmüyor.** Yalnızca `Demo:ExposeOtp=true` iken (varsayılan kapalı) giriş/transfer kodları yanıta eklenir. Parola sıfırlama kodu **hiçbir zaman** yanıtta dönmez.
- **Eşit zamanlı ve eşit yanıt**: bilinmeyen TCKN için de bir BCrypt doğrulaması yapılır; "kullanıcı yok" ve "yanlış PIN" aynı yanıtı verir.
- **IP başına hız sınırı** (`/api/auth/*`, varsayılan dakikada 10): kaba katman. Hassas katman hesap kilidi ve OTP sayacıdır.
- **Denetim kaydı**: başarısız giriş, kilitlenme ve sıfırlama talepleri gerçek istemci IP'siyle yazılıyor.
- Tekrarlanan iki SMTP gönderici tek `IOtpDelivery`'de toplandı; e-posta içeriği HTML-kodlanıyor, kod ve adres loga yazılmıyor.

**Neden bu seçimler.**
- *Kilit mi, yoksa artan gecikme mi?* Basit ve açıklaması kolay olduğu için kilit. Dezavantajı: saldırgan, bir kurbanın TCKN'sini sürekli deneyip onu kilitleyebilir (kilitleme ile hizmet engelleme). Etki 15 dakikalık geçici kilitle ve IP hız sınırıyla sınırlı. Gerçek bir bankada ek olarak cihaz/IP tabanlı risk puanlaması olur.
- *Kodu neden hash'lemedim?* Kod 5 dakikalık ömürlü ve 5 denemelik. Veritabanı sızsa bile pencere çok dar. Üretimde yine de özet saklanır; bilinen sınırlama.
- *Hız sınırı neden uygulama içi?* Tek örnekli bir demo için yeterli. Çok örnekli bir dağıtımda sınır örnekler arasında paylaşılmaz; orada Redis tabanlı bir sınırlayıcı veya ters vekil (gateway) seviyesinde sınır gerekir.
- *`ForwardedHeaders` neden Dockerfile'da?* Render gibi bir vekilin arkasında tüm istekler vekilin IP'sinden gelir; ayar olmazsa tüm kullanıcılar aynı hız sınırı kovasını paylaşır. İmaj yalnızca vekil arkasında kullanılmalı, doğrudan internete açılırsa `X-Forwarded-For` sahtelenebilir.

**Bilinen sınırlamalar (dürüst liste).**
- JWT ömrü 7 gün ve iptal edilemiyor. Parola sıfırlandığında eski token'lar süresi dolana kadar geçerli kalır. Çözüm (kısa ömürlü token + yenileme ya da güvenlik damgası) bu görevin kapsamı dışındaydı. *(v1.2'de T12 ile giderildi: erişim token'ı artık 15 dakika yaşıyor ve oturumlar sunucuda iptal edilebiliyor.)*
- Kayıt (`register`) hâlâ kullanıcı adı/TCKN'nin alınmış olduğunu söylüyor (kullanıcı sayımı).
- `BankingService`'teki denetim kayıtları hâlâ sabit `127.0.0.1` yazıyor.
- Herkese açık demo'da (`Demo:ExposeOtp=true`) giriş/transfer 2FA'sı fiilen PIN'e düşer. Bu bilinçli bir demo tavizi, bayrakla kontrol ediliyor ve README'de uyarı var.

**Nasıl kanıtladım.** 100 birim test (OTP: yaşam döngüsü, amaç, bağlama, deneme sınırı, bekleme; kilit: eşik, süre sonu, sıfırlama; servis: giriş, kilit, 2FA, iki adımlı sıfırlama, transfer onayı). Bağlama kontrolünü kasıtlı bozunca 4 test düştü (mutasyon kontrolü). LocalDB'de tüm migration'larla çalışan API'ye karşı: 2FA yanıtında kod yok, 5 yanlış PIN sonrası `AccountLocked`, bilinmeyen TCKN ile yanlış PIN aynı yanıt, parola sıfırlama kodsuz başarısız ve yanıtta kod yok, denetim kaydında gerçek IP, hız sınırı IP başına (3 izin, sonra 429 + `Retry-After`, başka IP ayrı kova, auth dışı endpoint etkilenmiyor).


---

## T6 — Eşzamanlı para hareketleri (yarış durumu)

**Sorun.** Bakiye değiştiren her akış "oku → kontrol et → hesapla → yaz" şeklindeydi, arada kilit veya sürüm kontrolü yoktu. İki istek aynı anda aynı bakiyeyi okuyup ikisi de kendi hesabını mutlak değer olarak yazıyordu (**kayıp güncelleme**). Bu gerçek bir veritabanında ölçüldü: önce **testi yazıp düzeltmeden çalıştırdım ve başarısız olduğunu gördüm**; SQL Server ve PostgreSQL'de, toplam para 1000 TL iken 1019,94 / 1211,03 / 1362,10 TL'ye çıktı. Yani sistem **yoktan para üretiyordu** (aynı yarış, bakiyeden fazla harcamaya da izin verir).
Kod okurken aynı sınıftan başka sorunlar da çıktı:
- Kredi kartı limitinde aynı yarış: iki eşzamanlı harcama, birlikte limiti aşabiliyordu.
- Hesap kapatma: bakiye okunduktan sonra değişirse, eski bakiye aktarılıp hesap siliniyordu (para kaybı).
- Talimat işçisi: **herhangi** bir hata talimatı kalıcı olarak kapatıyordu (geçici bir çakışma bile). Hata sonrası bellekte değişmiş bakiyeleri, kapatma kaydıyla birlikte yanlışlıkla kaydedebilirdi. İki işçi örneği aynı talimatı iki kez çalıştırabilirdi.
- İşçi `CreditCardDebt` adını arıyordu, uygulama ise `CreditCardAutoPay` üretiyordu: otomatik kart ödeme talimatları vadesi gelince "geçersiz tutar" ile kapatılıyordu.

**Ne yaptım.**
- **İyimser eşzamanlılık (optimistic concurrency)**: `Account` ve `CreditCard`'a `Version` (int) kolonu. Her `UPDATE`/`DELETE` artık `WHERE Id = @id AND Version = @okuduğumSürüm` ile çalışır; satır arada değiştiyse hiçbir şey yazılmaz ve EF `DbUpdateConcurrencyException` fırlatır. `SaveChanges` geçersiz kılınarak sürüm otomatik artırılır (tek yerde, unutulamaz).
- **Yeniden deneme**: Çakışmada işlem geri alınır, bellek temizlenir ve işlem **taze verilerle baştan** çalışır (en fazla 10 deneme, artan rastgele bekleme ile). Bakiye yeniden kontrol edilir, bu yüzden "bakiye yetersiz" doğru sonucu verir. Aynı yolla **deadlock kurbanı** (SQL Server 1205) ve PostgreSQL `40001`/`40P01` hataları da yeniden denenir: veritabanı bunların "yeniden çalıştır" anlamına geldiğini zaten söyler. (Deadlock'u ilk teşhis ederken testte gördüm.)
- Transfer, OTP kontrolü *bir kez*, para adımı ise *tekrarlanabilir* olacak şekilde ikiye ayrıldı: tek kullanımlık kod, yeniden denemede tüketilmiş olmasın.
- Para hareketi yapan tüm metotlar aynı çatıdan geçiyor: transfer, kart borç ödeme, kart harcama, ekstre kapama, hesap kapatma, döviz alım/satım, para yatırma.
- **Talimat işçisi baştan yazıldı**: her talimat kendi kapsam ve `DbContext`'inde işlenir; çakışmada talimat **kapatılmaz**, sonraki döngüde tekrar denenir; `NextExecutionDate` talimatın eşzamanlılık jetonudur, böylece iki işçi aynı talimatı iki kez çalıştıramaz; gerçek bir hatada bellekteki yarım değişiklikler atılır ve talimat temiz bir `UPDATE` ile kapatılır; `CreditCardAutoPay` tanınır.
- Migration `AddConcurrencyVersions` ve PostgreSQL betiği güncellendi.

**Neden bu seçim (alternatifler).**
- *`xmin` / `rowversion`:* sağlayıcıya özgü (PostgreSQL `xmin`, SQL Server `rowversion`). Bu uygulama iki sağlayıcıyı destekliyor (yerelde SQL Server, üretimde PostgreSQL), uygulama yönetimli tamsayı sürüm ikisinde de aynı çalışıyor.
- *Karamsar kilit (`SELECT … FOR UPDATE`):* satırı okurken kilitler, çakışmayı baştan önler. Dezavantajı: sağlayıcıya özgü SQL, kilit sırası yönetimi (deadlock), kilit süresince bekleme. Bu uygulamada çakışma nadir, iyimser yaklaşım okumayı engellemez ve nadir çakışmada yeniden dener. Çok yoğun tek-satır yarışta (ör. tek "kasa" hesabı) karamsar kilit daha uygun olurdu.
- *Serializable izolasyon:* en güçlü garanti ama hem SQL Server'da hem PostgreSQL'de çok sayıda serileştirme hatası/deadlock üretir ve her yerde yeniden deneme gerektirir; tüm okumalar pahalılaşır.
- *Sürümü yalnız `Balance` ile sınırlamak:* sürüm satır düzeyinde, bakiye dışında bir alan değişse de artar. Daha dar bir jeton (`Balance`'ın kendisi) mümkündü ama "bakiye aynı değere dönmüş" (ABA) durumunda çakışmayı kaçırırdı; ayrı sürüm sayısı bunu yaşatmaz.

**Bilinen sınırlamalar.**
- Çok yoğun tek-satır yarışta 10 deneme yetmeyebilir; bu durumda `ConcurrentModification` ("tekrar deneyin") döner, para yine korunur.
- Döviz alımı sırasında kur okuması ile yazma arasında kur değişebilir (kur sabitlenmiyor).
- `DeleteAccount` ve `Exchange` içinde audit kaydı ayrı bir `SaveChanges` ile yazılıyor; ikisi tek işlemde değil (bakiye/işlem yazımı yine tek atomik adımda).
- Uygulama içi sürüm yalnızca bu uygulamanın EF yolundan korur. Veritabanına doğrudan SQL ile yazan bir araç sürümü artırmazsa çakışma tespiti devre dışı kalır.

**Nasıl kanıtladım.** (1) Gerçek veritabanı testleri: 40 eşzamanlı transfer (toplam bakiyeyi aşan), zıt yönlü transferler, yatırma + transfer karışımı, 30 eşzamanlı kart harcaması (limit aşılmıyor), 4 işçinin aynı talimatı aynı anda çalıştırması (tek kez icra), hatalı talimatın temiz kapanması, kart otomatik ödemesi. Değişmezler: toplam para sabit, hiçbir bakiye eksiye düşmüyor, her başarılı işlem tam bir kez kayıtlı, başarısızlıklar yalnızca beklenen iş sonuçları. Düzeltmeden önce başarısız (yukarıdaki rakamlar), sonra SQL Server (LocalDB) ve PostgreSQL 16'da tekrarlı çalıştırmalarda geçiyor. Deadlock'u bu testler ortaya çıkardı. (2) Gerçek API'ye karşı HTTP: 40 paralel transfer, 33 başarılı, 7 yetersiz bakiye, 0 pes, toplam tam 2000 TL, hesap sürümleri (v33) başarılı transfer sayısına eşit. (3) PostgreSQL yükseltme betiği: eski şema → modelle aynı sütunlar, ikinci çalıştırma değişiklik yapmıyor, unique indeks mevcut. CI, PostgreSQL servisiyle bu testleri her push'ta çalıştırır. Veritabanı bağlantısı yoksa bu testler "atlandı" olarak görünür (sessizce geçmez).


---

## T7 — Temizlik ve sertleştirme

**Sorun (bulduklarım).**
1. **Şablondan kalan, sızdıran uçlar.** `/weatherforecast` (ASP.NET şablonu) canlı API'de duruyordu. `/db-check` kimlik doğrulamasızdı ve hata olunca `exception.Message`'ı (veritabanı hata metni) olduğu gibi döndürüyordu; başarı mesajı da "LocalDB" ifadesini ele veriyordu.
2. **CORS: her origin + credentials.** `SetIsOriginAllowed(_ => true)` ile `AllowCredentials()` birlikte, herhangi bir sitenin (kurbanın ziyaret ettiği) API'yi kurbanın token'ıyla çağırabilmesi demek.
3. **Hata ayrıntısı istemciye gidiyordu.** `GlobalExceptionMiddleware` üretimde `exception.Message`'ı döndürüyordu. Üstüne transfer/ödeme/harcama servisleri `ex.Message`'ı doğrudan iş yanıtına yazıyordu (SQL hatası, bağlantı bilgisi sızabilir).
4. **Denetim kaydında sahte IP.** Altı yerde `IpAddress = "127.0.0.1"` yazılıydı: kayıt "kim, nereden" sorusunu cevaplamıyordu.
5. **Docker imajı root çalışıyordu** ve `EXPOSE 80/443` yazıyordu, oysa .NET 10 imajı 8080 dinler.
6. **`.gitignore` hataları**: `[Db]`, `bbin/`, `[Log]s/` (bu bir karakter sınıfı, "Ls/", "os/", "gs/" dizinlerini eşler, gerçek log dizinlerini değil), `*.副本`.
7. **EF uyarısı:** `Account.InterestRate` için ondalık kesinlik tanımsızdı.
8. **CI:** `System.Random` korumasında `Program.cs` muaf tutuluyordu (şablon yüzünden).

**Ne yaptım.**
- `/weatherforecast` ve `/db-check` silindi. Yerine **`/health`** (canlılık: hiçbir şeye dokunmaz) ve **`/health/ready`** (hazırlık: veritabanına ulaşılıyor mu). İkisi de yalnızca `Healthy`/`Unhealthy` döndürür, ayrıntı loga gider.
- **CORS izin listesi**: yalnızca `Cors:AllowedOrigins` (varsayılan GitHub Pages adresi; üretimde `Cors__AllowedOrigins__0`) kabul edilir. `CorsOriginPolicy` ayrı, test edilebilir bir sınıf. Geliştirme modunda `null` (dosyadan açılan sayfa) ve `localhost` kabul edilir, **üretimde edilmez** (`null` origin, sandbox'lı iframe ve `data:` URL'lerin de gönderdiği bir değerdir).
- **Hata gövdesi**: üretimde genel mesaj + `traceId`; tam istisna yalnızca geliştirme modunda. Servislerin `ex.Message` yansıtması kaldırıldı, hata loglanıyor, istemciye genel mesaj dönüyor.
- **Denetim kaydı**: `IClientInfo` (istek bağlamından gerçek IP) ile altı yer de gerçek adresi yazıyor; istek dışı işler (talimat işçisi) `system:standing-order-worker` yazıyor. `AuthService.Register` da IP alıyor.
- **Dockerfile**: root olmayan `app` kullanıcısı (`USER $APP_UID`), açık `ASPNETCORE_HTTP_PORTS=8080`, doğru `EXPOSE 8080`.
- **`.gitignore`** düzeltildi, log/`TestResults` dizinleri doğru biçimde yok sayılıyor.
- **`InterestRate`** `decimal(5,2)` (migration + PostgreSQL betiği; betik testi kesinliği de karşılaştırıyor).
- **CI**: `System.Random` koruması artık yalnızca `MarketRateService`'i muaf tutuyor. Test adımı `pipefail` ile çalışır (aksi halde `| tee` başarısız testleri gizlerdi) ve **atlanan test varsa CI'yı başarısız sayar**: PostgreSQL servisi bağlanmazsa veritabanı testleri sessizce "geçmiş" görünmesin.

**Neden bu seçimler.**
- *`/health` ikiye bölündü:* orkestratörler canlılık (süreç yaşıyor mu, yeniden başlatılsın mı) ile hazırlığı (trafik alabilir mi) ayırır. Veritabanı kesintisinde süreci yeniden başlatmak çözüm olmaz, o yüzden canlılık veritabanına bakmaz.
- *Origin için `null`'ı neden yalnızca geliştirmede:* README'deki "index.html'i doğrudan aç" akışı çalışmaya devam etmeli, ama üretimde `null`'a izin vermek bir delik.
- *`AllowCredentials` neden hâlâ var:* SignalR istemcisi varsayılan olarak kimlik bilgisiyle bağlanır. Güvenli olması için artık joker değil, açık origin listesiyle birlikte kullanılıyor.

**Bilinen sınırlamalar.**
- Docker imajını bu makinede derleyemedim (Docker kurulu değil). *(Sonradan Render'da derlenip dağıtıldı; üretim bu imajla çalışıyor. v1.3'ten beri CI de imajı her çalıştırmada derliyor.)*
- Denetim kaydı hâlâ veritabanı seviyesinde değiştirilemez değil (yalnızca uygulama silmiyor). "Immutable" iddiası T9'da README'de düzeltilecek.
- `/health` uçları kimlik doğrulamasız (tasarım gereği: yük dengeleyici çağırır).
- `X-Forwarded-For` güveni: Dockerfile tüm vekilleri güvenilir sayar; yalnızca vekil arkasında çalıştırılmalı.

**Nasıl kanıtladım.** 139 birim ve gerçek-veritabanı testi (CORS politikası: listedeki/benzeri/yanlış şema-port-alt alan, `null`/localhost yalnızca geliştirmede; ara katman: üretimde sızıntı yok, geliştirmede tam; denetim IP'si; servis hata mesajı sızdırmıyor). Canlı API'ye karşı: üretim modunda `/health` 200, `/health/ready` veritabanı yokken 503 ve gövdede bağlantı bilgisi yok, `/weatherforecast` ve `/db-check` 404, veritabanı kapalıyken giriş 500 ama gövde genel mesaj + `traceId`, CORS yalnızca listedeki origin'e `Access-Control-Allow-Origin` veriyor; geliştirme modunda `null` ve `localhost` kabul, kötü origin reddediliyor; tüm 13 migration uygulandı; `InterestRate` `decimal(5,2)`; denetim satırlarında gerçek IP.


---

## T8 — Rol tabanlı yetkilendirme ve entegrasyon testleri (yetki açığı)

**Bu görev planda "test kapsamı (3 → 30+)" idi.** Testleri yazarken `[Authorize]`'ın ötesinde ne olduğunu okuyunca, kod tabanındaki **en ciddi yetkilendirme açığını** buldum ve görevi ona çevirdim.

**Sorun.**
1. **"Temsilci" = kullanıcı adında "agent" geçmesi.** `ChatController` bir kullanıcıyı destek temsilcisi sayıyordu, eğer `username.Contains("agent")`. Kayıtta kullanıcı adı serbest olduğu için **herkes `agent_x` adıyla kayıt olup temsilci olabiliyordu**. Frontend de aynı kuralla temsilci panelini açıyordu.
2. **Temsilci uçlarında hiç rol kontrolü yoktu.** `active-sessions` (tüm konuşmalar), `agent-metrics`, `suggest-response/{id}` (herhangi bir oturumun içeriğini okuyup özetliyor) ve `transfer-session/{id}` yalnızca `[Authorize]` (giriş yapmış olmak) istiyordu.
3. **SignalR hub'ı tamamen açıktı.** `JoinSessionAsync`: giriş yapmış herkes **herhangi** bir sohbet odasına girip canlı okuyabiliyordu. `SendMessageAsync`: oturumun sahibi olmayan **herkes otomatik "Agent"** etiketleniyordu, yani bir müşteri başka bir müşterinin sohbetine **bankanın sesiyle** yazabiliyordu ("OTP kodunuzu paylaşın" sosyal mühendisliği için ideal). `CloseSessionAsync`: herkes her oturumu kapatabiliyordu. `RegisterAgentAsync`: herkes "Agents" grubuna girip her yeni müşteri talebini dinleyebiliyordu.
4. **Ölü ve suistimale açık test uçları**: `test-setup`, `test-ai/{id}` (giriş yapmış herkese keyfi istemle AI kotası harcatıyordu), `test-send-message`, `test-rag`. Arayüz hiçbirini kullanmıyordu.

**Ne yaptım.**
- `User.Role` (`Customer`/`Agent`): veritabanı kolonu + JWT'de `role` claim'i + girişte/kayıtta yanıt alanı. **Kayıt her zaman Customer** oluşturur; istek modelinde rol alanı yok. Temsilci, yönetici tarafından veritabanında terfi ettirilir (README'de SQL).
- `ChatController`: `active-sessions`, `agent-metrics`, `suggest-response`, `transfer-session` → `[Authorize(Roles = "Agent")]`. Mesaj okuma: müşteri yalnızca kendi oturumunu, temsilci herhangi birini; karar `User.IsInRole("Agent")` ile, kullanıcı adıyla değil.
- `SupportHub`: `JoinSession` ve `CloseSession` yalnızca oturum sahibi veya temsilci; `SendMessage` göndereni sahip → "User", temsilci → "Agent", başkası → reddedilir; `RegisterAgent` yalnızca temsilci.
- Dört ölü test ucu silindi.
- Frontend: rol artık sunucudan geliyor; temsilci paneli rol `Agent` değilse açılmıyor (asıl koruma sunucuda, bu yalnızca kullanım kolaylığı).
- **Entegrasyon test altyapısı** (`ApiFactory`): gerçek uygulamayı (tüm ara katmanlar, kimlik doğrulama, yetkilendirme, denetleyiciler, SignalR hub'ı) bellek içinde başlatır ve **HTTP/SignalR üzerinden** kullanıcı kaydı + giriş dahil gerçek istemci gibi konuşur.
- Testler: **rol** (kullanıcı adı rol vermez, rol token'da, isteğe rol eklenemez), **sohbet HTTP** (anonim 401, müşteri 403, "agent" adı fark etmez, başkasının konuşması okunamaz, temsilci okur, ölü uçlar 404), **banka IDOR** (başkasının hesap listesi/işlemleri/transferi/yatırma/hesap kapama/kart ekstresi/kart ödeme/harcama/dönem ilerletme, hepsi reddedilir; bakiyeler değişmez), **SignalR** (odaya girememe ve mesajı duymama, bankanın sesiyle yazamama, temsilci olamama ve talepleri duymama, başkasının oturumunu kapatamama, sahip kendi oturumunu kullanabilir, temsilci herhangi birine "Agent" olarak yazabilir, kimliksiz bağlantı reddedilir).

**Neden bu seçimler.**
- *Rol veritabanında, token'da:* sunucu tarafında doğrulanabilir tek kaynak. İstemciden gelen hiçbir şey (kullanıcı adı, istek alanı, localStorage) yetki vermez.
- *Yönetici promosyonu SQL ile:* bir "kendini terfi ettir" ucu veya yapılandırmadaki ad listesi, "ilk kayıt olan kazanır" yarışı yaratırdı. Basit ve savunulabilir olan: yetkiyi uygulama değil yönetici verir.
- *Kullanıcı adı alanını kısıtlamak yerine rolü ayırmak:* `agent` adını yasaklamak yanlış çözüm (isimle yetki zaten kötü fikir); doğru çözüm yetkiyi addan ayırmak.
- *Entegrasyon testleri:* birim testler `ChatController`'ı atlayıp servisleri çağırdığı için bu açığı **göremezdi**. Açık, ara katman ve rota seviyesinde.

**Bilinen sınırlamalar.**
- Rol token'da taşınıyor ve token ömrü 7 gün: bir temsilcinin yetkisi alındığında, token süresi dolana kadar eski token çalışmaya devam eder. Çözüm (kısa ömürlü token + yenileme ya da her istekte rol doğrulama) bu sürümün kapsamı dışındaydı. *(v1.2'de T12 ile token ömrü 15 dakikaya indi; rol değişikliği bir sonraki yenilemede etkili olur.)*
- Mevcut canlı veritabanında adında "agent" geçen hesaplar yükseltme sonrası müşteri olur; gerçek personelin README'deki SQL ile terfi ettirilip yeniden giriş yapması gerekir.
- `deposit` ucu bir demo "para yükleme" musluğudur: giriş yapan herkes kendi hesabına 10.000.000 TL'ye kadar ekleyebilir. Gerçek bir sistemde olmaz, README'de belirtilecek.
- Sohbetteki AI yanıtı, oturum sahibinin hesap bilgilerini kullanabiliyor olabilir. Bunun doğrulaması ve istem enjeksiyonu riski bu sürümde incelenmedi.

**Nasıl kanıtladım.** Paket o gün 185 testten oluşuyordu (sayı sonraki sürümlerde arttı; 46'sı bu göreve ait entegrasyon testi). **Mutasyon kontrolü:** düzeltmeleri geçici olarak eski haline (kullanıcı adı kuralı, rol yok, odaya serbest giriş) getirince **14 test düştü**, geri alınca geçti. Entegrasyon testleri art arda üç çalıştırmada kararlı. Tüm paket SQL Server (LocalDB) ve PostgreSQL 16 ile geçiyor.


---

## T9 — README dürüstlüğü

**Sorun.** README, kodun yapmadığı veya doğrulanamayan şeyleri söylüyordu. Bir okuyucu bunu kodla karşılaştırdığında ilk yakalayacağı şeyler bunlardır ve bir güvenlik projesinde **abartı, açığın kendisi kadar güven kaybettirir**.
- "Bank-level / corporate-level architecture", "high-fidelity", "secure credit card pipelines", "advanced anti-fraud".
- Türkçe bölümde: "**BDDK ve finansal güvenlik denetim standartlarına uygundur**": doğrulanmamış bir uyumluluk iddiası.
- "**Immutable** audit trail": hiçbir şey değiştirmeyi engellemiyordu (yalnızca uygulama silmiyordu).
- "%100 başarı oranı" (3 test için), "otonom", "Fledgling exceptions" gibi yazım/anlam hataları, MS SQL Server'ın üretim veritabanı olduğu izlenimi (üretim PostgreSQL).
- Gerçek anahtarı `appsettings.json`'a yazmayı öğütleyen kurulum talimatı (T1'de düzeltildi).

**Ne yaptım.**
- Girişe "Her şey simülasyondur, gerçek banka değildir" notu; abartılı sıfatlar çıkarıldı, söylenenler kodla örtüşüyor.
- Denetim günlüğü "değiştirilemez" yerine "yalnızca-ekleme (gelenek gereği), kurcalamaya karşı korumalı değil"; uyumluluk iddiası kaldırıldı; hata ara katmanı "RFC 7807 *tarzı*" (alanlar uyuyor ama birebir standart değil).
- **Güvenlik Modeli** tablosu (risk → kodun yaptığı) ve **Bilinen Sınırlamalar** bölümü: demo para musluğu, 7 günlük iptal edilemeyen token (v1.2'de T12 ile giderildi) ve `localStorage`, örnek başına hız sınırı, düz metin OTP, simüle kartlar, kurcalanabilir denetim, kullanıcı sayımı, gözden geçirilmeyen AI sohbeti, iki veritabanı sağlayıcısı, henüz derlenmemiş Docker değişikliği (sonradan Render'da derlendi).
- Mimari diyagramı (Mermaid), eşzamanlılık ve test bölümleri gerçek duruma göre yeniden yazıldı; teknoloji yığını (üretimde PostgreSQL) ve canlı demo notları (soğuk başlangıç, e-posta/SMTP, demo bayrağı).
- `CHANGELOG.md` ve **yükseltme kontrol listesi**: yeni anahtarları üret, ortam değişkenlerini ayarla, SQL betiğini çalıştır, temsilcileri terfi ettir, `main`'e birleştir, sağlık uçlarını kontrol et.

**Neden bu seçim.** Sınırlamaları kendin yazarsan okuyucu için bir sürpriz olmaz, bilinçli bir karar olarak görünür: "bunun farkındaydım, nedenini ve ne yapacağımı biliyorum". Ayrıca kullanıcıyı (ve sonraki geliştiriciyi) yanıltmamak başlı başına bir mühendislik sorumluluğudur.

### Bir süreç hatası ve dersi (dürüst not)

T5'ten itibaren bazı kod değişikliklerini PowerShell betikleriyle uyguladım. Windows PowerShell 5.1, **BOM'suz** `.ps1` dosyasını sistemin ANSI kod sayfasıyla (bu makinede Türkçe, 1254) okur. Betiğin içine yazdığım Türkçe metinler bu yüzden bozulup (her harf iki yanlış karakter) kaynak dosyalara yazıldı: hata mesajları, bir arayüz mesajı ve **veritabanına giden varsayılan transfer kategorisi `Diğer`**. Derleme ve testler geçti çünkü bozuk metin geçerli bir metindi. Fark etme yolum: README'yi yazarken bir betikte Türkçe karakter görüp dosyaları taramak.

Düzeltme: bozuk dizileri karakter kodlarıyla (kodlamadan bağımsız) geri çevirdim, her şeyi taradım (yalnızca üç dosya), ve **bir daha olmaması için** depodaki tüm metin dosyalarını tarayan bir test ekledim (`EncodingHygieneTests`). Kural: ASCII dışı karakter içeren değişiklikleri betikle değil, UTF-8'e güvenilir bir araçla uygula; "geçen testler doğruluğun kanıtı değildir" dersi.


---

## T10 — Saklı XSS (v1.2): başka bir kullanıcının metni sayfada çalışıyordu

**Sorun.** Ön yüz HTML'i `innerHTML` ve şablon dizeleriyle üretiyordu ve başka bir kullanıcıdan gelen metni olduğu gibi içine koyuyordu. En açık örnek havale açıklamasıydı: saldırgan, kurbana 1 TL gönderirken açıklamaya `<img src=x onerror="...">` yazıyor, kurban işlem geçmişini açtığı anda bu kod **kurbanın tarayıcısında, kurbanın oturumuyla** çalışıyordu. Token `localStorage`'da olduğu için `localStorage.getItem('token')` ile okunabiliyordu (yerelde kanıtladım: yük çalıştı, token okundu). Aynı desen kayıtlı alıcı takma adında, sohbet mesajlarında, destek oturumu başlığında, kredi kartı ekstre satırlarında ve borsa adlarında da vardı.

**Neden bu kadar ciddi.** Bu bir "saklı" (stored) XSS: kurbanın bir bağlantıya tıklaması gerekmiyor, sadece kendi hesabına bakması yeterli. Saldırgan, kurban adına istediği API çağrısını yapabilir (para transferi dahil).

**Ne yaptım.**
- `app.js` içine `esc()` yardımcısı ekledim (`& < > " ' \`` karakterlerini kaçırır). Özellikle tırnakları da kaçırıyor, çünkü `data-alias="${c.alias}"` gibi öznitelik bağlamlarında `"` ile öznitelikten çıkıp yeni bir `onmouseover=` eklemek mümkündü.
- Sunucudan veya başka kullanıcıdan gelen **her** şablon enterpolasyonunu `esc(...)` ile sardım (`app.js` ve `chat.js`). Sayılar `toFixed()` ile üretildiği, sabit metinler kodun içinde olduğu için onlara dokunmadım.
- İkinci katman olarak üç sayfaya da **Content-Security-Policy** (`<meta>`) ekledim: `script-src` yalnızca kendi dosyalarımıza ve üç CDN'e izin verir, `unsafe-inline` ve `unsafe-eval` yok, `object-src 'none'`, `base-uri 'self'`. Bunun için `dashboard.html` içindeki yedi satır içi `onclick`/`onsubmit` özniteliğini `addEventListener`'a taşıdım; aksi halde katı bir CSP sayfayı bozardı.
- Aynı saldırıyı yeniden denedim: yük artık işlem satırında düz metin olarak görünüyor, hiçbir `<img>` oluşmuyor, bayrak ve token okuması çalışmıyor.
- `FrontendXssGuardTests`: bilinen tehlikeli enterpolasyonların (`${tx.description`, `${msg.content}`, `${c.alias}` …) ham halde bulunmamasını, `esc()` yardımcısının tırnakları kaçırdığını, her sayfada `unsafe-inline` içermeyen bir CSP olduğunu ve HTML'de satır içi betik/olay özniteliği bulunmadığını doğrular.

**Neden bu seçim (ve eledikler).**
- *`innerHTML` yerine `textContent`?* En sağlam yol bu, ama arayüzün büyük kısmı şablon dizeleriyle yazılmış; hepsini DOM API'sine çevirmek büyük ve riskli bir yeniden yazım olurdu. Kaçırma + CSP aynı korumayı çok daha küçük bir değişiklikle sağlıyor.
- *Sunucuda temizlemek (girişte HTML'i silmek)?* Veri bozulur ve bağlam bilmeden doğru yapılamaz (aynı metin HTML'de, öznitelikte ve JS'te farklı kaçırılır). Doğru yer, çıktının üretildiği yerdir.
- *DOMPurify?* Zengin HTML'e izin vermek gerekirse doğru araç budur; burada hiç HTML'e ihtiyaç yok, düz metin yeterli.

**Bilinen sınırlamalar (dürüst liste).**
- Guard testleri bir *listeyi* denetler; yeni bir enterpolasyon eklenip `esc()` unutulursa test bunu otomatik yakalamaz. Gerçek çözüm şablonları DOM API'sine taşımak ya da bir lint kuralıdır.
- `<meta>` CSP'si `frame-ancestors` ve rapor ayarlayamaz; bunlar yanıt başlığı ister. GitHub Pages özel başlık eklemeye izin vermiyor.
- Token hâlâ `localStorage`'da. XSS'in en kötü sonucu (token çalmak) ancak ömrü kısaltılıp iptal edilebilir yapılırsa küçülür (v1.2'nin sıradaki işi).
- Zincirleme risk: sohbetteki `[CONFIRM_TRANSFER:...]` gibi işaretler mesaj içeriğinden ayrıştırılıyor; sunucunun bunları yalnızca güvenilir kaynaktan kabul ettiği ayrıca gözden geçirilmeli.


---

## T11 — Üretimde e-posta hiç gitmiyordu (v1.2): SMTP yerine HTTPS API

**Sorun.** Şifre sıfırlama ve 2FA kodları e-postayla gidiyordu, ama canlı sistemde hiçbir e-posta gelmiyordu. Arayüz "kod gönderildi" diyordu (kod bilerek hata vermiyor), kullanıcı ise bekliyordu. Neden: Render'ın ücretsiz katmanı giden SMTP portlarını (25, 465, 587) engelliyor; `SmtpClient` bağlanamıyor, hata yalnızca arka plan görevinde loglanıyordu. Yani özellik kodda vardı ama üretimde **çalışamazdı**.

**Ne yaptım.**
- Gönderimi iki parçaya böldüm: `EmailOtpDelivery` (e-postanın içeriğini hazırlar, arka planda gönderir, asla hata fırlatmaz) ve değiştirilebilir bir `IMailTransport`.
- `BrevoMailTransport`: Brevo'nun `POST /v3/smtp/email` HTTPS ucuna gider (443 portu serbest). Anahtar `api-key` başlığında, gönderen adresi Brevo'da doğrulanmış olmalı. `Brevo__ApiKey` verilirse bu kullanılır; yoksa eski `SmtpMailTransport` (yerel geliştirme için) devreye girer.
- Yalnızca anahtar verilip gönderen adresi unutulursa uygulama **başlamıyor** (sessizce çalışıyormuş gibi görünüp hiç e-posta göndermemesinden iyidir).
- Brevo hata verirse log'a yalnızca HTTP durumu ve Brevo'nun hata kodu yazılıyor; yanıt gövdesi yazılmıyor çünkü alıcının adresini içerebilir. Kod ve adres hiçbir zaman loglanmıyor.
- Testler (`EmailDeliveryTests`): istek doğru adrese, doğru başlık ve gövdeyle gidiyor; reddedilince hata mesajı adres sızdırmıyor; yapılandırma eksikse başlamıyor; taşıyıcı çökse bile istek kırılmıyor; kullanıcı adı HTML'e kaçırılıyor (e-posta içinde XSS olmasın).

**Neden bu seçim (ve eledikler).**
- *Başka bir SMTP sağlayıcısı veya farklı port (2525)?* Render yalnızca bilinen SMTP portlarını değil, giden SMTP'yi genel olarak kısıtlıyor; HTTPS API her barındırıcıda çalışır.
- *Brevo SDK paketi?* Tek bir HTTP çağrısı için ek bağımlılık gereksiz; Dependabot/CI yükünü artırır.
- *Gönderimi istek içinde beklemek?* Sağlayıcı yavaşsa giriş yavaşlar. Arka planda gönderiyoruz; bedeli, hatanın kullanıcıya gösterilmemesi (bilinen sınırlama).

**Bilinen sınırlamalar (dürüst liste).**
- Gönderen adresi `@gmail.com` gibi ücretsiz bir adres olduğunda Brevo alan adını imzalayamaz (SPF/DKIM hizalanmaz); bazı alıcılar postayı spama atar. Çözüm kendi alan adı ve DNS kayıtları.
- Gönderim "at ve unut": başarısızlık kullanıcıya bildirilmiyor, yeniden deneme yok. Üretimde kuyruk + yeniden deneme gerekir.
- Ücretsiz katman günde 300 e-posta; yoğun kullanımda sessizce reddedilir.
- Kod hâlâ 5 dakikalık, düz metin OTP (README'deki sınırlama sürüyor).


---

## T12 — 7 gün geçerli, iptal edilemeyen token (v1.2): kısa ömür + dönen yenileme token'ı

**Sorun.** Giriş token'ı (JWT) 7 gün geçerliydi ve sunucu onu **geri alamıyordu**: JWT kendi içinde doğrulanır, veritabanına bakılmaz. Yani şifre sıfırlansa, hesap kilitlense, kullanıcı "çıkış yap"a bassa bile çalınmış bir token bir hafta boyunca para transferi yapabilirdi. Token `localStorage`'da durduğu için (XSS bölümüne bak, T10) çalınması da gerçekçi bir senaryo. "Çıkış yap" aslında yalnızca tarayıcıdaki kopyayı siliyordu.

**Ne yaptım.**
- Erişim token'ı artık **15 dakika** yaşıyor. İptal edilemeyen bir şeyin ömrünü kısa tutmak, hasarın üst sınırını belirler.
- Her girişte ayrıca **yenileme (refresh) token'ı** veriliyor: 256 bit rastgele, ömrü 7 gün, **tek kullanımlık**. `POST /api/auth/refresh` onu yeni bir erişim token'ı ve **yeni** bir yenileme token'ıyla değiştiriyor (rotasyon).
- Veritabanında token'ın kendisi değil **SHA-256 özeti** duruyor (`RefreshTokens` tablosu). Veritabanı sızsa bile çalışan token çıkmaz. Token zaten 256 bit rastgele olduğu için tuzlu/yavaş özet gerekmez (PIN'in aksine).
- Aynı girişten türeyen tüm token'lar bir **aile** (`FamilyId`) oluşturuyor. Kullanılmış bir token **tekrar** gelirse bir kopya dolaşıyor demektir: ailenin tamamı iptal edilir (hem hırsız hem gerçek kullanıcı oturumunu kaybeder, kullanıcı yeniden girer). Denetim günlüğüne `RefreshTokenReuse` yazılır.
- **Çıkış** (`POST /api/auth/logout`), **şifre sıfırlama** ve **hesap kilitlenmesi** oturumları sunucuda iptal ediyor. Aynı anda iki istek aynı token'ı kullanırsa iyimser eşzamanlılık (`Version`, T6'daki desen) yüzünden yalnızca biri kazanıyor; bunu iki veritabanında da gerçek eşzamanlı testle doğruladım (8 istek, tam 1 başarı).
- 10 saniyelik tolerans: iki sekme aynı anda yenilerse kaybeden sekme **hırsız sayılmaz**, yalnızca reddedilir; ön yüz diğer sekmenin yazdığı yeni token'ı alıp devam eder.
- Ön yüz: `fetch` sarmalayıcısı süre dolmadan 30 saniye önce sessizce yeniliyor, 401 gelirse bir kez yenileyip isteği tekrarlıyor, yenileme de reddedilirse çıkış yaptırıyor. Aynı anda gelen 5 istek **tek** yenileme yaptırıyor (tarayıcıda doğruladım). SignalR bağlantısı `accessTokenFactory` ile her bağlanışta güncel token alıyor; token artık URL'ye elle yazılmıyor.
- Süreler yapılandırılabilir (`JwtSettings__AccessTokenMinutes`, `RefreshTokenDays`) ve aralık dışı değer uygulamanın başlamasını engelliyor.

**Neden bu seçim (ve eledikler).**
- *Her istekte veritabanından "iptal edildi mi" bakmak (güvenlik damgası)?* Anında iptal verir ama JWT'nin ana avantajını (durumsuzluk) kaldırır ve her istekte sorgu ekler. Kısa ömür + yenilemede kontrol, çoğu durumda yeterince küçük bir pencere (en fazla 15 dk) bırakıyor.
- *Yenileme token'ını HttpOnly çerezde tutmak?* XSS'ten korur, ama ön yüz (github.io) ile API (onrender.com) **farklı site**; üçüncü taraf çerezlerini tarayıcılar giderek engelliyor ve CSRF koruması gerekir. Bu mimaride kırılgan, o yüzden `localStorage` + XSS savunması (T10) tercih edildi; sınırlamayı açıkça yazdım.
- *Rotasyonsuz uzun yenileme token'ı?* Çalınırsa 7 gün sessizce kullanılır ve fark edilmez. Rotasyon + yeniden kullanım tespiti hırsızı da kullanıcıyı da "ele verir".

**Bilinen sınırlamalar (dürüst liste).**
- Çalınan bir **erişim** token'ı süresi dolana kadar (en çok 15 dk) geçerli kalır; çıkış yapmak onu anında öldürmez.
- Token'lar hâlâ `localStorage`'da: bir XSS hatası hem erişim hem yenileme token'ını okuyabilir. Rotasyon sayesinde hırsız ile kullanıcıdan biri ailenin tamamını düşürür, ama saldırgan kısa süre kullanabilir.
- Rol değişikliği (SQL ile elle) bir sonraki yenilemede etkili olur, anında değil.
- Açık bir SignalR bağlantısı token süresi dolsa da bağlı kalır; token yalnızca bağlanırken denetlenir.
- Temizlik bir arka plan işi değil: kullanıcının süresi bir günden fazla geçmiş token'ları bir sonraki girişte siliniyor.



---

## T13 — Destek sohbeti (v1.2): sınırsız mesaj, sahte "işlem onayı" kartı

**Sorun.** Canlı destek kanalı (SignalR hub) üç açık taşıyordu:
1. **Sınır yoktu.** Giriş yapmış herkes saniyede istediği kadar, istediği uzunlukta mesaj gönderebiliyordu. Müşteri mesajının her biri bir veritabanı yazımı **ve** harici bir AI çağrısı (Gemini) demek: maliyet ve hizmet kesintisi riski. ASP.NET'in hız sınırlayıcı ara katmanı yalnızca bağlantıyı açan HTTP isteğini görür, açık bağlantı üzerinden çağrılan hub metotlarını görmez; yani auth uçlarındaki sınır (T5) burada işe yaramıyordu.
2. **Makine işaretleri taklit edilebiliyordu.** Sohbet, `[CONFIRM_TRANSFER: ...]`, `[TRANSFER_SUCCESS: ...]` gibi köşeli parantezli metinleri arayüzde kart ve **Onayla düğmesine** çeviriyor. Bunu kimin yazdığına bakılmıyordu. Yani bir destek temsilcisi (veya AI modelinin kendisi, ya da müşteri) bir müşterinin sohbetine `[CONFIRM_TRANSFER: source=müşterinin hesabı, destination=başkası, amount=5000]` yazarsa müşteri gerçek bir "Para Transferi Onayı" kartı görüp tıklayabilirdi. "Transfer başarılı" mesajı da aynı şekilde sahte yazılabiliyordu.
3. **Hub transferi doğrulanmıyordu.** REST ucu tutar aralığını (0,01–10 milyon) ve açıklama uzunluğunu (200) denetliyordu; aynı transferi hub üzerinden yapan metot bu denetimleri atlıyordu.

**Ne yaptım.**
- Sunucuda **boyut ve hız sınırı**: mesaj en çok 1000 karakter (boş mesaj reddedilir); kullanıcı başına dakikada 10 ve saatte 100 mesaj (temsilcilerde dakikada 3 kat), saatte 10 yeni sohbet, dakikada 5 sohbet-içi transfer. Sayaç **kullanıcı** başına (bağlantı başına değil), yani ikinci bir sekme açmak hak kazandırmaz. Sınırlar `Chat:*` ile ayarlanır; hub mesaj boyutu 16 KB ile sınırlı.
- **Makine işaretleri sunucuda etkisizleştirilir:** müşteri veya temsilci yazdığı metindeki `[CONFIRM_TRANSFER:` gibi işaretlerin köşeli parantezi `(` olur (okunur ama kart olmaz). AI modelinin yazdığı serbest metinde de aynısı yapılır; yalnızca sunucunun ayrıştırılmış alanlardan kendi ürettiği onay kartı bundan muaf.
- **Arayüz de gönderene bakar:** onay kartı yalnızca `AI` gönderenli, başarı/hata/oda-transferi kartları yalnızca `System` gönderenli mesajlardan üretilir (savunma katmanı 2).
- Hub transferi, REST ucuyla aynı DataAnnotations denetiminden geçer.
- Hub hataları artık sohbet kutusunda gösteriliyor (önceden arayüz `Error` olayını hiç dinlemiyordu); giriş kutularına `maxlength=1000` kondu.
- Testler: işaretlerin her varyantı (büyük/küçük harf, boşluklu), hız sınırlayıcının kayan pencere davranışı (sahte saatle), 200 paralel çağrıda tam sınır kadar izin, hub üzerinden uçtan uca sınırlar.

**Bir hata, testin yakaladığı.** İşaret süzgecinde `RegexOptions.IgnoreCase` kullanmıştım. Türkçe kültür ayarında `I` harfinin küçüğü `ı` olduğu için `[confirm_transfer:` (küçük harf) süzgeçten kaçtı. Test, geliştirme makinesinin Türkçe ayarı sayesinde yakaladı; `CultureInvariant` ile düzeltildi. Ders: kullanıcı girdisini süzen düzenli ifadelerde kültürden bağımsız eşleştirme kullan.

**AI sohbeti gözden geçirmesi (README'deki eski sınırlama).**
- *Ne ifşa edebilir?* Model yalnızca bu oturumun konuşmasını ve oturum **sahibinin** kendi bakiyelerini görür (`GET_BALANCES` oturumun UserId'sine bağlı). Başka bir müşterinin verisine erişimi yok.
- *Prompt injection?* Bir müşteri yalnızca **kendi** sohbetini etkileyebilir. Model para hareket ettiremez; yalnızca bir transfer *önerir* ve müşterinin onayı gerekir; transferin kendisi sahiplik, limit ve OTP denetimlerinden geçer (T5/T6). Modelin yazdığı hiçbir şey komut sayılmaz (yukarıdaki etkisizleştirme).
- *Kalan:* metin ve bakiyeler **harici bir sağlayıcıya (Gemini)** gider; gerçek bir bankada bu bir KVKK/veri paylaşımı konusudur. Model hâlâ garip cevaplar verebilir.

**Neden bu seçim (ve eledikler).**
- *Hız sınırını veritabanında tutmak?* Birden fazla örnekte paylaşımlı olur ama her mesajda ek sorgu demek; bellek içi sayaç (örnek başına) bu proje için yeterli ve README'de zaten "hız sınırı örnek başına" diye yazılı.
- *İşaretleri reddetmek, etkisizleştirmek yerine?* Reddetmek meşru bir mesajı ("[ACTION: ...] ne demek?") kaybettirir; etkisizleştirmek metni korur.
- *Onay kartını sunucuda imzalamak (HMAC'li, tek kullanımlık öneri)?* En sağlam yoldur ve transferi gerçekten AI önerisine **bağlar**. Bu sürümde yapmadım (hub transferi hâlâ istemcinin gönderdiği alanları kabul ediyor, ama bunlar normal transfer ucuyla aynı yetkiyle sınırlı). Sonraki iş olarak not edildi.

**Bilinen sınırlamalar (dürüst liste).**
- Sayaçlar bellekte ve örnek başına: yeniden başlatmada sıfırlanır, birden fazla örnekte paylaşılmaz.
- `ConfirmTransferFromChatAsync` bir AI önerisine bağlı değil: kullanıcı, kendi hesabından, sohbet açıkken herhangi bir transferi hub üzerinden tetikleyebilir (REST ucuyla aynı yetki; ek bir yetki kazanılmıyor).
- AI yanıt süresi/maliyeti için günlük üst sınır yok (dakika/saat sınırları dolaylı olarak sınırlıyor).



---

## T14 — Üretim şeması modelle uyuşmuyordu (v1.2): "Start Session" hiçbir şey yapmıyordu

**Sorun.** Canlı sitede sohbet penceresindeki **Start Session** düğmesi hiçbir şey yapmıyordu. Arayüzde hata yoktu; sunucu `StartSessionAsync` içinde bir istisna fırlatıyordu. Aynı kodu yerelde SQL Server'da ve PostgreSQL'de (modelden üretilmiş tablolarla) denedim: **çalışıyordu**. Fark veritabanındaydı. Üretim tabloları Supabase'de **elle** oluşturulmuştu (`MigrateAsync` bilerek kapalı, bkz. `Program.cs`) ve `ChatSessions` tablosunda kodun yazdığı `IsActive` sütunu yoktu. Yani sohbet üretimde hiç çalışmamıştı; unutulan bir sütun, yalnızca o özelliğe basılınca ortaya çıktı.

**Tüm şemayı karşılaştırdım.** Supabase'den bütün sütun listesini alıp EF'in modelden ürettiği PostgreSQL şemasıyla karşılaştırdım (`docs/deploy/schema-check.sql`). Sonuç:
- **Eksik sütun yok** (112 sütunun hepsi var).
- **`StandingOrders.Amount` NOT NULL**, oysa kod kredi kartı otomatik ödeme talimatında tutarı bilerek boş bırakıyor (tüm ekstreyi öder). Bu talimat üretimde oluşturulamazdı: ikinci bir "yalnızca o özelliğe basınca çıkan" hata.
- **Zaman sütunları `timestamp without time zone`.** API bu yüzden `2026-10-06T10:27:18` gibi **`Z`'siz** döndürüyor; tarayıcı bunu **yerel saat** sanıyor. Türkiye'de (UTC+3) gösterilen her saat 3 saat geriydi (canlı API'den doğruladım). Model `timestamp with time zone` kullanıyor (`RefreshTokens` ve `LockoutEnd` zaten öyle) ve `Z` ile döner.
- Zararsız farklar: `varchar` yerine `text`, bazı sütunların modelden gevşek olması, eski `MarketRates` tablosunun fazladan sütunları (yalnızca "boş mu?" kontrolünde kullanılıyor).

**Ne yaptım.** `docs/deploy/v1.2-postgres-upgrade.sql` betiğine: `ChatSessions.IsActive` ekleme; `StandingOrders.Amount` için `DROP NOT NULL`; tüm zaman sütunlarını `timestamptz`'ye çevirme. Çevirme **mevcut değerleri UTC olarak yorumlar** (`AT TIME ZONE 'UTC'`; üretimde saklanan değerler zaten UTC) ve yalnızca hâlâ "without time zone" olan sütunlara dokunur, yani betik iki kez çalıştırılırsa saatleri tekrar **kaydırmaz**. Gerçek PostgreSQL testi: "eski hâli" kuran (varsayılanı olan bir sütun dahil), bir satır yazan, betiği iki kez çalıştıran ve hem sütun listesini hem de o satırın **aynı UTC anını** koruduğunu doğrulayan bir test.

**Neden bu seçim (ve eledikler).**
- *Sadece `IsActive` eklemek?* Sohbeti düzeltir ama otomatik ödemeyi ve saat kaymasını bırakır. Şemanın tamamına bakmak üç sorunu birden buldu.
- *Arayüzde `Z` eklemek (`new Date(x + 'Z')`)?* Yamadır: her tarih için ayrı yer, her yeni yerde unutulur. Doğru yer veri tipinin kendisi.
- *Üretimde `MigrateAsync` açmak?* pgBouncer (işlem modu) ve Supabase'de elle yönetilen tablolarla çakışır, o yüzden kapalı. Bedeli bu: şema ile kod ayrı ayrı yönetildiği için kayma olur. Karşı önlem: elle çalıştırılan betikleri gerçek PostgreSQL'e karşı test etmek ve `schema-check.sql` ile kontrol etmek.

**Bilinen sınırlamalar (dürüst liste).**
- Şema kontrolü elle yapılıyor (Supabase'de sorguyu çalıştırıp çıktıyı karşılaştırmak); otomatik değil. Sürümler arası şema kayması için gerçek çözüm EF migration'larını üretimde de uygulamak.
- Eski `MarketRates` tablosu hâlâ modelden farklı (`Id` uuid, fazladan `Name`/`NameEn`/`Change`). Tablo boş kalırsa başlangıçtaki tohum ekleme başarısız olur (şu an dolu).
- Betik, tabloları kısa süre kilitler (küçük tablolar; demo için sorun değil).



---

## T15 — Küçük sertleştirmeler (v1.2): log gürültüsü, güvenlik başlıkları, T.C. kontrol basamakları

(`CHANGELOG.md` içindeki "SQL komutları artık loga yazılmıyor", "API güvenlik başlıkları" ve "T.C. Kimlik Numarası kontrol basamakları" maddeleri bu bölümden gelir.)

Üç küçük iş, hepsi "sorun büyük değil ama bir gözden geçirenin ilk bakacağı yerler".

### 1) Üretim logu SQL ile doluydu
**Sorun.** Varsayılan log seviyesi `Information` olduğu için EF Core her SQL komutunu yazıyordu; kalıcı emir işçisi (standing-order worker) 30 saniyede bir sorgu çalıştırdığından Render logunun çoğu `Executed DbCommand` satırıydı. Gerçek hata satırlarını bulmak zorlaşıyor; üstelik SQL parametreleri logda dolaşıyor.
**Ne yaptım.** `Microsoft.EntityFrameworkCore.Database.Command` için seviye `Warning`; bizim kendi `Information` mesajlarımız görünür kalıyor. Yerelde ayrıntı gerekirse `Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information` ortam değişkeniyle açılır. Testi: `LoggingConfigurationTests`.

### 2) API yanıtlarında güvenlik başlığı yoktu
**Ne yaptım.** `SecurityHeadersMiddleware` her yanıta (hata yanıtları dahil, çünkü `OnStarting` kullanıyor) şunları ekler: `X-Content-Type-Options: nosniff` (tarayıcı JSON'u HTML sanmasın), `X-Frame-Options: DENY` ve `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` (API hiçbir zaman bir sayfa/çerçeve içeriği değil), `Referrer-Policy: no-referrer`, ve `/api` ile `/hubs` yanıtlarında `Cache-Control: no-store` (bakiye ve işlemler tarayıcı/ara önbellekte kalmasın). **HSTS**: yalnızca üretimde ve yalnızca HTTPS isteklerinde, 180 gün, `includeSubDomains` yok (onrender.com paylaşımlı).
**Neden bu seçim.** Web arayüzünün kendi başlıkları (GitHub Pages özel başlık eklemeye izin vermez) CSP `<meta>` etiketiyle veriliyor (T10); bu middleware yalnızca API tarafını kapsar.
**Sınırlamalar.** HSTS'in `preload` listesine eklenmedi (geri alınması zor bir taahhüt). HSTS, Render proxy'sinin `X-Forwarded-Proto` başlığını doğru taşımasına bağlı (Dockerfile'daki `ASPNETCORE_FORWARDEDHEADERS_ENABLED`); canlıda `curl -I` ile görülmeli. ASP.NET, `localhost` için HSTS göndermez, bu yüzden test başka bir ana makine adı kullanır.

### 3) T.C. Kimlik Numarası kontrol basamakları
**Sorun.** Kayıt, 11 hane olan her rakam dizisini kabul ediyordu (`99999999999` dahil): yazım hatası kayda girer, "şifremi unuttum" hiç gelmeyen e-postaya giderdi.
**Ne yaptım.** Gerçek algoritma: ilk hane 0 olamaz; 10. hane `((d1+d3+d5+d7+d9)·7 − (d2+d4+d6+d8)) mod 10`, 11. hane ilk 10 hanenin toplamının `mod 10`'u. `TcKimlikNo.IsValid` (sunucu, FluentValidation kuralı) ve aynı kural tarayıcıda (kayıt formunda anında geri bildirim). Testler: bilinen geçerli/geçersiz örnekler ve **geçerli bir numaranın herhangi bir hanesi değiştirilince her seferinde reddedildiği** (110 tek-hane yazım hatası).
**Önemli sınır (dürüst).** Bu **kimlik doğrulama değildir**: numaranın o kişiye ait olduğunu yalnızca resmi MERNİS servisi söyler. Rastgele 11 haneli dizilerin çoğunu eler (yaklaşık 100'de 1'i geçer) ve yazım hatalarını yakalar; birinin gerçek bir numarayı kullanmasını engellemez. Demo için örnek geçerli numaralar: `11111111110`, `10000000146` (kimseye ait değil). Eski `11111111111` gibi sahte numaralarla **yeni kayıt yapılamaz**; zaten kayıtlı hesaplar giriş yapmaya devam eder (kontrol yalnızca kayıtta).
**Neden bu seçim.** Girişte aynı kuralı uygulamak, kuraldan önce açılmış hesapları kilitlerdi.

