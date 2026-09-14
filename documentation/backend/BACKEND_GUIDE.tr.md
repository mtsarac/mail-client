# Backend Kılavuzu (Türkçe)

## Model

MailAccount authenticated principal'dır ve MailCredential, MailSession, MailFolder, Mail, Attachment, DeviceToken, SendOperation ve AuditLog varlıklarına doğrudan sahiptir. Application User, rol, kayıt, onay ve admin yönetimi kaldırılmıştır.

## Onboarding

1. İstemci e-postayı `POST /api/accounts/discover` rotasına gönderir.
2. Backend sırayla known provider, DNS SRV, autoconfig, Microsoft Autodiscover ve güvenli host heuristic'lerini dener.
3. Outbound-host ve transport kontrolünü geçen ilk aday opaque discovery ID arkasında geçici saklanır.
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

Başarılı bağlantı initial sync'i async kuyruğa alır. Cache posta account-scoped kalır. Legacy incremental sync, Sent-folder append, Firebase delivery ve provider-specific OAuth2'nin tam production portu deferred'dır; tamamlanmış gösterilmemelidir.

## Hatalar ve gözlemlenebilirlik

API stabil kodlu ProblemDetails kullanır. Correlation ID `X-Correlation-ID` ile taşınır. Serilog JSON application ve HTTP logları yazar. Audit satırları nullable MailAccountId kullanır. Secret değerler recursive maskelenir; V2 middleware request body loglamaz.

## İstemci akışı

```text
email -> discover -> connect
           |
           +-- hata -> manuel ayarlar -> connect-manual
```

Frontend uygulaması ayrı iştir.
