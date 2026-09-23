# API referansı

🇬🇧 [English](../en/api-reference.md)

Tüm endpoint'lerin özeti. Tam istek/yanıt şemaları ve hata kodları için Swagger
arayüzüne bakın (`/swagger`, yalnızca Development). Mobil istemci için örnekler:
[Flutter rehberi](../flutter-api-integration.md).

**Kurallar**

- Yetki `bearer` = `Authorization: Bearer <accessToken>`; `anon` = token yok.
- Hatalar RFC 7807 problem details'tir; sabit snake_case `code` alanı içerir.
  Bozuk JSON gövdesi → `400` `invalid_request`; yarıda kesilmiş multipart gövde
  gövdesiz `400` alır.
- Her yanıt `X-Correlation-ID` taşır; isteği izlemek için kendi değerinizi gönderebilirsiniz.
- Rate limit: hesap başına dakikada 60 istek (anonimde IP başına) → `429`.
- Posta değişiklikleri remote-first'tür (önce IMAP sunucusunda uygulanır, sonra yansıtılır).
- `Idempotency-Key` (gönderim uçları): zorunlu, en fazla 200 karakter
  (`idempotency_key_required` / `idempotency_key_too_long`).

### Accounts

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| POST | `/api/accounts/discover` | anon | E-posta adresi için IMAP/SMTP ayarlarını keşfeder |
| POST | `/api/accounts/connect` | anon | Keşif sonucundan posta kutusu oluşturur; token döner |
| POST | `/api/accounts/connect-manual` | anon | Manuel sunucu ayarlarıyla posta kutusu oluşturur |
| POST | `/api/accounts/login` | anon | Mevcut posta kutusuna başka cihazdan giriş |
| GET | `/api/account` | bearer | Geçerli hesap |
| POST | `/api/account/reconnect` | bearer | Saklanan kimlik bilgilerini günceller (ör. şifre değişince) |
| DELETE | `/api/account` | bearer | Hesabı ve verilerini siler |
| GET | `/api/account/sessions` | bearer | Oturum açmış cihazları/oturumları listeler |
| DELETE | `/api/account/sessions/{sessionId}` | bearer | Bir oturumu uzaktan kapatır |

### OAuth

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| POST | `/api/accounts/oauth/{provider}/start` | anon | OAuth başlatır (Authorization Code + PKCE); `google` veya `microsoft` |
| POST | `/api/accounts/oauth/{provider}/complete` | anon | `state` + `code` ile OAuth'u tamamlar; token döner |

### Auth

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| POST | `/api/auth/refresh` | anon (refresh token in body) | Refresh token'ı döndürür, yeni çift verir |
| POST | `/api/auth/logout` | anon (refresh token in body) | Bu cihazın oturumunu iptal eder |

### Folders

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| GET | `/api/folders` | bearer | Önbellekteki klasörleri listeler |
| POST | `/api/folders/refresh` | bearer | Klasör listesini sunucudan yeniden okur |
| POST | `/api/folders/{id}/sync` | bearer | Klasör sync'ini kuyruğa alır (202) |

### Mail

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| GET | `/api/mails` | bearer | Posta listesi (`folderId`, `isRead`, `hasAttachments`, `search`, `page`, `pageSize` ≤ 100) |
| GET | `/api/mails/{id}` | bearer | Posta detayı (`isFromMe`: Sent/Drafts postası ya da gönderen hesabın adresiyle büyük/küçük harf duyarsız aynıysa, klasörden bağımsız) |
| GET | `/api/search` | bearer | Önbellekteki postada arama (tüm filtreler opsiyonel, AND; aşağıya bakın) |
| GET | `/api/mails/{mailId}/attachments/{attachmentId}` | bearer | Eki indirir |
| POST | `/api/mails/send` | bearer + `Idempotency-Key` | Posta gönderir (multipart/form-data) |
| GET | `/api/mails/{id}/compose/reply · reply-all · forward` | bearer | Hazır doldurulmuş yazma bağlamı |

`/api/search` filtreleri: `from` = gönderen adresi veya görünen adında büyük/küçük
harf duyarsız "içerir"; `to` = herhangi bir To/Cc/Bcc adresi veya adında büyük/küçük
harf duyarsız "içerir"; `fromDate`/`toDate` alınma zamanına göre filtreler,
`fromDate` dahil, `toDate` hariç; UTC ofseti olmayan değerler UTC kabul edilir.

### Drafts

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| GET | `/api/drafts/{id}` | bearer | Taslağı getirir |
| POST | `/api/drafts` | bearer | Taslak oluşturur (sunucudaki Taslaklar klasörüne) |
| PUT | `/api/drafts/{id}` | bearer | Taslağı değiştirir (dönen `mailId` farklı olabilir) |
| DELETE | `/api/drafts/{id}` | bearer | Taslağı siler |
| POST | `/api/drafts/{id}/send` | bearer + `Idempotency-Key` | Taslağı gönderir ve siler; başarılı gönderim aynı key ile tekrarlanırsa `422 mail_not_draft` yerine kayıtlı sonuç döner (`sent: true`, `draftRemoved: true`) |

### Mail ops

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| PATCH | `/api/mails/{id}/read` | bearer | Okundu durumunu ayarlar (body) |
| POST | `/api/mails/{id}/{op}` | bearer | `op`: read, unread, star, unstar, trash, restore, archive, spam, not-spam (204, gövdesiz) |
| POST | `/api/mails/{id}/move · copy` | bearer | Klasöre taşır/kopyalar (body) |
| POST | `/api/mails/bulk/{action}` | bearer | En fazla 100 `mailIds` üzerinde toplu işlem; `read`, `unread`, `star`, `unstar`, `archive`, `trash`, `restore`, `spam`, `not-spam`, `move` (`move` için `folderId` zorunlu) |

### Conversations

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| GET | `/api/conversations` | bearer | Konuşmaları yeniden eskiye listeler (`page`, `pageSize` ≤ 100); `participants` = tekil gönderen adları (görünen ad, yoksa adres), alfabetik, en fazla 10 |
| GET | `/api/conversations/{id}` | bearer | Konuşma ve mesajları (`includeTrash`, `include=body`); `isFromMe` posta detayındaki gibi |

### Devices

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| POST | `/api/devices` | bearer | Push token kaydeder (posta kutusu+token başına idempotent) |
| DELETE | `/api/devices/{id}` | bearer | Cihazı siler |

### Management

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| GET | `/api/management/runtime-settings` | `X-Management-Key` | Geçerli runtime ayarları + `version` |
| PUT | `/api/management/runtime-settings` | `X-Management-Key` | Ayarları değiştirir; `expectedVersion` ile korunur |
| GET | `/api/management/whitelist` | `X-Management-Key` | Allowlist e-postalarını listeler |
| POST | `/api/management/whitelist/emails` | `X-Management-Key` | E-posta ekler (tekrar/geçersiz atlanır) |
| DELETE | `/api/management/whitelist/emails/{email}` | `X-Management-Key` | E-postayı kaldırır (zorunluysa posta kutusunu devre dışı bırakır) |

### Health

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| GET | `/health · /health/live · /health/ready` | anon | Canlılık / hazırlık (ready: Postgres + depolama) |
