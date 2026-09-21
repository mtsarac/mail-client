# Mail Client API — Flutter Entegrasyon Rehberi

> Flutter istemci ekibi için entegrasyon rehberi.

Bir IMAP/SMTP posta istemcisi API'si. Bir hesap aynı anda birden çok cihazdan oturum açabilir — her cihaz kendi refresh token'ıyla bağımsız bir `MailSession` alır, biri diğerini geçersiz kılmaz. Bu rehber, Flutter uygulamasının kimlik doğrulamadan gönderim akışına kadar backend'e doğru şekilde bağlanması için gereken her uç noktayı, alan adını ve hata kodunu kapsar.

- **Base URL (geliştirme):** `http://localhost:5071`
- **Base URL (prod):** dağıtım ortamından alınır
- **Kimlik doğrulama:** `Authorization: Bearer <accessToken>`
- **Gövde biçimi:** `application/json` (aksi belirtilmedikçe; taslak/gönderim uçları `multipart/form-data`)
- **Etkileşimli şema:** `GET /swagger` (Swagger UI, her uç için örnek gövde + olası hata kodları) — bu rehber ile çelişirse Swagger esas alınır.
- **Sağlık:** `GET /health` (canlılık), `GET /health/ready` (Postgres + depolama). Anonim, hız sınırından muaf; uygulama içinde "sunucu ayakta mı" kontrolü için kullanılabilir.
- **Tarih/saat:** tüm zamanlar UTC ISO-8601 (`…Z`); `DateTime.parse(...).toLocal()` ile göster. **ID'ler:** GUID string.

---

## İçindekiler

1. [Hızlı başlangıç](#hızlı-başlangıç)
2. [Bilmen gereken dört kural](#bilmen-gereken-dört-kural)
3. [Kimlik & hesap](#1-kimlik--hesap)
4. [Klasörler](#2-klasörler)
5. [Mail — okuma & arama](#3-mail--okuma--arama)
6. [Mail — durum & taşıma](#4-mail--durum--taşıma)
7. [Yazma, taslak & gönderme](#5-yazma-taslak--gönderme)
8. [Konuşmalar](#6-konuşmalar)
9. [Cihaz & push bildirimleri](#7-cihaz--push-bildirimleri)
10. [Hata kodları](#8-hata-kodları)
11. [Enum referansı](#9-enum-referansı)
12. [Prod erişim listesi (allowlist)](#10-prod-erişim-listesi-allowlist)

---

## Hızlı başlangıç

Bir posta kutusunu bağlamaktan ilk mail listesini çekmeye kadar gerçek istek sırası. Her adımın döndürdüğü değer bir sonrakinde kullanılır.

1. **Sunucuyu keşfet** — `POST /api/accounts/discover` sadece e-posta adresiyle IMAP/SMTP sunucusunu otomatik bulmayı dener. Başarılıysa `discoveryId` döner (10 dakika geçerli).
2. **Bağlan** — Keşif başarılıysa `POST /api/accounts/connect` ile `discoveryId` + şifre gönder. Keşif `422` ile başarısız olursa (`manualSetupAvailable: true`), kullanıcıdan IMAP/SMTP host bilgisini alıp `POST /api/accounts/connect-manual` kullan. İkisi de aynı `TokenResponse`'u döner. Email zaten kayıtlıysa (`409 mail_account_already_exists`) — yani kullanıcı bu hesaba başka bir cihazdan zaten bağlanmışsa — `connect` yerine `POST /api/accounts/login` kullan; hesabı değiştirmeden yeni cihaz için yeni bir `TokenResponse` verir.
3. **Token'ları sakla** — `accessToken` (15 dk ömürlü) ile `refreshToken`'ı (180 gün, kaydırmalı) güvenli depoya (`flutter_secure_storage`) yaz. Her korumalı istekte `Authorization: Bearer accessToken` gönder.
4. **401 aldığında yenile** — `POST /api/auth/refresh` ile `refreshToken`'ı gönder, yeni bir çift al ve eskisinin yerine yaz. Refresh token'lar tek kullanımlıktır, her yenilemede rotasyona girer.
5. **Klasörleri ve mailleri çek** — `GET /api/folders` bağlantı anında zaten dolu gelir. `GET /api/mails?folderId=…` ile listele, `GET /api/mails/{id}` ile detay al.

### Bilmen gereken dört kural

**1. Hata gövdesi tek biçim.** Her hata RFC 9110 Problem Details formatında döner: `{ code, title, status, correlationId }`. İstemci tarafında switch/case'i her zaman `code` alanı üzerinden yaz; `title` insan-okur metindir, sabit kalacağı garanti değildir.

**2. Idempotency-Key zorunlu.** Mail gönderen iki uç (`/mails/send`, `/drafts/{id}/send`) `Idempotency-Key` header'ı ister; yoksa `400 idempotency_key_required`. Uygulama içi bir UUID üret, aynı gönderim denemesinde aynı key'i tekrar kullan (retry'da çift mail gitmesin diye). Aynı key + aynı gövdeyle tekrar çağrı, gönderimi tekrarlamaz — kayıtlı sonuç aynen döner.

**3. Hız sınırı.** Hesap (girişliyse) ya da IP başına dakikada 60 istek (sabit pencere). Aşımda `429` döner, gövdesiz; `Retry-After` header'ı (saniye) beklenmesi gereken süreyi verir — backoff'u ona göre yap. `/health`, `/metrics` hariçtir.

**4. Sayfalama.** `/mails`, `/search`, `/conversations` `page` (1'den başlar) ve `pageSize` alır, `{ items, page, pageSize, total }` zarfıyla döner. `pageSize` sunucuda 100'e kırpılır. `/folders`, `/account/sessions` sayfalanmaz (düz dizi).

---

## 1. Kimlik & hesap

Keşif → bağlan → token akışı. `discover`, `connect`, `connect-manual`, `login` ve OAuth uçları anonimdir (henüz token yok); geri kalanı `Bearer` ister.

### `POST /api/accounts/discover`
**Auth:** yok

Bilinen sağlayıcı listesi, DNS SRV, autoconfig, Microsoft Autodiscover ve son çare olarak sezgisel (`imap.domain` / `mail.domain`) stratejilerini sırayla dener; ilk gerçekten bağlanabilen adayı döner. Şifre burada gönderilmez.

```json
// İstek
{ "email": "person@example.com" }
```

```json
// 200 OK
{
  "discoveryId": "9B58…D9",
  "email": "person@example.com",
  "provider": "Custom",
  "authenticationMethods": ["Password", "AppSpecificPassword"],
  "manualSetupAvailable": true
}
```

| Durum | code | Anlamı |
|---|---|---|
| 200 | — | `discoveryId` 10 dakika geçerli, tek kullanımlık. |
| 422 | `mail_discovery_failed` | Hiçbir strateji bağlanamadı. Gövdede `manualSetupAvailable: true` gelir → connect-manual'a geç. |
| 400 | — | Geçersiz/boş email (ValidationProblem, `errors.email`). |

### `POST /api/accounts/connect`
**Auth:** yok

Keşifle bulunan sunucuya kimlik bilgileriyle bağlanır ve **yeni** bir hesap oluşturur. Email zaten kayıtlıysa reddedilir — var olan hesap için bu uç değil, oturum açmış kullanıcı `/api/account/reconnect` kullanır.

```json
// İstek
{
  "discoveryId": "9B58…D9",
  "authentication": { "type": "Password", "password": "••••••••" },
  "deviceIdentifier": "iphone-15-pro-a1b2"
}
```

```json
// 200 OK — TokenResponse
{
  "accessToken": "eyJhbGciOi…",
  "refreshToken": "duN2HA78M…==",
  "mailAccountId": "22c8e563-…",
  "accessTokenExpiresAt": "2026-09-18T08:14:33Z"
}
```

| Durum | code | Anlamı |
|---|---|---|
| 401 | `mail_authentication_failed` | Sunucu şifreyi reddetti. |
| 409 | `mail_account_already_exists` | Bu email zaten bağlı bir hesaba ait — **hesap oluşturmaz**, bunun yerine `POST /api/accounts/login` kullan (yeni cihazdan giriş). |
| 403 | `email_not_allowlisted` | Prod erişim listesi açıkken ve email listede değilken. Bkz. [Prod erişim listesi](#10-prod-erişim-listesi-allowlist). |
| 403 | `provider_disabled` / `provider_new_accounts_disabled` / `authentication_method_disabled` | Sağlayıcı ya da kimlik doğrulama yöntemi sunucu tarafında kapatılmış (runtime ayarı). "Şu an desteklenmiyor" mesajı göster. |
| 422 | `discovery_expired` | `discoveryId` süresi doldu, keşfi tekrarla. |
| 422 | `unsupported_authentication_method` | Gönderilen `authentication.type` bu sunucu için geçerli değil (`authenticationMethods` listesinden seç). |
| 422 | `mail_server_unsafe` | Keşfedilen sunucu güvenlik kontrolünden (SSRF/TLS) geçemedi. |
| 502 | `mail_tls_failed` / `mail_server_unreachable` | IMAP/SMTP sunucusuna ulaşılamadı / TLS el sıkışması başarısız. |

### `POST /api/accounts/connect-manual`
**Auth:** yok

Keşif başarısız olduğunda son çare. Host/port/şifreleme kullanıcıdan alınır, sunucu tarafında aynı SSRF/TLS/kimlik doğrulama kontrollerinden geçer.

```json
// İstek
{
  "email": "person@example.com",
  "username": "person@example.com",
  "authentication": { "type": "Password", "password": "••••••••" },
  "imap": { "host": "imap.example.com", "port": 993, "security": "SslOnConnect" },
  "smtp": { "host": "smtp.example.com", "port": 587, "security": "StartTls" },
  "displayName": "Kişisel",
  "deviceIdentifier": "iphone-15-pro-a1b2"
}
```

Başarıda `/connect` ile birebir aynı `TokenResponse` ve hata kodları döner; ek olarak `400 manual_setup_invalid` (host/port/alan doğrulaması), `400 invalid_email` ve `401 mail_smtp_authentication_failed` (IMAP tamam, SMTP şifreyi reddetti) görülebilir. `deviceIdentifier` (opsiyonel) cihaz başına sabit bir değer olmalı — `/account/sessions` listesinde bu ad görünür.

> **Önemli:** `security` yalnızca iki değer alır ve porta bağlıdır: `SslOnConnect` IMAP için 993 / SMTP için 465; `StartTls` IMAP için 143 / SMTP için 587. Başka kombinasyon `422 mail_server_unsafe` döner.

### `POST /api/accounts/login`
**Auth:** yok

Zaten kayıtlı bir posta kutusuna **başka bir cihazdan** giriş. `/connect`'in aksine hesap oluşturmaz veya güncellemez — sadece gönderilen şifreyi hesabın kayıtlı sunucu ayarlarına karşı doğrular ve doğruysa o cihaz için yeni, bağımsız bir `MailSession` açar. İlk cihazın oturumu bundan etkilenmez, ikisi de aynı anda geçerli kalır.

```json
// İstek
{
  "email": "person@example.com",
  "password": "••••••••",
  "deviceIdentifier": "android-pixel-8-c3d4"
}
```

```json
// 200 OK — TokenResponse (connect ile aynı şekil)
{
  "accessToken": "eyJhbGciOi…",
  "refreshToken": "duN2HA78M…==",
  "mailAccountId": "22c8e563-…",
  "accessTokenExpiresAt": "2026-09-18T08:14:33Z"
}
```

| Durum | code | Anlamı |
|---|---|---|
| 404 | `mail_account_not_found` | Bu email için kayıtlı hesap yok — kullanıcıyı `/connect` akışına yönlendir. |
| 401 / 422 | `mail_authentication_failed` vb. | Şifre veya sunucu doğrulaması reddedildi; hesap değişmez. |
| 403 | `mail_account_disabled` / `email_not_allowlisted` / `provider_existing_accounts_disabled` | Hesap erişimi kapalı, allowlist dışı ya da sağlayıcıda mevcut hesap girişi kapatılmış. |
| 403 | `provider_disabled` / `authentication_method_disabled` | Sunucu politikası bu girişi kapatmış. |

### OAuth — `POST /api/accounts/oauth/{provider}/start` ve `/complete`
**Auth:** yok

`provider` yalnızca `google` veya `microsoft`. Authorization Code + PKCE akışı; sağlayıcı token'ları hiçbir zaman istemciye dönmez.

```json
// start — istek
{ "email": "person@gmail.com", "deviceIdentifier": "…" }

// start — 200 OK
{ "authorizationUrl": "https://accounts.google.com/…", "state": "opaque-protected-state" }
```

```json
// complete — istek
{ "state": "…", "code": "…" }

// complete — 200 OK: aynı TokenResponse
```

| Durum | code | Anlamı |
|---|---|---|
| 400 | `invalid_email` | Geçersiz email. |
| 403 | `email_not_allowlisted` / `provider_disabled` / `provider_new_accounts_disabled` / `provider_existing_accounts_disabled` / `authentication_method_disabled` | Erişim politikası reddi. |
| 409 | `mail_account_needs_reauthentication` | (`complete`) hesap yeniden yetkilendirme istiyor. |
| 422 | `oauth_provider_not_configured` | `provider` google/microsoft dışı ya da sunucuda client bilgisi yok. |
| 422 | `oauth_redirect_uri_invalid` | (`start`) redirect URI izinli değil. |
| 422 | `oauth_state_invalid` | (`complete`) `state` bozuk/süresi dolmuş — `start`'tan yeniden başla. |
| 422 | `oauth_code_exchange_failed` | Sağlayıcı `code`'u reddetti (tek kullanımlıktır). |

`authorizationUrl`'u sistem tarayıcısında / `flutter_web_auth_2` ile aç; redirect URI uygulamanın deep link'i olmalı. `state`'i saklamana gerek yok, redirect URI zaten üzerinde taşır.

### `POST /api/auth/refresh`
**Auth:** yok (body'de refreshToken)

Refresh token rotasyonludur: her çağrı eskisini geçersiz kılıp yeni bir çift döner. Aynı token iki kez kullanılamaz.

```json
// İstek
{ "refreshToken": "duN2HA78M…==" }
```

Yanıt: `TokenResponse` — `accessToken` + `refreshToken` ikisi de yeni.

| Durum | code | Anlamı |
|---|---|---|
| 401 | `invalid_refresh_token` | Süresi dolmuş, iptal edilmiş, ya da hesap silinmiş/erişimi kesilmiş. **Kullanıcıyı yeniden bağlanma ekranına gönder.** |

### `POST /api/auth/logout`
**Auth:** yok (body'de refreshToken)

Verilen refresh oturumunu iptal eder. `{ "refreshToken": "…" }` gönder, `204` döner. Token bilinmiyorsa/zaten iptalse `401 session_revoked` döner — istemcide bunu **başarı say**, yine de yerel token'ları sil. Zaten verilmiş `accessToken` süresi dolana (≤15 dk) kadar geçerli kalır. Çıkışta FCM cihaz kaydını da sil (`DELETE /api/devices/{id}`).

### `GET /api/account`
**Auth:** Bearer

Oturum açmış hesabın özet bilgisi — uygulama açılışında "kim bağlı" göstermek için. `status` `NeedsReauthentication` ise reconnect ekranını aç.

```json
// 200 OK
{
  "id": "22c8e563-…",
  "emailAddress": "person@example.com",
  "displayName": "",
  "provider": "Custom",
  "status": "Active"
}
```

### `POST /api/account/reconnect`
**Auth:** Bearer

Oturum açmış hesabın şifresi/sunucu ayarları değiştiğinde (ör. şifre sıfırlandı, `mail_account_needs_reauthentication` geldi) kullanılır. `imap`/`smtp` alanları opsiyoneldir — verilmezse mevcut sunucu ayarları korunur, sadece kimlik bilgisi güncellenir.

> Fark: bu uç zaten oturum açmış (`Bearer` token'ı olan) bir cihaz için hesabın kendi ayarlarını değiştirir. Henüz token'ı olmayan **yeni bir cihazdan** var olan hesaba giriş yapmak için `/api/accounts/login` kullan — reconnect anonim değildir, login'in yerini tutmaz.

```json
// İstek
{ "authentication": { "type": "Password", "password": "••••••••" } }
```

Yanıt `200`: `GET /api/account` ile aynı `AccountResponse`. Hatalar: `401 mail_authentication_failed`, `404 mail_account_not_found`, `422 mail_server_unsafe` / `unsupported_authentication_method`.

### `GET /api/account/sessions`
**Auth:** Bearer

Hesaba bağlı, hâlâ geçerli (süresi dolmamış, iptal edilmemiş) tüm cihaz oturumlarını döner — "Bağlı cihazlar" ayarlar ekranı için. Kendi cihazını işaretlemek için istemci, uygulamada sakladığı kendi `deviceIdentifier`'ını dizideki değerlerle karşılaştırsın (sunucu "bu senin cihazın" bilgisini ayrıca işaretlemez).

```json
// 200 OK — dizi elemanı
{
  "id": "3fa85f64-…",
  "deviceIdentifier": "iphone-15-pro-a1b2",
  "createdAt": "2026-08-01T10:00:00Z",
  "lastUsedAt": "2026-09-17T22:14:00Z",
  "expiresAt": "2027-03-01T10:00:00Z"
}
```

### `DELETE /api/account/sessions/{sessionId}`
**Auth:** Bearer

Belirtilen cihazın oturumunu uzaktan kapatır (o cihazın refresh token'ı geçersiz olur, bir sonraki `401`'de yeniden giriş ister). Sadece kendi hesabının oturumları silinebilir; başka hesaba ait bir id `404` döner. `204` başarıda gövdesizdir.

| Durum | code | Anlamı |
|---|---|---|
| 404 | — | Oturum yok, zaten iptal edilmiş, ya da başka hesaba ait. |

### `DELETE /api/account`
**Auth:** Bearer

Hesabı ve tüm önbelleklenmiş mail/eklerini kalıcı siler. `204` döner. **Geri alınamaz** — istemcide mutlaka onay adımı olsun.

---

## 2. Klasörler

Klasör listesi bağlantı anında otomatik keşfedilir; bu uçlar yeniden senkronize etmek için.

### `GET /api/folders`
**Auth:** Bearer

Hesabın tüm klasörlerini döner (dizi, sayfalama yok).

```json
// 200 OK — dizi elemanı
{
  "id": "8f54e583-…",
  "mailAccountId": "22c8e563-…",
  "name": "INBOX",
  "fullName": "INBOX",
  "folderType": "Inbox",
  "uidValidity": 1789380474,
  "isSyncEnabled": true,
  "isAvailable": true,
  "unreadCount": 3,
  "totalCount": 142
}
```

`unreadCount` / `totalCount` silinmiş işaretli (`deleted`) mailler hariç, sunucudaki önbellekten sayılır — drawer rozetleri için sayfalardan hesap yapma, bunları kullan.

`folderType` filtrelemede kullanışlı: `Inbox · Sent · Drafts · Trash · Junk · Archive · Custom`. `isAvailable: false` olan klasörler sunucudan silinmiş demektir, UI'da gizle.

### `POST /api/folders/refresh`
**Auth:** Bearer

Sunucudaki klasör ağacını yeniden keşfeder (yeni/silinen klasörleri yakalar). `202 Accepted` + `{ "folders": 7 }` döner; sonuç asenkron uygulanır, ardından `GET /api/folders` ile tekrar çek.

### `POST /api/folders/{id}/sync`
**Auth:** Bearer

Belirli bir klasörün mail senkronizasyonunu kuyruğa alır (pull-to-refresh için ideal). `202 Accepted`, gövde yok — arka planda otomatik senkron zaten periyodik çalışır, bu sadece anlık tetikler. Bitiş bildirimi yoktur: `202` sonrası listeyi tekrar çek (ya da FCM `new_mail` bekle).

| Durum | code | Anlamı |
|---|---|---|
| 404 | — | Klasör yok / başka hesaba ait. |
| 403 | `mail_account_disabled` | Hesap kapalı. |
| 409 | `mail_folder_unavailable` | Klasör sunucudan silinmiş (`isAvailable: false`). |
| 409 | `mail_account_needs_reauthentication` / `credential_missing` | Kimlik bilgisi geçersiz → reconnect. |
| 503 | `sync_queue_full` | Kuyruk dolu, kısa süre sonra tekrar dene. |

`POST /api/folders/refresh` da aynı `403`/`409` kodlarını verebilir.

---

## 3. Mail — okuma & arama

### `GET /api/mails`
**Auth:** Bearer

Hesap kapsamında liste, en yeni önce (`receivedAt`). Query: `folderId`, `isRead`, `hasAttachments`, `search`, `page`, `pageSize` (üst sınır 100). Liste öğesi `snippet` (gövdenin ilk ~120 karakteri, düz metin), `flagged`, `answered`, `attachmentCount` ve `conversationId` taşır. Veri sunucudaki önbellekten gelir; taze içerik için önce `POST /folders/{id}/sync`.

```json
// 200 OK
{
  "items": [{
    "id": "21d342c3-…", "folderId": "d59de533-…",
    "subject": "Toplantı notları",
    "fromAddress": "sender@example.com", "fromDisplayName": "Gönderen",
    "toAddress": "person@example.com",
    "isRead": true, "hasAttachments": false,
    "receivedAt": "2026-09-17T01:56:58Z",
    "conversationId": "806acf4c-…",
    "snippet": "Merhaba, toplantı notları ekte…",
    "flagged": false, "answered": false, "attachmentCount": 0
  }],
  "page": 1, "pageSize": 20, "total": 142
}
```

### `GET /api/search`
**Auth:** Bearer

Yalnızca sunucudaki önbellekli maillerde tam metin + filtreli arama; tüm filtreler opsiyonel ve AND'lenir. Query: `q`, `folderId`, `conversationId`, `from`, `to`, `fromDate`, `toDate` (ISO-8601), `isRead`, `flagged`, `hasAttachment` (dikkat: `/mails`'te `hasAttachments`), `page`, `pageSize`. Yanıt şekli `/mails` ile birebir aynı (`MailListResponse`).

### `GET /api/mails/{id}`
**Auth:** Bearer

Tam mail içeriği: gövde, katılımcılar, header'lar, ekler.

```json
// 200 OK (kısaltılmış)
{
  "id": "21d342c3-…", "folderId": "…", "accountId": "…", "uid": 8,
  "messageId": "9F1n…@mail.example.com",
  "inReplyToMessageId": "", "references": "",
  "subject": "Toplantı notları",
  "from": [{ "id": "…", "type": "From", "address": "sender@example.com", "displayName": "Gönderen", "sortOrder": 0 }],
  "to": ["…"], "cc": [], "bcc": [], "replyTo": [],
  "bodyText": "…",
  "body": { "html": "…", "hasRemoteContent": false, "remoteContentHosts": [], "trackingPixelHosts": [] },
  "isRead": true, "answered": false, "flagged": false, "draft": false,
  "deleted": false, "recent": false, "hasAttachments": true,
  "sentAt": "…", "receivedAt": "…", "internalDate": "…",
  "conversationId": "806acf4c-…", "isFromMe": false,
  "headers": [{ "name": "X-Mailer", "value": "…" }],
  "attachments": [{
    "id": "…", "fileName": "rapor.pdf", "contentType": "application/pdf",
    "sizeBytes": 48211, "isInline": false, "contentId": "", "contentDisposition": "attachment"
  }]
}
```

`conversationId` her zaman dolu gelir (tek mailse kendi konuşması); `isFromMe` Gönderilmiş/Taslak klasöründeki mailler ya da gönderen hesabın kendi adresi ise `true`. HTML-only maillerde `bodyText` sunucuda HTML'den üretilir (yalnızca yeni senkronlanan mailler için). `body.html` sunucuda üretilen render edilebilir HTML'dir; yalnızca metin gerekirse `bodyText`. `isInline: true` ekler HTML içinde `cid:<contentId>` ile referanslanır — WebView'de bu URL'leri ek indirme ucuyla eşleştirmen gerekir. Bulunamazsa `404 mail_not_found`.

`body.hasRemoteContent` true ise HTML gövdede dış kaynaklı içerik (izleme pikseli olabilir) var — `WebView`'de uzak içerik yüklemeden önce kullanıcıya sor.

### `GET /api/mails/{mailId}/attachments/{attachmentId}`
**Auth:** Bearer

Ham dosya baytlarını döner (`Content-Type` ekin gerçek türü, `Content-Disposition` dosya adını taşır); JSON değildir, `Authorization` header'ıyla akış olarak indir (`Dio` `ResponseType.bytes`). `404` ek hesaba/mail'e ait değilse ya da yoksa.

---

## 4. Mail — durum & taşıma

Tümü gövdesiz `POST`, başarıda `204` döner. Tüm mutasyonlar önce uzak IMAP sunucusuna uygulanır (remote-first) — bu yüzden ağ gecikmesi yaşanabilir, UI'da iyimser güncelleme + geri alma stratejisi öner.

### `POST /api/mails/{id}/{action}`
**Auth:** Bearer

Dokuz sabit eylem adı, hepsi aynı imza: `POST /api/mails/{id}/<action>`, gövde yok.

| action | Anlamı |
|---|---|
| `read` | okundu işaretle |
| `unread` | okunmadı işaretle |
| `star` | yıldızla |
| `unstar` | yıldızı kaldır |
| `trash` | çöpe taşı |
| `restore` | çöpten geri al |
| `archive` | arşivle |
| `spam` | spam işaretle |
| `not-spam` | spam değil → Gelen kutusu |

| Durum | code | Anlamı |
|---|---|---|
| 204 | — | Uygulandı. `restore` yalnızca daha önce `trash`/`spam` ile taşınmış maile çalışır. |
| 404 | `mail_not_found` | Mail hesaba ait değil ya da yok. |
| 404 | `mail_folder_not_found` | Hedef klasör (örn. Trash/Archive/Junk) hesapta yok. |
| 409 | `mail_operation_conflict` | Yerelde tutulan klasör durumu sunucudakiyle uyuşmuyor (UIDVALIDITY çakışması) — o klasörü yeniden senkronize et, tekrar dene. |
| 422 | `mail_operation_not_supported` | Örn. daha önce trash'lenmemiş maile `restore` çağrısı. |
| 502 | `mail_move_failed` | Klasör değiştiren eylemler (`trash`/`restore`/`archive`/`spam`/`not-spam`) sunucu tarafında taşınamadı. |
| 502 | `mail_provider_unavailable` | IMAP sunucusuna ulaşılamadı — geçici, kullanıcıya "tekrar dene" göster. |

> Eski `PATCH /api/mails/{id}/read` (gövde: `{ "isRead": true }`) hâlâ çalışır ama yeni entegrasyonlar `POST …/read` / `…/unread` kullanmalı. **Dikkat:** bu eski uç 409 çakışmasında farklı bir kod döner — `mailbox_changed` (yukarıdaki `mail_operation_conflict` değil). Yeni entegrasyonlar bu eski uca hiç dokunmayacaksa bu ayrımı görmez.

### `POST /api/mails/{id}/move` ve `POST /api/mails/{id}/copy`
**Auth:** Bearer

Belirli bir hedef klasöre taşı/kopyala (özel klasör hiyerarşisinde, örn. kullanıcı tanımlı klasörler).

```json
// İstek
{ "folderId": "9be00940-…" }
```

Aynı hata kodu seti yukarıdaki eylem tablosuyla birebir aynıdır (`mail_not_found`, `mail_folder_not_found`, `mail_operation_conflict`, `mail_move_failed`, `mail_provider_unavailable`) — `copy` yalnızca `mail_operation_conflict`/`mail_provider_unavailable` döndürebilir, klasör değiştirmediği için `mail_move_failed` onu ilgilendirmez.

### `POST /api/mails/bulk/{action}`
**Auth:** Bearer

Aynı işlemi birden çok maile tek istekte uygular. `action`: `read`, `unread`, `star`, `unstar`, `archive`, `trash`, `restore`, `spam`, `not-spam`, `move` (`move` için gövdede `folderId` zorunlu). Her mail **birbirinden bağımsız** işlenir — biri hata verse (çakışma, bulunamama) bile diğerleri uygulanmaya devam eder; sonucu her zaman `200 OK` ile item bazında oku.

```json
// İstek
{ "mailIds": ["21d342c3-…", "9c2f1a04-…"], "folderId": null }
```

```json
// 200 OK
{
  "results": [
    { "mailId": "21d342c3-…", "success": true, "code": null },
    { "mailId": "9c2f1a04-…", "success": false, "code": "mail_not_found" }
  ]
}
```

`code` alanı başarısız item'larda yukarıdaki tekil eylem hata kodlarından biridir (`mail_not_found`, `mail_folder_not_found`, `mail_operation_conflict`, `mail_move_failed`, `mail_provider_unavailable`, `mail_account_needs_reauthentication`); başarılıysa `null`.

| Durum | code | Anlamı |
|---|---|---|
| 200 | — | İstek kabul edildi, her item'ın kendi sonucu `results` içinde. |
| 400 | — | `mailIds` boş, 100'den fazla eleman içeriyor, ya da `action: move` iken `folderId` eksik (ValidationProblem). |
| 404 | — | `action` bilinmeyen bir değer (yukarıdaki 5 değerin dışında). |

> `mailIds` başına en fazla **100** eleman kabul edilir. `Idempotency-Key` bu uçta **gerekli değildir** — sadece gönderim uçlarında zorunlu (bkz. [Hızlı başlangıç, kural 2](#bilmen-gereken-dört-kural)).

---

## 5. Yazma, taslak & gönderme

### `GET /api/mails/{id}/compose/{reply\|reply-all\|forward}`
**Auth:** Bearer

Bir maile yanıt/ilet ekranını önceden doldurmak için gereken alanları döner — `In-Reply-To`/`References` zincirini istemci hesaplamaz. Gönderirken yalnızca `replySourceMailId` (form alanı) ver; sunucu header'ları kendisi ekler. `forward`'da `attachments` kaynak mailin ekleridir (yeniden yüklemen gerekir). Bulunamazsa `404 mail_not_found`. Alıntı gövdesini (`originalFrom/Date/Subject`'ten) istemci oluşturur.

```json
// 200 OK
{
  "sourceMailId": "…",
  "to": [{ "address": "sender@example.com", "displayName": "" }],
  "cc": [],
  "suggestedSubject": "Re: Toplantı notları",
  "inReplyToMessageId": "9F1n…@mail.example.com",
  "references": "9F1n…@mail.example.com",
  "originalFrom": "sender@example.com", "originalDate": "…", "originalSubject": "Toplantı notları",
  "attachments": []
}
```

### `POST /api/drafts`
**Auth:** Bearer · **Gövde:** `multipart/form-data`

Yeni taslak oluşturur (sunucu tarafında IMAP `APPEND` ile). Alanlar form-data: `To` (çoklu), `Cc`, `Bcc`, `subject`, `bodyHtml` ve/veya `bodyText`, `replySourceMailId` (opsiyonel), dosya alanları ek olarak eklenir (alan adı serbest, her dosya bir ek). Form doğrulama hataları gönderimdeki kodlarla aynıdır. `reconciliationPending: true` ise `mailId` henüz sunucuyla eşleşmemiştir; kısa süre sonra taslak listesini/`GET /drafts/{id}`'yi tazele. `warning` doluysa kullanıcıya göster.

| Durum | code | Anlamı |
|---|---|---|
| 404 | `mail_account_not_found` | Hesap yok. |
| 422 | `drafts_folder_unavailable` | Hesapta Drafts klasörü yok/kullanılamıyor. |

```json
// 200 OK
{ "created": true, "mailId": "4cae24be-…", "reconciliationPending": false, "warning": null }
```

### `GET /api/drafts/{id}`
**Auth:** Bearer

Yanıt şekli `GET /api/mails/{id}` ile aynıdır (`MailDetailResponse`).

| Durum | code | Anlamı |
|---|---|---|
| 404 | — | Taslak yok / başka hesaba ait. |
| 422 | `mail_not_draft` | Bu id artık taslak değil (gönderildi/silindi) — bkz. not aşağıda. |

### `PUT /api/drafts/{id}`
**Auth:** Bearer · **Gövde:** `multipart/form-data`

IMAP taslaklar yerinde düzenlenemez: sunucu eski mesajı siler, yenisini `APPEND` eder. Yanıt `POST /drafts` ile aynı şekil (`{ created: false, mailId, … }`) ama **yeni** bir `mailId` döner.

`PUT` hataları: `404 draft_not_found`, `422 mail_not_draft` / `drafts_folder_unavailable`.

> **Kritik:** güncellemeden dönen yeni `mailId`'yi state'te değiştir. Eski id artık geçersizdir — ona `GET` atarsan `422 mail_not_draft` alırsın.

### `DELETE /api/drafts/{id}`
**Auth:** Bearer

Taslağı siler, `204` döner. Hatalar: `404 draft_not_found`, `422 trash_folder_unavailable` / `mail_not_draft`, `502 draft_delete_failed`.

### `POST /api/drafts/{id}/send`
**Auth:** Bearer

Taslağı olduğu gibi gönderir, gövde yok. `Idempotency-Key` header'ı **zorunlu** (bkz. [Hızlı başlangıç, kural 2](#bilmen-gereken-dört-kural)). Hatalar `/mails/send` tablosuyla aynıdır (+ `404 draft_not_found`, `422 mail_not_draft`). `draftRemoved: false` ise mail gitmiştir ama taslak silinememiştir — kullanıcıya "gönderildi" göster, hata gösterme.

```json
// 200 OK
{ "sent": true, "sentCopySaved": true, "draftRemoved": true, "warning": null,
  "mailId": "…", "conversationId": "…" }
```

`mailId` / `conversationId` gönderilen mailin Gönderilmiş klasöründeki kaydıdır; `sentCopySaved: false` iken ya da kayıt hemen bulunamazsa `null` gelir.

### `POST /api/mails/send`
**Auth:** Bearer · **Gövde:** `multipart/form-data`

Taslaksız doğrudan gönderim. Form alanları: `To` (çoklu, en az bir tane), `Cc`, `Bcc`, `subject`, `bodyHtml` ve/veya `bodyText`, en fazla 20 dosya eki, `replySourceMailId` (yanıtlarken). `Idempotency-Key` header'ı zorunlu.

```json
// 200 OK
{ "sent": true, "sentCopySaved": true, "warning": null,
  "mailId": "…", "conversationId": "…" }
```

Kopya kaydedildiyse sunucu Gönderilmiş klasörünü hemen senkronlar; `mailId` / `conversationId` yanıtla birlikte gelir ve mail `GET /api/conversations/{id}` içinde hemen görünür. Yerel geçici kopyayı bu id ile değiştir. `sentCopySaved: false` iken ya da aynı `Idempotency-Key` ile tekrarlanan çağrıda (kayıtlı sonuç) ikisi `null` döner.

`sent: true` ama `sentCopySaved: false` → mail gitti, Gönderilmiş klasörüne kopya yazılamadı (`warning` dolu); başarı say, uyarıyı göster.

| Durum | code | Anlamı |
|---|---|---|
| 400 | `idempotency_key_required` | Header eksik ya da >200 karakter. |
| 400 | `recipient_required` / `body_required` / `body_too_large` / `invalid_recipient` / `invalid_mail_header` / `message_not_constructible` | Form doğrulaması (alıcı yok, gövde boş/çok büyük, adres ya da konu başlık enjeksiyonu içeriyor…). |
| 400 | `attachment_too_large` / `too_many_attachments` | Sunucu limitleri (varsayılan tekil ek 25 MB, mail toplamı 50 MB; en fazla 20 ek — sunucuda runtime'da değişebilir, istemcide sabit kodlama, ekleri seçerken önden 25 MB kontrolü yap). |
| 409 | `idempotency_conflict` | Aynı key farklı bir gövdeyle tekrar gönderildi — aynı key'i yeni bir gönderimde kullanma. |
| 401 | `mail_smtp_authentication_failed` | SMTP şifreyi reddetti → reconnect. |
| 409 | `send_in_progress` | Aynı key ile gönderim hâlâ sürüyor; kısa süre sonra tekrar dene ya da bekle. |
| 409 | `delivery_unknown` | SMTP oturumu sonucu belirsiz kaldı — mail gitmiş olabilir. **Otomatik retry yapma**; kullanıcıya "Gönderilenler'i kontrol et" de, gerekirse yeni key ile elle tekrar göndermesine izin ver. |
| 409 | `mail_account_needs_reauthentication` | Kimlik bilgisi geçersiz → reconnect. |
| 502 | `mail_server_unreachable` / `mail_tls_failed` | SMTP'ye ulaşılamadı; geçici hata olarak ele al. |

> Aynı `Idempotency-Key` ile tekrar çağrı, aynı gövdeyle yapılırsa gönderim tekrarlanmaz — kayıtlı sonuç aynen döner. Ağ zaman aşımı sonrası güvenle retry atabilirsin.

---

## 6. Konuşmalar

Mailleri `Message-ID` / `In-Reply-To` / `References` zincirine göre gruplar; zincir yoksa normalize edilmiş subject + ortak katılımcı yedeği kullanılır (`Re:`, `Fwd:`, `Ynt:`, `İlt:`, `AW:`, `WG:` vb. önekler temizlenir).

### `GET /api/conversations`
**Auth:** Bearer

Query: `page`, `pageSize` (üst sınır 100).

```json
// 200 OK
{
  "items": [{
    "id": "806acf4c-…", "subject": "Toplantı notları",
    "participants": ["sender@example.com"],
    "messageCount": 3, "unreadCount": 1, "hasAttachments": false,
    "startedAt": "…", "lastMessageAt": "…"
  }], "page": 1, "pageSize": 50, "total": 17
}
```

### `GET /api/conversations/{id}`
**Auth:** Bearer

Query: `includeTrash=false` Çöp ve Spam klasörlerindeki mesajları dışarıda bırakır (varsayılan: tüm klasörler). `include=body` her mesaja `bodyText` ve `body` (`GET /api/mails/{id}` ile aynı şekil) ekler — thread'i N istek yerine tek istekte çekmek için.

```json
// 200 OK
{
  "id": "…", "subject": "…",
  "messages": [{
    "id": "…", "folderId": "…", "subject": "…",
    "fromAddress": "…", "fromDisplayName": "…",
    "sentAt": "…", "receivedAt": "…",
    "isRead": true, "hasAttachments": false,
    "isFromMe": false
  }]
}
```

---

## 7. Cihaz & push bildirimleri

Firebase Cloud Messaging üzerinden çalışır. Uygulama açılışında ve token yenilendiğinde `POST /api/devices` çağır.

### `POST /api/devices`
**Auth:** Bearer

Aynı `token` tekrar gönderilirse güncellenir (upsert) — her uygulama açılışında güvenle çağırabilirsin.

```json
// İstek
{
  "token": "fcm-device-token",
  "platform": "android",
  "appVersion": "1.4.0",
  "locale": "tr-TR"
}
```

```json
// 201 Created
{
  "id": "…", "platform": "android",
  "appVersion": "1.4.0", "locale": "tr-TR",
  "registeredAt": "…", "lastSeenAt": null
}
```

### `DELETE /api/devices/{id}`
**Auth:** Bearer

Çıkış yaparken/bildirimleri kapatırken çağır. `204`.

### Push (FCM) veri şeması

`firebase_messaging` ile alınan bildirimin `data` alanı — mail içeriği/önizleme asla eklenmez, sadece sonraki API çağrısı için gerekli kimlikler taşınır.

```json
{
  "type": "new_mail",  // | "mail_state_changed" | "account_reauthentication_required" | "sync_error"
  "accountId": "…",
  "mailId": "…",          // varsa
  "conversationId": "…",  // varsa
  "folderId": "…",        // varsa
  "operation": "trash"    // mail_state_changed için: read/star/trash/move/…
}
```

> Bildirim geldiğinde gövdeyi bildirimden okuma — `mailId` ile `GET /api/mails/{id}` çağırıp güncel/doğrulanmış veriyi çek.

---

## 8. Hata kodları

Her hata gövdesi `{ code, title, status, correlationId }` — bazılarında ek alanlar (`manualSetupAvailable` gibi) olur. Destek talebinde her zaman `correlationId`'yi ilet.

| HTTP | code | Ne zaman |
|---|---|---|
| 401 | `mail_authentication_failed` | Sunucu şifre/oturum reddetti. |
| 401 | `invalid_refresh_token` | Refresh token geçersiz/kullanılmış/hesap devre dışı — yeniden bağlan. |
| 401 | `mail_smtp_authentication_failed` | SMTP şifre reddi (gönderim/connect-manual). |
| 401 | `session_revoked` | Yalnızca `/auth/logout`: oturum zaten iptal — başarı say. |
| 401 | `management_unauthorized` | Yalnızca yönetim uçları; istemci tarafını ilgilendirmez. |
| 400 | `invalid_email` / `invalid_recipient` / `recipient_required` / `body_required` / `body_too_large` / `too_many_attachments` / `attachment_too_large` / `invalid_mail_header` / `message_not_constructible` / `manual_setup_invalid` | Form/gövde doğrulaması. |
| 400 | `idempotency_key_required` / `idempotency_key_too_long` | Gönderim uçları. |
| 403 | `email_not_allowlisted` | Prod erişim listesi açık, email listede değil — bkz. [altta](#10-prod-erişim-listesi-allowlist). |
| 403 | `mail_account_disabled` | Hesap devre dışı bırakıldı. |
| 403 | `provider_disabled` / `provider_new_accounts_disabled` / `provider_existing_accounts_disabled` / `authentication_method_disabled` | Sunucu tarafı politika: sağlayıcı/yöntem kapalı ("şu an desteklenmiyor"). |
| 404 | `mail_account_not_found` / `mail_not_found` / `draft_not_found` | Kaynak yok ya da başka hesaba ait. Kodsuz `404`: oturum/klasör/ek/konuşma/cihaz/bilinmeyen bulk eylemi. |
| 404 | `mail_folder_not_found` | Mail durum/taşıma uçlarında hedef klasör hesapta yok. |
| 422 | `mail_discovery_failed` | Otomatik keşif başarısız → manuel bağlantıya geç. |
| 422 | `mail_server_unsafe` / `unsupported_authentication_method` / `discovery_invalid` / `discovery_expired` | Sunucu/keşif/yöntem reddi. |
| 422 | `oauth_provider_not_configured` / `oauth_redirect_uri_invalid` / `oauth_state_invalid` / `oauth_code_exchange_failed` | OAuth akışı hataları (bkz. OAuth bölümü). |
| 422 | `drafts_folder_unavailable` / `trash_folder_unavailable` | Hesapta gerekli özel klasör yok. |
| 422 | `mail_not_draft` | Taslak id'si artık geçerli değil (gönderildi/güncellendi/silindi). |
| 422 | `mail_operation_not_supported` | Mail durum/taşıma uçlarında desteklenmeyen işlem (örn. trash'lenmemiş maile `restore`). |
| 409 | `mail_account_already_exists` | Email zaten bağlı. |
| 409 | `mail_operation_conflict` | Modern `POST /api/mails/{id}/{action}` (ve `/move`, `/copy`) uçlarında klasör durumu değişti, yeniden senkronize et. |
| 409 | `mailbox_changed` | Yalnızca eski `PATCH /api/mails/{id}/read` uçlarına özgü — aynı anlam, farklı kod. |
| 409 | `idempotency_conflict` / `send_in_progress` | Aynı gönderim anahtarıyla çakışma. |
| 409 | `delivery_unknown` | Gönderim sonucu belirsiz — otomatik retry yapma. |
| 409 | `mail_account_needs_reauthentication` / `credential_missing` | Saklı kimlik bilgisi geçersiz/yok → `reconnect` (OAuth ise OAuth'u yeniden çalıştır). |
| 409 | `mail_folder_unavailable` | Klasör sunucudan silinmiş. |
| 429 | — | Hız sınırı aşıldı (dk. başına 60 istek); gövde yok. |
| 502 | `mail_move_failed` | Klasör değiştiren mail işlemi (trash/restore/archive/spam/not-spam/move) sunucu tarafında başarısız. |
| 502 | `mail_provider_unavailable` / `mail_tls_failed` / `mail_server_unreachable` | IMAP/SMTP sunucusuna ulaşılamadı — geçici hata olarak ele al, tekrar dene. |
| 502 | `draft_delete_failed` | Taslak sunucudan silinemedi; tekrar dene. |
| 503 | `sync_queue_full` / `oauth_refresh_lock_unavailable` | Geçici yoğunluk; kısa backoff ile tekrar dene. |
| 500 | `mail_operation_failed` | Bilinmeyen mail işlemi hatası. |
| 500 | `unexpected_error` | Beklenmeyen sunucu hatası; `correlationId`'yi logla. |

---

## 9. Enum referansı

Tüm enum değerleri JSON'da **string** olarak serileşir (sayısal değil).

| Enum | Değerler |
|---|---|
| `MailProvider` | `Custom · Google · Microsoft · ICloud · Yahoo` |
| `MailSecurity` | `SslOnConnect · StartTls` |
| `AuthenticationMethod` | `Password · AppSpecificPassword · OAuth2` |
| `MailFolderType` | `Inbox · Sent · Drafts · Trash · Junk · Archive · Custom · Unknown` |
| `MailAccountStatus` | `Active · NeedsReauthentication · ConnectionError · Disabled` |

> `MailAccountStatus.NeedsReauthentication` gördüğünde kullanıcıyı `/api/account/reconnect` ekranına yönlendir; `Disabled` gördüğünde tüm istekler `401`/`403` döner, uygulama çıkışı yaptır.

---

## 10. Prod erişim listesi (allowlist)

Production ortamında, belirlenmiş bir email listesi dışındaki kullanıcılar uygulamayı kullanamayacak şekilde kapatılıp açılabilen bir özellik var. Bu, istemci davranışını iki noktada etkiler.

**Yeni bağlantı reddi.** `discover`/`connect`/`connect-manual`/OAuth uçlarından `403 email_not_allowlisted` gelebilir. Bağlanma ekranında bu kodu, genel hata mesajından ayrı, net bir "bu hesap henüz erişime açılmadı" mesajıyla göster.

**Erişimin anında kesilmesi.** Zaten bağlı bir hesap listeden çıkarılırsa, o an açık bir oturum olsa bile ilk sıradaki istekte `401` alır ve `/api/auth/refresh` de `invalid_refresh_token` ile reddeder. Bu durumda yakalanan 401'i normal "token süresi doldu" akışından ayırman gerekmez — ikisi de aynı şekilde "yeniden bağlan" ekranına düşer.

> Bu özellik varsayılan **kapalı** ve test/geliştirme ortamlarında devre dışıdır — yerelde bu davranışı görmeyeceksin. Prod'da açık olup olmadığını ve listeyi biz (backend) yönetiyoruz; istemci tarafında yalnızca 403/401 durumlarını doğru mesajla karşılaman yeterli.

---

*Mail Client API — v2 · Flutter entegrasyon rehberi*
