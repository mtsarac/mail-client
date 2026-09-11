# API Referansı (Türkçe)

> English: [API_REFERENCE.en.md](API_REFERENCE.en.md)

Temel URL (dev): `http://localhost:5223`. İnteraktif doküman (yalnızca dev):
`/swagger`. `POST /api/auth/register` ve `POST /api/auth/login` hariç her
yerde `Authorization: Bearer <JWT>`. Alan adları birebir DTO adlarıdır. Hata
gövdesi: RFC 9457 problem details (`errors: { alan: [mesajlar] }`).

## Kimlik doğrulama

### `POST /api/auth/register` (anonim, `auth` limiti)

```json
{ "email": "kullanici@ornek.com", "password": "gizli123", "displayName": "Ada" }
```

- Geçerli sözdiziminde hep `202 Accepted`: `{ "message": "If the registration request can be accepted, it has been received." }`: yeni/kayıtlı adres ayırt edilmez.
- `400`: bozuk e-posta, 8-128 dışı parola, eksik görünen ad (>250) veya `Registration:Mode=Disabled`.

### `POST /api/auth/login` (anonim, `auth` limiti)

```json
{ "email": "kullanici@ornek.com", "password": "gizli123" }
```

- `200`: `{ "accessToken": "...", "userId": "...", "email": "...", "role": "User|Admin" }` (12 saatlik token).
- `401` yanlış kimlik; `403` aktif olmayan kullanıcı; `400` bozuk gövde.

## Kullanıcılar / Admin (`Admin` rolü gerekli)

| Metot ve Yol | İstek | Yanıt | Kodlar |
|---|---|---|---|
| `GET /api/admin/users` | - | `[{ id, email, displayName, role, status, ... }]` | 200, 401, 403 |
| `POST /api/admin/users` | `{ email, password, displayName, role, status }` | `201` + kullanıcı, `Location: /api/admin/users/{id}` | 201, 400, 409 (yinelenen), 401, 403 |
| `PATCH /api/admin/users/{id}/approve` | - | `204` (statü→Active, tokenlar ölür) | 204, 404, 401, 403 |
| `PATCH /api/admin/users/{id}/disable` | - | `204` (statü→Disabled, tokenlar ölür) | 204, 404, 401, 403 |
| `PATCH /api/admin/users/{id}/enable` | - | `204` (statü→Active, tokenlar ölür) | 204, 404, 401, 403 |
| `POST /api/admin/users/{id}/reset-password` | `{ password }` | `204` (tokenlar ölür) | 204, 400, 404, 401, 403 |

## Posta hesapları (JWT, sahip kapsamlı)

Hesap gövdesi (oluşturma; güncelleme aynı alanlar, `password` opsiyonel):

```json
{
  "emailAddress": "kullanici@ornek.com", "displayName": "İş",
  "username": "kullanici@ornek.com", "password": "kutu-parolası",
  "imapHost": "imap.ornek.com", "imapPort": 993, "imapSecurity": "SslOnConnect",
  "smtpHost": "smtp.ornek.com", "smtpPort": 587, "smtpSecurity": "StartTls",
  "saveSentCopy": true
}
```

Sınırlar: e-posta/kullanıcı adı ≤ 320, görünen ad ≤ 250, kutu parolası ≤ 1024,
port 1-65535, geçerli DNS hostları (loopback/özel/rezerv reddedilir),
`imapSecurity/smtpSecurity` ∈ `SslOnConnect|StartTls` (`None` yalnız dev/test).

| Metot ve Yol | Yanıt | Kodlar / Notlar |
|---|---|---|
| `GET /api/mail-accounts` | `[MailAccountResponse{ id, emailAddress, displayName, username, imapHost, imapPort, imapSecurity, smtpHost, smtpPort, smtpSecurity, saveSentCopy, isActive }]` | 200 |
| `GET /api/mail-accounts/{id}` | `MailAccountResponse` | 200, 404 |
| `POST /api/mail-accounts` | `201` + hesap, `Location` başlığı | 201, 400, 409 yinelenen |
| `PUT /api/mail-accounts/{id}` | `200` + hesap | 200, 400, 404, 409. IMAP kimlik değişimi önbelleği siler |
| `DELETE /api/mail-accounts/{id}` | `204`, önbellek posta + dosyaları siler | 204, 404 |
| `POST /api/mail-accounts/{id}/test` (`mail-operations` limiti) | `MailAccountTestResponse{ succeeded, message }`; başarıda klasörleri de yeniler | 200, 404 |
| `GET /api/mail-accounts/{id}/folders` | `[MailFolderResponse{ id, name, fullName, folderType, uidValidity, isSyncEnabled, isAvailable }]` | 200, 404 |
| `POST /api/mail-accounts/{id}/folders/refresh` (`mail-operations` limiti) | `MailFolderRefreshResponse{ succeeded, message, folders }` | 200, 404 |
| `PATCH /api/mail-accounts/{id}/folders/{folderId}/sync` | `{ "isSyncEnabled": true }` → `MailFolderResponse` | 200, 400, 404 |

## Posta (JWT, sahip kapsamlı)

- `GET /api/mails?folderType=Inbox&page=1&pageSize=30`: ayrıca `accountId`,
  `folderId` filtreleri. `200 MailPageDto{ items: [MailSummaryDto{ id,
  mailAccountId, mailAccountEmail, folderId, folderType, fromDisplayName,
  fromAddress, subject, receivedAt, isRead, hasAttachments }], totalCount,
  page, pageSize }`. `400` hatalı `folderType`/sayfalama (`page ≥ 1`,
  `1 ≤ pageSize ≤ 100`), `404` bilinmeyen hesap/klasör. Sıra
  `ReceivedAt DESC, Id DESC`. Gövde ve yol yok.
- `GET /api/mails/{id}`: `200 MailDetailDto` (özet + `messageId`,
  `toAddress`, `bodyHtml`, `bodyText`, `attachments: [AttachmentDto{ id,
  fileName, contentType, sizeBytes, isInline, contentId }]`). Değilse `404`.
- `GET /api/mails/{mailId}/attachments/{attachmentId}`: dosya bayt akışı
  (`Results.File`). Herhangi bir uyumsuzluk/kayıp dosyada `404`.
- `PATCH /api/mails/{id}/read` (`mail-operations` limiti),
  `{ "isRead": true }` → `200 MailReadDto{ id, isRead }`. `404` bilinmeyen,
  `409` klasör sunucuda değişmiş (yenile + tekrar dene), `502` posta sunucusu
  hatası, `400` doğrulama.

## Gönderim (JWT, `mail-operations` limiti)

`POST /api/mail-accounts/{accountId}/send`: `multipart/form-data` alanları:
`toAddress`, `subject`, `bodyHtml` ve/veya `bodyText`, en fazla 20
`attachments`. **`Idempotency-Key: <uuid>` başlığı zorunlu**
(eksik/boş/200+ karakter → 400).

- `sent=true` iken `200 { sent, sentCopySaved, warning }`.
  `sentCopySaved=false` + uyarı = gönderildi ama Sent kopyası başarısız;
  yeniden gönderme.
- `502` SMTP/taşıma hatası (gönderim kanıtı yoksa aynı anahtarla retry olabilir).
- `404` bilinmeyen hesap; `409` anahtar kullanımda / belirsiz / içerik
  uyumsuzluğu; `400` doğrulama; `429` limit.

## Cihazlar (JWT, sahip kapsamlı, `mail-operations` limiti)

- `POST /api/devices/register` `{ "pushToken": "...", "platform": "android" }`
  → `200 DeviceTokenResponse{ id, platform, registeredAt, lastSeenAt }`.
  `400` boş/500+ karakter token veya platform ∉ {android, ios}.
- `DELETE /api/devices/{id}` → `204`; yabancı/bilinmeyen id'de `404`.

## Sağlık (anonim)

- `GET /health` → `200 { "status": "ok" }`.
- `GET /health/db` → `200 { status: "Healthy", checks: [{ name: "postgres",
  status: "Healthy", error: null }] }` veya `503 Unhealthy`.

## HTTP hata hızlı referansı

| Kod | Buradaki anlamı |
|---|---|
| 400 | doğrulama (`errors` içeren problem details) |
| 401 | eksik/geçersiz/süresi dolmuş JWT, öldürülmüş oturum |
| 403 | kimlik var ama yasak (aktif olmayan kullanıcı, admin rotasında admin olmayan) |
| 404 | bulunamadı **veya** başkasının posta verisi (varlık sızdırılmaz) |
| 409 | yinelenen hesap, UIDVALIDITY değişimi, idempotency çakışması/belirsizliği |
| 429 | limit (`auth` IP başına / `mail-operations` kullanıcı başına, 20/dk) |
| 502 | posta sunucusu işlemi başarısız (IMAP/SMTP/sağlayıcı) |
| 503 | `/health/db` sağlıksız |
