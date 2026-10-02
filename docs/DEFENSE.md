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
