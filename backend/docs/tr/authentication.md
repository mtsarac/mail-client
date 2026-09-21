# Kimlik doğrulama

🇬🇧 [English](../en/authentication.md)

API'de ayrı bir kullanıcı tablosu yoktur: oturum açmak, bir posta kutusuna
bağlanmaktır. Bir `MailAccount` (posta kutusu başına bir tane) cihaz başına bir
`MailSession` ile birden çok oturuma sahip olabilir.

## Token'lar

| Token | Biçim | Ömür |
|-------|-------|------|
| Access token | JWT (HS256), `sub` = hesap kimliği | 15 dk (`Jwt__AccessTokenMinutes`) |
| Refresh token | Opak, oturum başına hash'li saklanır | 180 gün, kaydırmalı (`Session:*`) |

`Authorization: Bearer <accessToken>` gönderin. `401` alınca `POST /api/auth/refresh`
çağırın. Refresh token'lar **döner**: her çağrı eskisini geçersiz kılıp yeni bir
çift verir; eski token'ı tekrar kullanmak `invalid_refresh_token` ile başarısız olur.

## Giriş akışları

1. **Şifre / uygulama şifresi (keşifle)** — `POST /api/accounts/discover` →
   `discoveryId` + şifre ile `POST /api/accounts/connect`. Sunucu, hesabı
   oluşturmadan önce kimlik bilgilerini IMAP/SMTP'ye karşı doğrular.
2. **Manuel sunucu** — host/port ayarlarıyla `POST /api/accounts/connect-manual`
   (dış host'lar SSRF kurallarına göre denetlenir).
3. **OAuth2 (Google, Microsoft)** — `POST /api/accounts/oauth/{provider}/start`
   yetkilendirme URL'si ve `state` döner; sağlayıcı yönlendirmesinden sonra
   uygulama `state` + `code` değerlerini `.../complete`'e gönderir.
   Authorization Code + PKCE; sağlayıcı token'ları istemciye hiç ulaşmaz.
   `OAuth__*` yapılandırması gerekir.
4. **Mevcut posta kutusu, yeni cihaz** — `POST /api/accounts/login` posta kutusu
   bilgilerini doğrular ve mevcut hesapta yeni oturum açar.

Dördü de aynı `TokenResponse`'u (access + refresh + bitiş) döner. `connect*` yalnızca
*oluşturur*; hesap zaten varsa `login` kullanın.

## Oturumlar ve cihazlar

- `GET /api/account/sessions` oturumları listeler; `DELETE /api/account/sessions/{id}`
  birini uzaktan kapatır (o cihaz sonraki refresh'te `401` alır).
- `POST /api/auth/logout` çağıran cihazın oturumunu kapatır.
- `POST /api/devices` hesap için Firebase push token'ı kaydeder.
- `POST /api/account/reconnect` posta şifresi değişince saklanan bilgileri
  günceller (kimlik bilgileri çalışmayı bırakınca `reauthentication` push bildirimi uygulamayı uyarır).

## Kimlik bilgisi saklama

Posta şifreleri/OAuth token'ları ASP.NET Data Protection ile şifrelenir
(`CredentialProtector`). Anahtarlar `DataProtection__KeyPath` içindedir; production'da
sertifikayla korunmalıdır (`DataProtection__CertificatePath`, bkz.
[Docker](../DOCKER.md)). Anahtar halkasını kaybetmek saklı bilgileri okunamaz yapar.

## E-posta allowlist'i (opsiyonel)

Varsayılan kapalıdır. `Whitelist.Enabled` (runtime ayarı) açıkken:

- yalnızca allowlist'teki e-postalar posta kutusu oluşturabilir veya giriş yapabilir;
- bir e-postayı kaldırmak o posta kutusunu anında devre dışı bırakır;
- arka plan işi (`ReconciliationIntervalMinutes`, varsayılan 15) artık izinli
  olmayan hesapları devre dışı bırakır ve `DataRetentionGraceDays` (varsayılan 30)
  sonra verilerini siler.

Management API ile yönetilir (`X-Management-Key` header'ı, `Management__Enabled=true`
gerekir):

```bash
curl -X POST "$API/api/management/whitelist/emails" \
  -H "X-Management-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"emails":["person@example.com"]}'
```


## Management API

`/api/management/*` JWT ile korunmaz. `X-Management-Key` header'ı ister,
`Management__Enabled=true` olmadıkça kapalıdır ve production'da
`Management__ApiKey` olmadan uygulama başlamaz. Genel internete açmayın.
