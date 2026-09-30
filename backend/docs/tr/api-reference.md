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
| GET | `/api/account` | bearer | Geçerli hesap (`signature` = varsayılan yeni-posta imzasının düz metni, yoksa null) |
| PUT | `/api/account/signature` | bearer | Eski uyumluluk: varsayılan yeni-posta imzasını ayarlar veya temizler (boş metin temizler); gövde `{signature}` |
| POST | `/api/account/reconnect` | bearer | Saklanan kimlik bilgilerini günceller (ör. şifre değişince) |
| DELETE | `/api/account` | bearer | Hesabı ve verilerini siler |
| GET | `/api/account/sessions` | bearer | Oturum açmış cihazları/oturumları listeler |
| DELETE | `/api/account/sessions/{sessionId}` | bearer | Bir oturumu uzaktan kapatır |
| GET | `/api/account/sync-status` | bearer | Klasör başına sync/backfill durumu (son başarılı sync, son hata, backfill ilerlemesi) |
| GET | `/api/account/quota` | bearer | IMAP QUOTA ile INBOX depolama kotası: `{available, usedBytes, limitBytes}` bayt cinsinden (sunucunun KiB değeri × 1024); sunucu QUOTA desteklemiyor, reddediyor veya hesap bağlanamıyorsa `available: false` ve boyutlar null |
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
Yeni posta push'u senkronizasyondan sonra gönderilir.
`new_mail` ve `snooze_expired` Android'de yalnız veri olarak
gelir (uygulama hızlı eylemlerle gösterir), iOS'ta APNs uyarısı taşır. Süresi
dolan ertelemeler sunucuda 30 saniyede bir sonlandırılır; her biri bir kez
uyanır, sahiplenilmeden önce iptal edilen veya ileri alınan erteleme hiç
uyanmaz.

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
| GET | `/api/folders` | bearer | Önbellekteki klasörleri `delimiter`, geçerli `parentId` ve yerel yedek hiyerarşiyi gösteren `isLocalParentOverride` ile listeler. `fullName` her zaman gerçek IMAP adıdır. |
| POST | `/api/folders` | bearer | Sunucuda klasör oluşturur (`name`, isteğe bağlı `parentId`); sunucu alt klasör oluşturmayı reddederse kökte oluşturup ancak başarılı olduğunda yerel üst klasöre bağlar |
| PATCH | `/api/folders/{id}` | bearer | Özel klasörü sunucuda yeniden adlandırır; id'ler korunur |
| PUT | `/api/folders/{id}/parent` | bearer | `Custom` klasörü `{"parentId":"<guid>"}` ile aynı hesaptaki kullanılabilir standart/özel klasörün altına, `{"parentId":null}` ile köke taşır. Önce IMAP RENAME dener; sunucu reddederse gerçek uzaktaki klasör ve mailler olduğu yerde kalır, yerel üst klasör saklanır (`isLocalParentOverride: true`). Refresh bu seçimi korur; kopyalama/silme yapılmaz. Döngü 409 `mail_folder_cycle`, ad çakışması 409 `mail_folder_exists`, bulunmayan/başka hesaptaki üst klasör 404 `mail_folder_not_found`, standart klasörü taşıma 422 `mail_folder_protected` döner. PATCH yalnızca yeniden adlandırır. |
| DELETE | `/api/folders/{id}` | bearer | Alt klasörü olmayan boş özel klasörü siler |
| PUT | `/api/folders/{id}/role` | bearer | Klasör rolünü elle atar: `{"role":"Sent"\|"Drafts"\|"Trash"\|"Junk"}`, otomatik tespite dönmek için `{"role":null}`. Aynı hesapta aynı rolü taşıyan diğer atamayı kaldırır; `folderType` etkin rolü döner ve refresh sonrası korunur. `400 invalid_folder_role`, başka hesabın klasörü için `404` |
| POST | `/api/folders/refresh` | bearer | Klasör listesini sunucudan yeniden okur |
| POST | `/api/folders/{id}/sync` | bearer | Klasör sync'ini kuyruğa alır (202) |


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

### Snippets

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| GET | `/api/snippets` | bearer | Hazır metinleri gösterim sırasıyla listeler (`sortOrder`, sonra oluşturulma zamanı) |
| POST | `/api/snippets` | bearer | Hazır metin oluşturur (201); `sortOrder` yoksa sona eklenir |
| PUT | `/api/snippets/{id}` | bearer | Hazır metni tümüyle günceller; `sortOrder` yoksa sırası korunur |
| DELETE | `/api/snippets/{id}` | bearer | Hazır metni siler (204) |

Gövde: `{title?, text, sortOrder?}`; yanıta `id`, `createdAt`, `updatedAt` eklenir.
Hazır metinler konusuz, kısa ve tekrar kullanılabilir gövde metinleridir. `text`
kırpılır ve 1-2000 karakterdir; `title` opsiyonel, kırpılır ve en fazla 100
karakterdir; `sortOrder` 0-99999. Doğrulama hataları `text`, `title` veya
`sortOrder` anahtarlı 400 validation problem döner. Başka hesabın hazır metin id'si
404 döner. Hazır metinler hesapla birlikte silinir.

### Güvenilir göndericiler

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| GET | `/api/trusted-senders` | bearer | Kayıtlı gönderici/alan adı görsel tercihlerini listeler |
| POST | `/api/trusted-senders` | bearer | Gönderici/alan adı tercihini kaydeder (201; varsa 200) |
| DELETE | `/api/trusted-senders/{id}` | bearer | Kayıtlı tercihi siler (204) |

Gövde: `{kind: "Sender" | "Domain", value}`; yanıta `id`, `createdAt` eklenir.
`value` kırpılır ve küçük harfe çevrilir; `Domain` değeri `@` ile başlayabilir.
Geçersiz adres veya alan adı `value` anahtarlı 400 validation problem döner.
Normal uzak görseller güvenilir gönderici kaydı olmadan da varsayılan olarak
yüklenir. Junk klasöründeki ve `Authentication-Results` başlığı `dmarc=fail`
bildiren postalar varsayılan olarak engellidir; `remoteContent=allow` yalnız o
posta için kullanıcı iznidir. Temizleme kuralları değişmez. Başka hesabın
kaydı 404 döner. Kayıtlar hesapla birlikte silinir.

### Mail

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| GET | `/api/mails` | bearer | Posta listesi (`folderId`, `isRead`, `hasAttachments`, `search`, `page`, `pageSize` ≤ 100) |
| GET | `/api/mails/{id}` | bearer | Posta detayı (`remoteContent=allow` yalnızca bu yanıtta temizlenmiş HTTP(S) görsellerine izin verir; `isFromMe`: Sent/Drafts postası ya da gönderen hesabın adresiyle büyük/küçük harf duyarsız aynıysa, klasörden bağımsız) |
| GET | `/api/mails/{id}/headers` | bearer | Tüm özgün başlıkları IMAP üzerinden getirir (`{headers:[{name,value}]}`) |
| GET | `/api/mails/{id}/source` | bearer | Özgün MIME dosyasını `message/rfc822` olarak indirir (çevrimiçi IMAP gerekir) |
| GET | `/api/mails/{id}/signature` | bearer | Özgün S/MIME imzasını doğrular; OpenPGP için `Unverifiable` döner; imzasızsa 422 |
| GET | `/api/search` | bearer | Önbellekteki postada arama (tüm filtreler opsiyonel, AND; aşağıya bakın) |
| GET | `/api/search/remote` | bearer | Kullanıcının başlattığı genel IMAP araması; eksik eşleşmeleri içeri alır, ardından `/api/search` tekrar çağrılır |
| GET | `/api/mails/{mailId}/attachments/{attachmentId}` | bearer | Eki indirir (depolama aranabilirse Range/206) |
| GET | `/api/compose/limits` | bearer | Güncel ek boyutu ve sayı sınırları |
| POST | `/api/mails/send` | bearer + `Idempotency-Key` | Posta gönderir (multipart/form-data; opsiyonel `identityId` bir gönderici kimliği seçer, verilmezse hesap adresi) |
| GET | `/api/mails/{id}/compose/reply · reply-all · forward` | bearer | Hazır doldurulmuş yazma bağlamı |

Normal HTTP(S) `<img src>` görselleri Junk veya `dmarc=fail` dışındaki
postalarda varsayılan olarak açılır. `remoteContent=allow` engellenen
postanın temizlenmiş görsellerini kullanıcı isteğiyle açar; script, event
handler, form, güvensiz URI şeması ve görsel olmayan uzak kaynaklar engelli
kalır. Yanıt gövdesi `remoteImageHosts` ve `remoteImagesAllowed` alanlarını
içerir. Görsellerin otomatik yüklenmesi açılma bilgisini göndericiye iletebilir.

`trackingPixelHosts`, takip pikseli gibi görünen uzak görsellerin hostlarını listeler (genişliği veya yüksekliği 1px ya da daha küçük olan veya `display:none`/`visibility:hidden` ile gizlenen görseller). Bunlar `remoteContent=allow` ile de engelli kalır; istemci takip içeriğinin engellendiğini gösterebilir.

Posta detayı `security` alanında MIME/satır içi OpenPGP `signed`/`encrypted` standardını (`SMime` veya `OpenPgp`) bildirir; bu alan güven veya şifre çözme başarısı anlamına gelmez. İmza ucu `{standard,status,signers}` döner; `status`: `Valid`, `Untrusted`, `Invalid` veya `Unverifiable`. `Valid`, kriptografik imza ve sertifika zincirinin doğrulandığı anlamına gelir; `Unverifiable` geçerli imza değildir. Kaynak uçları eşleşen IMAP UIDVALIDITY ve çevrimiçi sunucu gerektirir; hatalar: `mail_not_found` (404), `mail_account_needs_reauthentication` veya `mail_operation_conflict` (409), `mail_provider_unavailable` (502). Ham MIME ve tam başlıklar özel bilgi içerir; yalnız kullanıcının isteğiyle gösterilmeli veya paylaşılmalıdır.

Okundu bilgisi (MDN) isteğe bağlıdır; teslim durumu bildirimi (DSN) otomatiktir. Üç gönderim yolu da `requestReadReceipt` kabul eder (`true`/`false`, yoksa `false`, başka değer 400 `invalid_receipt_option`): `POST /api/mails/send` ve `POST /api/scheduled-sends` form alanı ile `POST /api/drafts/{id}/send?requestReadReceipt=true` sorgu parametresi. Mesaj yalnız değer `true` ise hesap/seçilen kimlik adresiyle `Disposition-Notification-To` başlığı taşır; bayrak gönderim idempotency parmak izine dahildir, aynı `Idempotency-Key` farklı değerle tekrar kullanılırsa 409 `idempotency_conflict` döner. Zamanlanmış gönderim bayrağı saklar ve dispatch sırasında uygular; liste ve detay yanıtları `requestReadReceipt` döner, `PUT /api/scheduled-sends/{id}` (form alanı) ve `POST /api/scheduled-sends/{id}/reschedule` (JSON, null olabilir) alan yoksa kayıtlı değeri korur. Sunucu DSN destekliyorsa her gönderim SMTP DSN başarı/hata isteği ekler; bunun için seçenek yoktur. DSN desteklemeyen sunucu gönderimi hiçbir zaman başarısız kılmaz veya uyarı üretmez. Alındı garantisi yoktur: alıcı/sunucu isteği yok sayabilir. Senkronizasyon ortaya çıkan raporları içe aktarmaz: okundu bilgileri (`multipart/report; report-type=disposition-notification`) ve tüm alıcı eylemleri `delivered`, `relayed` veya `expanded` olan teslim raporları atlanır ve yalnızca IMAP sunucusunda kalır. Geri dönen mailler (herhangi bir alıcısı `failed` veya `delayed`) ve ayrıştırılamayan raporlar normal mail olarak içe aktarılır.

Posta detayı, saklanan en üst `Authentication-Results` başlığı SPF, DKIM veya DMARC sonucu içeriyorsa `authentication` döner; aksi halde alan `null` olur. Sonuçlar `authservId` ile birlikte `pass`, `fail`, `softfail`, `neutral`, `none`, `temperror`, `permerror` ve `policy` değerleriyle sınırlıdır. Sunucu doğrulamayı yeniden çalıştırmaz; istemciler bu değerleri yalnız bilgi amaçlı göstermelidir.

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
| POST | `/api/drafts/{id}/send` | bearer + `Idempotency-Key` | Taslağı gönderir ve siler (opsiyonel `?requestReadReceipt=true` MDN ister); başarılı gönderim aynı key ile tekrarlanırsa `422 mail_not_draft` yerine kayıtlı sonuç döner (`sent: true`, `draftRemoved: true`) |

### Zamanlanmış gönderimler

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| POST | `/api/scheduled-sends` | bearer + `Idempotency-Key` | Postayı ileri bir tarihte göndermek üzere zamanlar (multipart/form-data) |
| GET | `/api/scheduled-sends` | bearer | Hesaba ait gönderimleri deneme ve hata durumlarıyla listeler |
| GET | `/api/scheduled-sends/{id}` | bearer | Düzenlenebilir içeriği ve bekletilen ek üstverisini getirir |
| PUT | `/api/scheduled-sends/{id}` | bearer | Pending gönderimi atomik değiştirir (multipart/form-data; tekrarlanan `keepAttachmentIds` mevcut ekleri korur, yeni dosyalar yüklenir) |
| POST | `/api/scheduled-sends/{id}/reschedule` | bearer + yeni `Idempotency-Key` | Failed gönderimi yeni Pending gönderime kopyalar |
| DELETE | `/api/scheduled-sends/{id}` | bearer | Pending gönderimi iptal eder veya Failed içeriği siler |

`PUT`, oluşturmayla aynı alıcı, gövde, konu, zaman ve ek sınırlarını uygular.
Dispatch başladıysa 409 `scheduled_send_already_sent`, Failed/Cancelled kayıt
için `scheduled_send_not_pending`, başka düzenleme önce tamamlandıysa
`scheduled_send_modified` döner. Bu çakışmalar içeriği değiştirmez.
DeliveryUnknown gönderimler tekrar denenmez ve iptal edilemez.
### Signatures

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| GET | `/api/signatures` | bearer | Adlandırılmış imzaları ve mod başına varsayılanları listeler (`{items, defaults}`) |
| POST | `/api/signatures` | bearer | İmza oluşturur (201) |
| PUT | `/api/signatures/{id}` | bearer | İmzanın yerine yenisini yazar |
| DELETE | `/api/signatures/{id}` | bearer | İmza siler (204); ona bakan varsayılanlar temizlenir |
| PUT | `/api/signatures/defaults` | bearer | Üç varsayılanı birden değiştirir (`{newMailSignatureId?, replySignatureId?, forwardSignatureId?}`; null ilgili modu temizler) |

Gövde: `{name, bodyText, bodyHtml?}`; yanıta `id`, `createdAt`, `updatedAt` eklenir.
İmzalar istemcinin düzenleyiciye eklediği metindir, sunucu giden postaya asla
kendisi eklemez: uygulama yazma moduna (yeni, yanıt/tümünü yanıtla, ilet)
ait varsayılanı seçip gövdesini düzenleyiciye yerleştirir. `name` kırpılır,
1-100 karakter; `bodyText` kırpılır, 1-10000 karakter; `bodyHtml` opsiyonel
varyanttır, kırpılır ve en fazla 50000 karakterdir. Doğrulama hataları
`name`, `bodyText` veya `bodyHtml` anahtarlı 400 validation problem döner.
Hesaba ait olmayan bir varsayılan id'si 404 `signature_not_found` döner;
başka hesaba ait imza id'si için de aynı kod döner. İmzalar hesapla birlikte
silinir. `PUT /api/account/signature`, yeni-posta varsayılanının eski adıdır:
boş olmayan metin onu oluşturur veya günceller (ve yanıt/ilet varsayılanı
boşsa onları da başlatır); boş metin ona bakan her modu temizler.

### Identities

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| GET | `/api/identities` | bearer | Gönderici kimliklerini listeler (varsayılan önce) |
| POST | `/api/identities` | bearer | Kimlik oluşturur (201) |
| PUT | `/api/identities/{id}` | bearer | Kimliğin yerine yenisini yazar |
| DELETE | `/api/identities/{id}` | bearer | Kimlik siler (204); bekleyen zamanlanmış gönderim kullanıyorsa engellenir |

Gövde: `{emailAddress, displayName?, replyTo?, signatureId?, isDefault}`;
yanıt aynı alanlara `id` ekler. Hesap başına en fazla bir varsayılan olur:
`isDefault: true` ile oluşturma/güncelleme önceki varsayılanı temizler.
`emailAddress` görünen adsız yalın adres olmalı, en fazla 320 karakter ve
hesap içinde büyük/küçük harf duyarsız benzersizdir (aksi halde 409
`identity_already_exists`); `displayName` en fazla 250 karakter; `replyTo`
opsiyonel yalın adrestir; `signatureId` hesaba ait olmalı (değilse 404
`signature_not_found`). Doğrulama hataları `emailAddress`, `displayName`
veya `replyTo` anahtarlı 400 validation problem döner. Başka hesaba ait
kimlik id'si 404 `identity_not_found` döner. Bekleyen zamanlanmış gönderimin
kullandığı kimliği silmek 409 `identity_in_use` döner. Gönderim, taslak ve
zamanlanmış gönderim opsiyonel `identityId` form alanı kabul eder: mesaj o
kimliğin adresi, görünen adı ve Reply-To değeriyle gider (daha sonra
gönderilen taslak, kimliği kayıtlı From adresinden çözer). Kimlikler hesapla
birlikte silinir.

### Mail ops

| Metot | Yol | Yetki | Açıklama |
|---|---|---|---|
| PATCH | `/api/mails/{id}/read` | bearer | Okundu durumunu ayarlar (body) |
| POST | `/api/mails/{id}/{op}` | bearer | `op`: read, unread, star, unstar, trash, restore, archive, spam, not-spam, delete (204, gövdesiz). `restore` maili çöpe/spam'e atıldığı klasöre geri taşır; kaydedilmiş kaynağı olmayan (başka istemci veya sunucu kuralıyla taşınmış) Trash/Junk maili Inbox'a gider. `delete` maili kalıcı olarak siler ve yalnızca Trash/Junk'ta izinlidir (aksi halde 422 `mail_operation_not_supported`; sunucu tarafı hata 502 `mail_delete_failed`) |
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
