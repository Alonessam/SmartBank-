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

**Nasıl kanıtladım.** 53 birim test (rastgele nonce, kurcalama, yanlış anahtar, bozuk girdi, hash anahtara bağlı, sütun uzunluğu, servis akışları, eski satır). LocalDB'de geçici bir veritabanında **tüm migration'ları** uyguladım, API'yi çalıştırdım: kayıt, giriş, hesap/kart okuma (CVV boş), yeni hesap açılışında CVV bir kez dönüyor, tekrar okununca boş. Veritabanında CVV sütunu yok, kart şifreli metinleri `v1:` ile başlıyor, kredi kartında hash dolu. Test veritabanı sonra silindi. **Not:** PostgreSQL betiği T4'te henüz gerçek bir PostgreSQL'de çalıştırılmamıştı; T6'da `PostgresUpgradeScriptTests` ile doğrulandı (eski şema → modelle aynı sütunlar, ikinci çalıştırma değişiklik yapmıyor).

**Mülakat soruları.**
- Sabit IV neden kötü? CBC ile GCM farkı nedir?
- GCM'de nonce tekrar kullanılırsa ne olur?
- Neden düz SHA-256 değil HMAC? Anahtar ayrımı (HKDF) neden?
- CVV neden saklanmaz? Şifreli de olsa neden saklanmaz?
- Bozuk bir şifreli değeri okurken neden hata fırlatıp, görüntüleme yolunda yakalıyoruz?

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
- JWT ömrü 7 gün ve iptal edilemiyor. Parola sıfırlandığında eski token'lar süresi dolana kadar geçerli kalır. Çözüm (kısa ömürlü token + yenileme ya da güvenlik damgası) kapsam dışı.
- Kayıt (`register`) hâlâ kullanıcı adı/TCKN'nin alınmış olduğunu söylüyor (kullanıcı sayımı).
- `BankingService`'teki denetim kayıtları hâlâ sabit `127.0.0.1` yazıyor.
- Herkese açık demo'da (`Demo:ExposeOtp=true`) giriş/transfer 2FA'sı fiilen PIN'e düşer. Bu bilinçli bir demo tavizi, bayrakla kontrol ediliyor ve README'de uyarı var.

**Nasıl kanıtladım.** 100 birim test (OTP: yaşam döngüsü, amaç, bağlama, deneme sınırı, bekleme; kilit: eşik, süre sonu, sıfırlama; servis: giriş, kilit, 2FA, iki adımlı sıfırlama, transfer onayı). Bağlama kontrolünü kasıtlı bozunca 4 test düştü (mutasyon kontrolü). LocalDB'de tüm migration'larla çalışan API'ye karşı: 2FA yanıtında kod yok, 5 yanlış PIN sonrası `AccountLocked`, bilinmeyen TCKN ile yanlış PIN aynı yanıt, parola sıfırlama kodsuz başarısız ve yanıtta kod yok, denetim kaydında gerçek IP, hız sınırı IP başına (3 izin, sonra 429 + `Retry-After`, başka IP ayrı kova, auth dışı endpoint etkilenmiyor).

**Mülakat soruları.**
- Parola sıfırlamada neden e-posta kodu şart? Yanıtın TCKN'ye göre değişmemesi neden önemli?
- OTP'yi neden amaca ve işleme bağlıyoruz? "Dynamic linking" nedir?
- 6 haneli bir kod deneme sınırıyla neden güvenli sayılır? Saldırganın 5 denemede başarı olasılığı nedir?
- Hesap kilidinin dezavantajı nedir, nasıl azaltılır?
- Sabit zamanlı karşılaştırma neden? Bilinmeyen kullanıcıda neden yine BCrypt çalıştırıyoruz?
- Uygulama içi hız sınırı ne zaman yetmez?

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

**Mülakat soruları.**
- Kayıp güncelleme (lost update) nedir? İki isteğin akışını adım adım çiz.
- İyimser ile karamsar eşzamanlılık farkı nedir? Bu uygulamada neden iyimser, ne zaman karamsar tercih edersin?
- Neden `xmin`/`rowversion` yerine uygulama yönetimli bir sürüm sayısı?
- Yeniden deneme neden "baştan, taze okumalarla" olmalı? OTP kontrolünü neden yeniden deneme döngüsünün dışına aldın?
- Deadlock nedir, SQL Server'da neden oluştu, nasıl ele aldın? Hangi hatalar yeniden denenebilir?
- Talimat işçisinde iki örnek aynı talimatı nasıl iki kez çalıştırabilirdi? `NextExecutionDate` jetonu bunu nasıl engelliyor?
- Testi önce başarısız görmenin değeri nedir? Bu testi InMemory sağlayıcıyla neden yazamadın?

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
- Docker imajını bu makinede derleyemedim (Docker kurulu değil). Resmî .NET imajının `app` kullanıcısı ve 8080 varsayımı belgelenmiş standart, ama Render'a çıkmadan önce bir kez dağıtıp doğrulamak gerekir.
- Denetim kaydı hâlâ veritabanı seviyesinde değiştirilemez değil (yalnızca uygulama silmiyor). "Immutable" iddiası T9'da README'de düzeltilecek.
- `/health` uçları kimlik doğrulamasız (tasarım gereği: yük dengeleyici çağırır).
- `X-Forwarded-For` güveni: Dockerfile tüm vekilleri güvenilir sayar; yalnızca vekil arkasında çalıştırılmalı.

**Nasıl kanıtladım.** 139 birim ve gerçek-veritabanı testi (CORS politikası: listedeki/benzeri/yanlış şema-port-alt alan, `null`/localhost yalnızca geliştirmede; ara katman: üretimde sızıntı yok, geliştirmede tam; denetim IP'si; servis hata mesajı sızdırmıyor). Canlı API'ye karşı: üretim modunda `/health` 200, `/health/ready` veritabanı yokken 503 ve gövdede bağlantı bilgisi yok, `/weatherforecast` ve `/db-check` 404, veritabanı kapalıyken giriş 500 ama gövde genel mesaj + `traceId`, CORS yalnızca listedeki origin'e `Access-Control-Allow-Origin` veriyor; geliştirme modunda `null` ve `localhost` kabul, kötü origin reddediliyor; tüm 13 migration uygulandı; `InterestRate` `decimal(5,2)`; denetim satırlarında gerçek IP.

**Mülakat soruları.**
- CORS joker + credentials neden tehlikeli? Same-origin policy ile CORS'un ilişkisi nedir? CORS bir sunucu güvenliği mekanizması mı?
- Liveness ile readiness arasındaki fark nedir? Veritabanı kesintisinde hangisi başarısız olmalı?
- Hata ayrıntısını neden istemciye değil loga yazıyoruz? `traceId` ne işe yarar?
- Konteyneri root çalıştırmak neden kötü? 1024 altı portlar neden root ister?
- `.gitignore`'da `[Log]s/` ne eşler? Neden hata?

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
- Rol token'da taşınıyor ve token ömrü 7 gün: bir temsilcinin yetkisi alındığında, token süresi dolana kadar eski token çalışmaya devam eder. Çözüm (kısa ömürlü token + yenileme ya da her istekte rol doğrulama) bu sürümün kapsamı dışında.
- Mevcut canlı veritabanında adında "agent" geçen hesaplar yükseltme sonrası müşteri olur; gerçek personelin README'deki SQL ile terfi ettirilip yeniden giriş yapması gerekir.
- `deposit` ucu bir demo "para yükleme" musluğudur: giriş yapan herkes kendi hesabına 10.000.000 TL'ye kadar ekleyebilir. Gerçek bir sistemde olmaz, README'de belirtilecek.
- Sohbetteki AI yanıtı, oturum sahibinin hesap bilgilerini kullanabiliyor olabilir. Bunun doğrulaması ve istem enjeksiyonu riski bu sürümde incelenmedi.

**Nasıl kanıtladım.** 185 test (46'sı bu göreve ait entegrasyon testi). **Mutasyon kontrolü:** düzeltmeleri geçici olarak eski haline (kullanıcı adı kuralı, rol yok, odaya serbest giriş) getirince **14 test düştü**, geri alınca geçti. Entegrasyon testleri art arda üç çalıştırmada kararlı. Tüm paket SQL Server (LocalDB) ve PostgreSQL 16 ile geçiyor.

**Mülakat soruları.**
- Yetkilendirme (authorization) ile kimlik doğrulama (authentication) farkı nedir? Bu açık hangisiydi?
- "Kullanıcı adında agent geçiyorsa temsilci" neden bir güvenlik açığı? Rol neden istemciden gelmemeli?
- IDOR nedir? Bu projede nerelerde olabilirdi, nasıl test ettin?
- SignalR hub'ında yetkilendirme neden `[Authorize]` ile bitmez? Grup üyeliği neden bir yetki sınırıdır?
- Entegrasyon testi ile birim testi farkı nedir? Bu açığı hangisi yakalar?
- JWT içindeki rol claim'inin dezavantajı nedir? Yetki geri alındığında ne olur?

---

## T9 — README dürüstlüğü

**Sorun.** README, kodun yapmadığı veya doğrulanamayan şeyleri söylüyordu. Bir mülakatçı bunu kodla karşılaştırdığında ilk yakalayacağı şeyler bunlardır ve bir güvenlik projesinde **abartı, açığın kendisi kadar güven kaybettirir**.
- "Bank-level / corporate-level architecture", "high-fidelity", "secure credit card pipelines", "advanced anti-fraud".
- Türkçe bölümde: "**BDDK ve finansal güvenlik denetim standartlarına uygundur**": doğrulanmamış bir uyumluluk iddiası.
- "**Immutable** audit trail": hiçbir şey değiştirmeyi engellemiyordu (yalnızca uygulama silmiyordu).
- "%100 başarı oranı" (3 test için), "otonom", "Fledgling exceptions" gibi yazım/anlam hataları, MS SQL Server'ın üretim veritabanı olduğu izlenimi (üretim PostgreSQL).
- Gerçek anahtarı `appsettings.json`'a yazmayı öğütleyen kurulum talimatı (T1'de düzeltildi).

**Ne yaptım.**
- Girişe "Her şey simülasyondur, gerçek banka değildir" notu; abartılı sıfatlar çıkarıldı, söylenenler kodla örtüşüyor.
- Denetim günlüğü "değiştirilemez" yerine "yalnızca-ekleme (gelenek gereği), kurcalamaya karşı korumalı değil"; uyumluluk iddiası kaldırıldı; hata ara katmanı "RFC 7807 *tarzı*" (alanlar uyuyor ama birebir standart değil).
- **Güvenlik Modeli** tablosu (risk → kodun yaptığı) ve **Bilinen Sınırlamalar** bölümü: demo para musluğu, 7 günlük iptal edilemeyen token ve `localStorage`, örnek başına hız sınırı, düz metin OTP, simüle kartlar, kurcalanabilir denetim, kullanıcı sayımı, gözden geçirilmeyen AI sohbeti, iki veritabanı sağlayıcısı, derlenemeyen Docker değişikliği.
- Mimari diyagramı (Mermaid), eşzamanlılık ve test bölümleri gerçek duruma göre yeniden yazıldı; teknoloji yığını (üretimde PostgreSQL) ve canlı demo notları (soğuk başlangıç, e-posta/SMTP, demo bayrağı).
- `CHANGELOG.md` ve **yükseltme kontrol listesi**: yeni anahtarları üret, ortam değişkenlerini ayarla, SQL betiğini çalıştır, temsilcileri terfi ettir, `main`'e birleştir, sağlık uçlarını kontrol et.

**Neden bu seçim.** Sınırlamaları kendin yazarsan mülakatta bir "gotcha"ya dönüşmez, olgunluk göstergesine dönüşür: "bunun farkındaydım, nedenini ve ne yapacağımı biliyorum". Ayrıca kullanıcıyı (ve sonraki geliştiriciyi) yanıltmamak başlı başına bir mühendislik sorumluluğudur.

### Bir süreç hatası ve dersi (dürüst not)

T5'ten itibaren bazı kod değişikliklerini PowerShell betikleriyle uyguladım. Windows PowerShell 5.1, **BOM'suz** `.ps1` dosyasını sistemin ANSI kod sayfasıyla (bu makinede Türkçe, 1254) okur. Betiğin içine yazdığım Türkçe metinler bu yüzden bozulup (her harf iki yanlış karakter) kaynak dosyalara yazıldı: hata mesajları, bir arayüz mesajı ve **veritabanına giden varsayılan transfer kategorisi `Diğer`**. Derleme ve testler geçti çünkü bozuk metin geçerli bir metindi. Fark etme yolum: README'yi yazarken bir betikte Türkçe karakter görüp dosyaları taramak.

Düzeltme: bozuk dizileri karakter kodlarıyla (kodlamadan bağımsız) geri çevirdim, her şeyi taradım (yalnızca üç dosya), ve **bir daha olmaması için** depodaki tüm metin dosyalarını tarayan bir test ekledim (`EncodingHygieneTests`). Kural: ASCII dışı karakter içeren değişiklikleri betikle değil, UTF-8'e güvenilir bir araçla uygula; "geçen testler doğruluğun kanıtı değildir" dersi.

**Mülakat soruları.**
- Dokümantasyondaki abartı neden bir güvenlik riski? "Immutable" ile "append-only by convention" farkı nedir?
- Bu projenin sınırlamalarını sayabilir misin? Hangisini ilk düzeltirdin ve nasıl?
- Karakter kodlaması hatası nasıl oluştu ve neden testler yakalamadı? Nasıl önledin?
