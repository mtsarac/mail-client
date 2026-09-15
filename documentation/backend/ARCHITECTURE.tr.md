# Mimari Referansı (Türkçe)

> English: [ARCHITECTURE.en.md](ARCHITECTURE.en.md)

## Kimlik

`MailAccount` kimliği doğrulanan asıl varlıktır. Yeni backend içinde uygulama User'ı, rol, kayıt, onay, admin yönetimi, parola hash'i veya UserId sahipliği yoktur. Her posta kutusu kendi credential, session, klasör, posta, ek, cihaz kaydı, gönderim işlemi ve audit kayıtlarına sahiptir.

Access JWT içindeki `sub`, `MailAccountId` değeridir. Kalıcı giriş deneyimi kısa ömürlü access JWT ve dönen refresh session ile sağlanır. PostgreSQL yalnız refresh-token hash'ini tutar. Posta sağlayıcı credential'ı ayrı güvenlik kavramıdır ve ASP.NET Core Data Protection ile şifrelenir.

## Katmanlar

```text
MailClient.Domain          yalnız entity ve enumlar
        ↑
MailClient.Application     sözleşmeler, DTO'lar, discovery modeli
        ↑
MailClient.Infrastructure  EF Core, PostgreSQL, MailKit, DNS/HTTP, depolama
        ↑
MailClient.Api             DI, JWT, middleware, endpoint, OpenAPI
```

Domain; EF Core, MailKit, ASP.NET Core veya ağ bağımlılığı taşımaz. Application yalnız Domain'e bağlıdır. Infrastructure dış bağlantıları ve persistence'ı gerçekler. API servisleri birleştirir ve HTTP sözleşmelerini eşler.

## Discovery ve bağlantı

Otomatik keşif sırası:

1. bilinen sağlayıcı kataloğu
2. DNS SRV
3. Thunderbird biçimli autoconfiguration
4. Microsoft Autodiscover
5. kontrollü güvenli hostname heuristic'leri

Adaylar yalnız desteklenen güvenli mod ve portları kullanır. MailKit bağlantısından önce hostname ve çözülen tüm adresler outbound-host doğrulamasından geçer. TLS sertifika doğrulaması açıktır. Discovery HTTP istemcileri redirect izlemez. İlk doğrulanan aday kazanır.

Başarılı discovery, rastgele ve opaque ID arkasında geçici olarak sunucuda tutulur. Başarısızlık HTTP 422, `code=mail_discovery_failed` ve `manualSetupAvailable=true` döndürür; hesap veya credential yazılmaz.

Manuel kurulum yalnız discovery'yi atlar. Persistence öncesi host sözdizimi, DNS/IP güvenliği, transport modu, TLS, IMAP auth ve SMTP auth aynı şekilde doğrulanır. Otomatik ve manuel yollar aynı MailAccount/MailCredential/MailSession modelinde birleşir.

## Persistence

Yeni backend tek temiz Initial migration kullanır ve varsayılan DB adı `mailclient_v2` olur.

Temel constraint'ler:

- benzersiz normalize posta adresi
- hesap başına benzersiz credential auth metodu
- benzersiz refresh-token hash'i
- hesap başına benzersiz klasör
- klasör başına benzersiz UID
- benzersiz `(MailAccountId, IdempotencyKey)` gönderim işlemi
- benzersiz `(MailAccountId, Token)` cihaz kaydı

## Posta kapsamı ve arka plan işi

Korumalı endpoint'ler MailAccountId değerini `ICurrentMailAccount` üzerinden alır. Klasör, posta, ek, cihaz ve gönderim sorguları hesap ID'siyle sınırlandırılır. Başka hesaba ait ID, 404 döndürür.

Başarılı bağlantı, posta kutusunun tümünü indirmeyi beklemeden initial sync kuyruğuna istek bırakır. Süreç içi worker bilinçli olarak küçüktür; sağlayıcıya özel OAuth2 ve legacy incremental sync'in tam portu ayrı iştir.

## Güvenlik ve gözlemlenebilirlik

Outbound doğrulama localhost, loopback, private, link-local, multicast ve güvensiz hedefleri engeller. Ek yolları yapılandırılmış storage kökü altında tutulur. SMTP alanlarında CR/LF injection reddedilir. Rate limit JWT MailAccountId veya pre-auth IP ile bölünür.

Serilog JSON kayıtlarını `logs/app-*.json` ve `logs/http-*.json` dosyalarına yazar. Correlation ID, `X-Correlation-ID` ile döner. Audit satırları nullable MailAccountId kullanır. Parola, refresh token, OAuth materyali, client secret, token ve credential alanları recursive biçimde maskelenir.

## Legacy sınıflandırması

- REUSE/ADAPT: Data Protection deseni, MailKit bağlantı yaklaşımı, outbound host kuralları, attachment path confinement, structured logging düzeni.
- REWRITE: identity, credentials, sessions, discovery, account connection, current-account context, schema, endpointler.
- REMOVE: User, rol/statü, application register/login, approval/admin yönetimi, token version ve UserId sahipliği.
