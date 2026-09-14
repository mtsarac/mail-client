# Güvenlik Referansı (Türkçe)

> English: [SECURITY.en.md](SECURITY.en.md)

## Kimlik doğrulama

MailAccount principal'dır. JWT issuer, audience, imza anahtarı ve süreyi doğrular; `sub`, MailAccountId değeridir. Access süresi yapılandırılabilir. Kalıcı MailSession refresh token'ları kriptografik rastgele üretilir, yalnız SHA-256 hash olarak saklanır, refresh sırasında döndürülür ve logout ile iptal edilir. Provider credential'ı session token'ından ayrıdır ve ASP.NET Core Data Protection ile şifrelenir.

Password ve AppSpecificPassword uygulanmıştır. OAuth2 storage modeli hazırdır; provider authorization/callback entegrasyonları deferred'dır. Sahte generic OAuth akışı yoktur.

## Hesap izolasyonu

Korumalı API'ler MailAccountId değerini `ICurrentMailAccount` üzerinden alır; request account ID'sine güvenmez. Folder, mail, attachment, device ve send kayıtları MailAccountId ile filtrelenir. Başka hesaba ait ID, 404 döndürür.

## Discovery ve SSRF

Sıra: bilinen provider, DNS SRV, autoconfig, Microsoft Autodiscover, kontrollü heuristic. Discovery HTTP istemcileri sonlu timeout kullanır ve otomatik redirect izlemez. Aday ve manuel hostlar protokol auth öncesi DNS/adres kontrolünden geçer. Bağlantılar doğrulanmış IP'yi kullanırken TLS sertifika/SNI doğrulaması için özgün hostname'i korur; böylece ikinci DNS sorgusu ve DNS-rebinding TOCTOU önlenir. Localhost, loopback, private IPv4, link-local, multicast, IPv6 unique-local ve güvensiz hedefler reddedilir. Yalnız `SslOnConnect` ve `StartTls` modellenir. MailKit sertifika doğrulaması kapatılmaz.

Manuel kurulum yalnız discovery'yi atlar. Host validation, DNS/IP, TLS, IMAP auth veya SMTP auth kontrollerini atlayamaz.

## Saklanan veri

Credential materyali persistence öncesi şifrelenir ve response'a girmez. Refresh token hash saklanır. Attachment yolları canonical hale getirilir ve storage kökü altında kalmak zorundadır. Send alanlarında CR/LF header injection reddedilir. Send idempotency, MailAccountId ve key ile unique; request fingerprint SHA-256'dır.

## Rate limiting ve hatalar

Global fixed-window limit, authenticated trafikte MailAccountId; pre-auth trafikte remote IP ile bölünür. Beklenen request, discovery ve provider hataları stabil kodlu ProblemDetails kullanır. Raw MailKit/network exception'ları API sözleşmesi değildir.

## Log ve audit

Serilog JSON kayıtları `logs/app-*.json` ve `logs/http-*.json` dosyalarına yazar. Correlation ID `X-Correlation-ID` ile taşınır; account ID auth sonrası structured scope'a girer. V2 middleware request body loglamaz. Audit satırları nullable MailAccountId kullanır. Recursive redaction; password, appSpecificPassword, accessToken, refreshToken, providerRefreshToken, authorizationCode, codeVerifier, clientSecret, token, credential ve secret alanlarını kapsar.

## Operasyon gereksinimleri

Production development JWT ve DB credential'larını değiştirmeli, Data Protection key'lerini korumalı kalıcı storage'da tutmalı, HTTPS'i doğru sonlandırmalı, CORS/proxy trust sınırlandırmalı ve reverse-proxy body limitlerini eşlemelidir. Yeni migration'lar `mailclient_v2` hedefler; legacy DB ve migration'lar ayrıdır.
