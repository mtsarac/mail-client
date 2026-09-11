# Güvenlik Referansı (Türkçe)

> English: [SECURITY.en.md](SECURITY.en.md)

Aşağıdaki her kontrol kodda mevcuttur. Temenni yok.

## Kimlik doğrulama ve oturumlar

- JWT bearer: issuer, audience, imza anahtarı ve süre doğrulanır; `Jwt:Key`
  32 karakterden kısaysa başlatma hata verir. Claim'ler: `sub` (kullanıcı
  id), `role`, `tv` (TokenVersion); 12 saat ömür; HMAC-SHA256.
- İstek başına oturum doğrulama (`UserSessionValidator`): kullanıcı var
  olmalı, `Active` olmalı, güncel `TokenVersion` sunmalı. Rol claim'i her
  istekte DB'den yeniden yazılır; statü/versiyon değişimi oturumları anında
  öldürür.
- Parolalar: Identity `PasswordHasher<User>` (PBKDF2), 8–128 karakter.
  Giriş/kayıt hataları geneldir (hep-`202` kayıt sözleşmesinin ötesinde
  kullanıcı sayımı yok).
- Kayıt modları (`Registration:Mode`): `Open` / `ApprovalRequired`
  (varsayılan) / `Disabled`. Admin onay/kapama/açma + parola sıfırlama
  `TokenVersion` artırır.

## Yetkilendirme ve sahiplik

- Roller `User`/`Admin`; admin rotaları `Admin` ister.
- **Admin posta sahipliğini atlamaz**: her posta/hesap/klasör/cihaz sorgusu
  çağıran `UserId`'ye filtrelenir. Yabancı id'ler `403` değil `404` döner
  (varlık sızdırılmaz).
- Cihaz silme ve token temizliği yazma sırasında sahipliği yeniden kontrol eder.

## Rate limiting

Sabit pencere, aşımda `429` (`RejectionStatusCode`): `auth` istemci IP başına
(kayıt/giriş, 20/dk); `mail-operations` giriş yapmış kullanıcı başına
(test/yenileme/gönderim/okundu/cihaz kayıt+silme, 20/dk). Gerçek istemci IP,
yalnızca güvenilir proxy yapılandırmasında forwarded header'dan gelir.

## SSRF / dış ağ güvenliği

Kullanıcı denetimli IMAP/SMTP hostları her bağlantıdan önce
`OutboundHostValidator`'dan geçer (`MailConnectionHelper.ResolveAllowedAsync`):

1. Literal kontrol: boş, `localhost` veya IP literal → loopback/özel aralıklar
   anında reddedilir.
2. Yoksa **tüm** adresler DNS ile çözülür (`IDnsResolver`) — çözülememe ret demektir.
3. Çözülen her adres kontrol edilir: IPv4'te loopback, özel (10/8,
   172.16/12, 192.168/16), link-local (169.254/16), CGNAT (100.64/10),
   multicast, rezerv/broadcast, dokümantasyon/kıyaslama/aktarma aralıkları;
   IPv6'da multicast, link-local (fe80::/10), unique-local (fc00::/7),
   dokümantasyon; IPv4-eşlemeli IPv6 önce normalize edilir.
4. Bağlantıda doğrulanan IP kullanılır (helper yolunda TOCTOU yeniden çözüm
   boşluğu yok).

Neden: kutu host alanı saldırgan denetimli girdidir; bu yoksa sunucu iç ağları
yoklamaya zorlanabilir. Doğrulayıcı testlerle kapsanır
(`OutboundHostValidatorTests`); testlerde GreenMail/yerel sunucular kullanılır.

## Taşıma güvenliği

`MailSecurity`: `SslOnConnect`, `StartTls`, `None`. `None`, Development/Test
dışında `security` doğrulama hatasıyla reddedilir — production posta trafiği
hep şifrelidir. (MailKit varsayılan sertifika doğrulaması geçerlidir; bunu
gevşeten özel callback yok.)

## Data Protection ve parola saklama

- Kutu parolaları ASP.NET Core Data Protection ile şifreli
  (`DataProtectionCredentialProtector`); halka `DataProtection:KeyPath`'te
  (varsayılan `data/protection-keys`, git'te yok).
- Anahtarlar yeniden başlatmalarda yaşamalı ve kalıcı paylaşımlı birimde tüm
  örneklerce paylaşılmalı, sıkı dosya izinleriyle.
- Production: halka X509 PFX ile şifreli (`CertificatePath` +
  `CertificatePassword`, env/mount ile, asla commit edilmez); dev dışı
  başlatma yokluğunda/bozukluğunda/private-key yokluğunda hızlı hata verir.
  Dev/Test atlayabilir.
- Parolalar yalnız kısa ömürlü posta operasyonlarında çözülür; API yanıtları
  asla parola veya depolama yolu içermez.

## Ekler ve istek boyutları

- Disk yolları yalnız GUID parçalarından kurulur; `LocalFileStorage`
  `GetFullPath` + kök-önek kontrolüyle sabitler (kaçış → hata). Dosya adları
  dosya sistemine hiç değmez (giden adlar 255/150 karaktere normalize).
- Gelen üst sınırlar: ek başına 25 MiB, mesaj başına ek toplamı 50 MiB,
  mesaj 100 MiB (SIZE önden bakış, ölçüm için gövde çekilmez),
  `MaxMessageBytes ≥ MaxMessageAttachmentBytes ≥ MaxAttachmentBytes` doğrulanır.
- Gönderim sınırları: gövde ≤ 1M karakter, ≤ 20 ek; Kestrel + multipart
  limitleri aynı sayılardan türetilir (+1 MiB çerçeve), uç nokta uygulamanın
  reddedeceğini reddeder. Proxy limitleri eşleşmeli.

## Gönderim idempotency'si (çift gönderim önleme)

`SendOperations` kullanıcı+anahtar başına unique, işlemsel claim
(`pg_advisory_xact_lock`), SHA-256 içerik parmak izi. Aynı anahtar+aynı
içerikte kayıtlı sonucu tekrar oynatır; yeniden kullanım çakışmasında `409`;
`DeliveryUnknown` sondur — SMTP kabul etmiş olabileceğinde uygulama asla
otomatik yeniden göndermez. Bkz. [BACKEND_GUIDE.tr.md](BACKEND_GUIDE.tr.md) §9.

## Push kimlik bilgisi yönetimi

Fail-fast başlatma doğrulaması (yalnızca yerel, ağ yok): proje kimliği
zorunlu, servis hesabı JSON'u var/ayrıştırılabilir/`type: service_account`
olmalı, e-posta + geçerli PEM private key içermeli. Çözüm sırası: açık yol →
env → bilinen ADC → `GetApplicationDefault()`. Sırlar loglanmaz. Yalnızca
`Unregistered` token siler. Sunucu JSON'u Flutter'a gömülmemelidir.

## Proxy yönetimi

`X-Forwarded-For/Proto` yalnız `Proxy:KnownProxies/KnownNetworks` doluyken ve
yalnız o ağlardan dikkate alınır (ayrıştırılmış IP/CIDR; bozuk girdiler
yoksayılır). Yoksa doğrudan bağlantı değerleri kullanılır — güvenilmez
ağlardan sahte başlıkların etkisi yoktur.

## Tedarik zinciri ve statik analiz

- CI NuGet denetimi (`dotnet list … --vulnerable --include-transitive`)
  zafiyetli pakette (geçişliler dahil) derlemeyi düşürür.
- Backend push/PR'larında + haftalık zamanlamada CodeQL C#.
- `TreatWarningsAsErrors` (+ CS0618 pragma'sı tek FCM `Token` kullanımına
  kapsamlı, gerekçe yorumlu — kayıt tokenları FID değildir).

## Hata yönetimi

Yakalanmayan hatalar → `UseExceptionHandler` ile ProblemDetails (istemciye
stack trace yok). Doğrulama/auth/limit/posta/sağlayıcı hataları
[API_REFERENCE.tr.md](API_REFERENCE.tr.md) uyarınca 400/401/403/429/404/409/502
eşlenir. Senkron hataları sunucu tarafında loglanır (kullanıcı id + host,
asla parola); push hataları commit edilmiş duruma dokunmaz.

## Dev kolaylıkları vs production zorunlulukları

| Development (gemiye alınmaz) | Production (zorunlu) |
|---|---|
| Düz HTTP, HTTPS yönlendirme yok | Güvenilir reverse proxy arkasında HTTPS |
| Serbest CORS (`AllowAnyOrigin/Headers/Methods`) | CORS politikası yok (tarayıcılar varsayılan engelli) |
| Commit'li config'de dev JWT anahtarı | Env/mount ile güçlü sır |
| `MailSecurity.None` izinli | Yalnız şifreli posta taşıması |
| Data Protection sertifikasız | X509 şifreli paylaşımlı halka |
| Firebase kapalı | Sır-mount servis hesabı |
| Swagger/OpenAPI map'li | Swagger yüzeyi yok |
