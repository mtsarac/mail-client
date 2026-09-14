# API Referansı (Türkçe)

> English: [API_REFERENCE.en.md](API_REFERENCE.en.md)

Temel URL deployment yapılandırmasından gelir. Development Swagger: `/swagger`.

## Onboarding

### Otomatik discovery

`POST /api/accounts/discover`

```json
{ "email": "kisi@ornek.com" }
```

Başarıda `discoveryId`, e-posta, sağlayıcı, auth metotları ve `manualSetupAvailable` döner. İç IMAP/SMTP ayarları sunucuda kalır.

Tüm stratejiler başarısız olursa HTTP 422 ProblemDetails döner:

```json
{
  "title": "Mail server discovery failed.",
  "status": 422,
  "code": "mail_discovery_failed",
  "manualSetupAvailable": true
}
```

Başarısızlıkta hiçbir kayıt yazılmaz. Discovery başarısızlığı sağlayıcının desteklenmediği anlamına gelmez; istemci manuel fallback sunmalıdır.

### Keşfedilen posta kutusunu bağlama

`POST /api/accounts/connect`

```json
{
  "discoveryId": "opaque-temporary-id",
  "authentication": { "type": "Password", "password": "ExamplePassword123!" },
  "deviceIdentifier": "optional-device-id"
}
```

Discovery ID tek kullanımlık ve sürelidir. Backend IMAP ve SMTP credential'larını doğrular, normalize MailAccount oluşturur veya mevcut hesabı kullanır, credential materyalini şifreler, MailSession oluşturur, access/refresh token döndürür.

### Manuel fallback

`POST /api/accounts/connect-manual`

```json
{
  "email": "kisi@ornek.com",
  "username": "kisi@ornek.com",
  "authentication": { "type": "Password", "password": "ExamplePassword123!" },
  "imap": { "host": "imap.ornek.com", "port": 993, "security": "SslOnConnect" },
  "smtp": { "host": "smtp.ornek.com", "port": 465, "security": "SslOnConnect" }
}
```

Manuel mod yalnız fallback'tir. SSRF korumasını, güvenli DNS/IP kontrollerini, TLS sertifika doğrulamasını, protokol kontrolünü, IMAP auth'u veya SMTP auth'u kapatmaz. Password ve AppSpecificPassword çalışır. OAuth2 modelde vardır; sağlayıcı akışları deferred'dır.

## Session'lar

- `POST /api/auth/refresh`, `{ "refreshToken": "..." }`: session ve token döndürür. Access JWT gerekmez.
- `POST /api/auth/logout`, `{ "refreshToken": "..." }`: bu istemci session'ını iptal eder.

Refresh token raw saklanmaz. JWT `sub`, MailAccountId değeridir.

## Hesap kapsamlı API

Aşağıdaki rotalar bearer JWT ister ve hesabı `sub` üzerinden belirler:

| Metot | Yol | Amaç |
|---|---|---|
| GET | `/api/account` | mevcut hesap |
| DELETE | `/api/account` | posta kutusu ve cache verisini sil |
| GET | `/api/folders` | klasörleri listele |
| POST | `/api/folders/refresh` | klasör yenilemeyi kuyruğa al |
| POST | `/api/folders/{id}/sync` | klasör sync isteği |
| GET | `/api/mails` | mevcut hesabın en fazla 100 postası |
| GET | `/api/mails/{id}` | posta detayı |
| PATCH | `/api/mails/{id}/read` | `{ "isRead": true }` |
| GET | `/api/mails/{mailId}/attachments/{attachmentId}` | hesaba ait eki indir |
| POST | `/api/mails/send` | multipart gönderim (to, subject, bodyHtml/bodyText, ≤20 ek, Idempotency-Key başlığı) → 200 `{ sent, sentCopySaved, warning }` |
| POST | `/api/devices` | cihaz token'ını hesaba kaydet |
| DELETE | `/api/devices/{id}` | hesaba ait cihaz token'ını sil |
| GET | `/health` | sağlık yanıtı |

Başka hesaba ait mail, attachment, folder veya device ID değerleri 404 döndürür.

## Stabil hatalar

ProblemDetails; `mail_discovery_failed`, `discovery_expired`, `mail_server_unsafe`, `mail_authentication_failed`, `unsupported_authentication_method`, `invalid_refresh_token`, `session_revoked`, `idempotency_key_required`, `idempotency_conflict` ve `invalid_mail_header` gibi stabil kodlar kullanır.

Beklenen provider/discovery hataları 400/401/409/422/429/502/503 olur. Raw MailKit ve network exception'ları API sözleşmesi değildir.
