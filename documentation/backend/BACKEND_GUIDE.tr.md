# Backend Kılavuzu (Türkçe)

## Model

MailAccount authenticated principal'dır ve MailCredential, MailSession, MailFolder, Mail, Attachment, DeviceToken, SendOperation ve AuditLog varlıklarına doğrudan sahiptir. Application User, rol, kayıt, onay ve admin yönetimi kaldırılmıştır.

## Onboarding

1. İstemci e-postayı `POST /api/accounts/discover` rotasına gönderir.
2. Backend sırayla known provider, DNS SRV, autoconfig, Microsoft Autodiscover ve güvenli host heuristic'lerini dener.
3. Her aday outbound-host ve transport kontrolüne ek olarak kimlik doğrulamasız IMAP ve SMTP bağlantısı, TLS sertifikası ve protokol kontrollerini geçmelidir. Kullanılabilir ilk aday opaque discovery ID arkasında geçici saklanır.
4. İstemci credential'ı `POST /api/accounts/connect` rotasına gönderir.
5. Backend IMAP ve SMTP auth doğrular, normalize hesabı oluşturur veya kullanır, credential'ı şifreler, refresh session oluşturur, JWT + refresh token döndürür ve initial sync kuyruğuna iş bırakır.

Discovery başarısız olursa HTTP 422, `mail_discovery_failed` ve `manualSetupAvailable=true` döner. Hiçbir kayıt yazılmaz. Başarısızlık provider'ın desteklenmediği anlamına gelmez.

Manuel fallback `POST /api/accounts/connect-manual` kullanır. Manuel mod yalnız discovery'yi atlar. SSRF, DNS/IP güvenliği, TLS sertifika, protokol, IMAP auth veya SMTP auth kontrollerini atlamaz.

## Session'lar

JWT `sub`, MailAccountId değeridir; access süresi yapılandırılabilir. Refresh token rastgele üretilir, SHA-256 hash saklanır, `/api/auth/refresh` ile döndürülür ve `/api/auth/logout` ile iptal edilir. Provider refresh token MailCredential'a aittir; backend session'ıyla karıştırılmaz.

## Persistence ve migration

Yeni backend tek temiz Initial EF Core migration kullanır ve varsayılan DB `mailclient_v2` olur. Legacy migration'lar `legacy-backend/` altında değişmeden kalır. Credential'lar Data Protection ile şifrelenir. Normalize e-posta, send idempotency, folder UID, device ownership ve session hash için DB uniqueness constraint'leri vardır.

## Posta ve sahiplik

Korumalı endpoint'ler hesabı JWT üzerinden `ICurrentMailAccount` ile belirler. Caller-supplied account ID'ye güvenilmez. Başka hesaba ait mail/folder/attachment/device ID, 404 döndürür. Send idempotency MailAccountId ve key ile scope edilir. Attachment yolu storage kökünden çıkamaz.

Başarılı bağlantı önce keşfedilen klasörleri yazar, sonra initial sync'i yanıtı bekletmeden async kuyruğa alır. Arka plan senkronu UIDVALIDITY reset, skipped UID, scan cursor, flag reconciliation, oversized mesaj yönetimi ve NeedsReauthentication geçişleriyle incremental UID import yapar. Gönderim MailAccountId başına idempotenttır; SMTP teslimi, Sent kopyası ve audit içerir. Yeni-posta push, Firebase etkinse hesap başına dağıtılır (değilse NoOp), geçersiz token budamasıyla. Provider-specific OAuth2 deferred'dır: credential modeli hazırdır, provider authorization akışı yoktur.

## Hatalar ve gözlemlenebilirlik

API stabil kodlu ProblemDetails kullanır. Doğrulanmış tek `X-Correlation-ID` değeri response, ProblemDetails, application log, HTTP log ve audit satırlarında ortaktır. Serilog JSON application ve HTTP logları yazar. HTTP JSON request ve response body'leri boyut sınırıyla ve recursive maskeleme ile kaydedilir; multipart ve binary body'ler hariç tutulur.

## İstemci akışı

```text
email -> discover -> connect
           |
           +-- hata -> manuel ayarlar -> connect-manual
```

Frontend uygulaması ayrı iştir.
