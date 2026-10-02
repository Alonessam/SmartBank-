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
