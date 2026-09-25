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
| GET | `/api/account/sync-status` | bearer | Klasör başına sync/backfill durumu (son başarılı sync, son hata, backfill ilerlemesi) |
| GET / PUT | `/api/account/sync-scope` | bearer | Hesabın arka planda senkronize edilen klasör kapsamını okur veya günceller (InboxAndSent, AllFolders, SelectedFolders) |
| GET / PUT | `/api/account/notification-settings` | bearer | Hesabın posta bildirim tercihlerini okur veya günceller (açık/kapalı, yalnız Gelen Kutusu, gizlilik) |

`/api/account/sync-scope` yanıtı `{scope, syncedFolderIds}`. PUT
`{scope:"SelectedFolders",folderIds:[...]}` bu hesaba ait kullanılabilir
klasörlerden en az birini seçer; diğer kapsamlarda `folderIds` verilmez.
Başka hesaba ait veya geçersiz klasör id'si ayarı değiştirmeden doğrulama
hatası döner. Yeni klasörler `AllFolders` kapsamına (varsayılan modda
Gelen/Gönderilen'e) dahil edilir; elle klasör sync'i kapsamdan bağımsızdır.

`/api/account/notification-settings` yanıtı
`{enabled, inboxOnly, privacy, previewsAllowedByServer}`; PUT gövdesi
`{enabled, inboxOnly, privacy}`. Ayarlar posta kutusunda oturum açık tüm
cihazlarda geçerlidir ve cihaz kaydını değiştirmez. `privacy`: `Full`
(gönderen, konu, kısa metin önizlemesi), `Limited` (gönderen ve konu;
varsayılan) veya `Private` (yalnız genel metin). Sunucu yöneticisi posta
önizlemelerini kapattıysa `previewsAllowedByServer` false olur ve push'lar
`Private` gönderilir. `inboxOnly: false` Gönderilmiş, Taslaklar, Çöp ve Spam
dışındaki tüm senkronize klasörlerdeki yeni postayı bildirir. `enabled: false`
yalnız yeni posta ve erteleme bitişi push'larını durdurur; hesap uyarıları
(yeniden kimlik doğrulama) yine gönderilir.

Yeni posta push'u sunucu kuralları çalıştıktan sonra gönderilir; kuralın
bildirilen klasörlerden çıkardığı veya okundu yaptığı posta bildirilmez.
`new_mail` ve `snooze_expired` Android'de yalnız veri olarak gelir (uygulama
hızlı eylemlerle gösterir), iOS'ta APNs uyarısı taşır. Süresi dolan
ertelemeler sunucuda 30 saniyede bir sonlandırılır; her biri bir kez uyanır,
sahiplenilmeden önce iptal edilen veya ileri alınan erteleme hiç uyanmaz.

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
| GET | `/api/folders` | bearer | Önbellekteki klasörleri listeler (`delimiter` ve `parentId` ile) |
| POST | `/api/folders` | bearer | Sunucuda klasör oluşturur (`name`, isteğe bağlı `parentId`) |
| PATCH | `/api/folders/{id}` | bearer | Özel klasörü sunucuda yeniden adlandırır; id'ler korunur |
| DELETE | `/api/folders/{id}` | bearer | Alt klasörü olmayan boş özel klasörü siler |
| POST | `/api/folders/refresh` | bearer | Klasör listesini sunucudan yeniden okur |
| POST | `/api/folders/{id}/sync` | bearer | Klasör sync'ini kuyruğa alır (202) |

### Rules

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| GET | `/api/rules` | bearer | Kuralları değerlendirme sırasıyla listeler (`priority`, sonra oluşturulma zamanı) |
| POST | `/api/rules` | bearer | Kural oluşturur (201); `legacyId` ile tekrar çağrı taşınmış kuralı döner (200) |
| PUT | `/api/rules/{id}` | bearer | Kuralı tümüyle günceller |
| DELETE | `/api/rules/{id}` | bearer | Kuralı siler (204) |

Gövde: `{name, enabled, priority, logic:"And"|"Or", conditions:[{type,value}], actions:[{type,folderId?,labelId?}], legacyId?}`.
Koşullar: `senderContains`, `senderEquals`, `senderDomain`, `subjectContains`,
`recipientContains` (To/Cc/Bcc), `hasAttachment` (değersiz), `folder` (klasör id).
İşlemler: `markRead`, `markUnread`, `star`, `archive`, `move` (`folderId`),
`trash`, `spam`, `addLabel` (`labelId`), `stopProcessing`. Klasör ve etiket id'leri
oturumdaki hesaba ait olmalıdır. 1-16 koşul ve işlem; öncelik 0-99999.
Kurallar yeni mail kaydedilip konuşmaya bağlandıktan sonra sunucuda çalışır;
uygulama kapalı olsa da uygulanır, geriye dönük içe aktarılan eski mailler işlenmez.
`folder` koşulu olmayan kural yalnız Gelen Kutusu'na uygulanır. Mail işlemleri
remote-first işlem servisini kullanır; işlemler sırayla çalışır, `stopProcessing`
sonraki kuralları atlar. Geçici hata maili sonraki sync için bekletir; bir mailin
hatası diğerlerini engellemez.

### Templates

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| GET | `/api/templates` | bearer | Yazma şablonlarını ada göre sıralı listeler |
| POST | `/api/templates` | bearer | Şablon oluşturur (201) |
| PUT | `/api/templates/{id}` | bearer | Şablonu tümüyle günceller |
| DELETE | `/api/templates/{id}` | bearer | Şablonu siler (204) |

Gövde: `{name, subject?, bodyText?, bodyHtml?}`; yanıta `id`, `createdAt`, `updatedAt` eklenir.
`name` kırpılır, 1-100 karakterdir ve hesap içinde büyük/küçük harf duyarsız
benzersizdir (aksi halde 409 `template_name_taken`). `subject` kırpılır, tek satır ve
en fazla 500 karakterdir (boş olabilir). `bodyText`/`bodyHtml`'den en az biri dolu
olmalı; her biri gönderimdeki sınırla (`MaxSendBodyChars`) sınırlıdır. Doğrulama
hataları `name`, `subject` veya `body` anahtarlı 400 validation problem döner. Başka
hesabın şablon id'si 404 döner. Şablonlar hesapla birlikte silinir.

### Mail

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| GET | `/api/mails` | bearer | Posta listesi (`folderId`, `isRead`, `hasAttachments`, `search`, `page`, `pageSize` ≤ 100) |
| GET | `/api/mails/{id}` | bearer | Posta detayı (`isFromMe`: Sent/Drafts postası ya da gönderen hesabın adresiyle büyük/küçük harf duyarsız aynıysa, klasörden bağımsız) |
| GET | `/api/search` | bearer | Önbellekteki postada arama (tüm filtreler opsiyonel, AND; aşağıya bakın) |
| GET | `/api/search/remote` | bearer | Kullanıcının başlattığı genel IMAP araması; eksik eşleşmeleri içeri alır, ardından `/api/search` tekrar çağrılır |
| GET | `/api/mails/{mailId}/attachments/{attachmentId}` | bearer | Eki indirir (depolama aranabilirse Range/206) |
| GET | `/api/compose/limits` | bearer | Güncel ek boyutu ve sayı sınırları |
| POST | `/api/mails/send` | bearer + `Idempotency-Key` | Posta gönderir (multipart/form-data) |
| GET | `/api/mails/{id}/compose/reply · reply-all · forward` | bearer | Hazır doldurulmuş yazma bağlamı |

`/api/search` filtreleri: `folderId`, `conversationId`, `isRead`, `flagged`,
`hasAttachment`, `labelId` tam eşleşir; `from` = gönderen adresi veya görünen
adında büyük/küçük harf duyarsız "içerir"; `to` = herhangi bir To/Cc/Bcc
adresi veya adında büyük/küçük harf duyarsız "içerir"; `fromDate`/`toDate`
alınma zamanına göre filtreler, `fromDate` dahil, `toDate` hariç; UTC ofseti
olmayan değerler UTC kabul edilir. `labelId` yalnızca oturum açan hesabın
sahip olduğu etiketlerle eşleşir.

`/api/search/remote` sayfalama hariç aynı arama filtrelerini kabul eder;
`q`, `from`, `to` alanlarından en az biri dolu olmalıdır. Yanıt
`{matched, imported, remaining, complete}` henüz indekslenmemiş eşleşmeleri
ve kapsamın tamamen taranıp taranmadığını bildirir. İstek başına en fazla
25 mail, 20 saniyelik süre sınırı; `complete: false` için tekrar deneyin.
`hasAttachment` IMAP yerine sonraki önbellek aramasında uygulanır.
Başka hesaba ait klasör id'si 404 döner.

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
| POST | `/api/mails/{id}/{op}` | bearer | `op`: read, unread, star, unstar, trash, restore, archive, spam, not-spam, delete (204, gövdesiz). `delete` maili kalıcı olarak siler ve yalnızca Trash/Junk'ta izinlidir (aksi halde 422 `mail_operation_not_supported`; sunucu tarafı hata 502 `mail_delete_failed`) |
| POST | `/api/mails/{id}/move · copy` | bearer | Klasöre taşır/kopyalar (body) |
| POST | `/api/mails/bulk/{action}` | bearer | En fazla 100 `mailIds` üzerinde toplu işlem; `read`, `unread`, `star`, `unstar`, `archive`, `trash`, `restore`, `spam`, `not-spam`, `delete`, `move` (`move` için `folderId` zorunlu) |

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
