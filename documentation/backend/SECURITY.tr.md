# Güvenlik Referansı (Türkçe)

> English: [SECURITY.en.md](SECURITY.en.md)

## Kimlik doğrulama

MailAccount principal'dır. JWT issuer, audience, imza anahtarı ve süreyi doğrular; `sub`, MailAccountId değeridir. Access süresi yapılandırılabilir ve kısa ömürlü kalır. Refresh session'lar `Session` yapılandırma bölümünde yaşar (`RefreshTokenLifetimeDays`, varsayılan 180; `SlidingExpiration`, varsayılan true). Sliding rotasyon her başarılı refresh'te session ömrünü yeniler; fixed mod özgün mutlak bitişi korur, session başlangıç sınırını asla aşamaz. Refresh token'lar kriptografik rastgele üretilir, yalnız SHA-256 hash olarak saklanır, eşzamanlılıkta tek kazananlı claim ile döndürülür ve logout ile iptal edilir. Provider credential'ı session token'ından ayrıdır ve ASP.NET Core Data Protection ile şifrelenir.

Password ve AppSpecificPassword uygulanmıştır. OAuth2 storage modeli hazırdır; provider authorization/callback entegrasyonları deferred'dır. Sahte generic OAuth akışı yoktur.

## Hesap izolasyonu

Korumalı API'ler MailAccountId değerini `ICurrentMailAccount` üzerinden alır; request account ID'sine güvenmez. Folder, mail, attachment, device ve send kayıtları MailAccountId ile filtrelenir. Başka hesaba ait ID, 404 döndürür.

## Discovery ve SSRF

Sıra: bilinen provider, DNS SRV, autoconfig, Microsoft Autodiscover, kontrollü heuristic. Discovery HTTP istemcileri SSRF-güvenli handler üzerinden çalışır: giden host `OutboundHostValidator` ile çözümlenir ve TCP bağlantısı doğrulanmış IP'ye sabitlenir; TLS SNI ve sertifika doğrulaması özgün URI hostname'i ile devam eder. Otomatik redirect kapalıdır. Aday ve manuel hostlar protokol auth öncesi DNS/adres kontrolünden geçer. Bağlantılar doğrulanmış IP'yi kullanırken TLS sertifika/SNI doğrulaması için özgün hostname'i korur; böylece ikinci DNS sorgusu ve DNS-rebinding TOCTOU önlenir. Localhost, loopback, private IPv4, link-local, multicast, IPv6 unique-local ve güvensiz hedefler reddedilir. Yalnız `SslOnConnect` ve `StartTls` modellenir. MailKit sertifika doğrulaması kapatılmaz.

Manuel kurulum yalnız discovery'yi atlar. Host validation, DNS/IP, TLS, IMAP auth veya SMTP auth kontrollerini atlayamaz.

## Saklanan veri

Credential materyali persistence öncesi şifrelenir ve response'a girmez. Refresh token hash saklanır. Attachment yolları canonical hale getirilir ve storage kökü altında kalmak zorundadır. Send alanlarında CR/LF header injection reddedilir. Send idempotency, MailAccountId ve key ile unique; request fingerprint SHA-256'dır.

## Rate limiting ve hatalar

Global fixed-window limit, authenticated trafikte MailAccountId; pre-auth trafikte remote IP ile bölünür. Beklenen request, discovery ve provider hataları stabil kodlu ProblemDetails kullanır. Raw MailKit/network exception'ları API sözleşmesi değildir.

Discovery state, kriptografik rastgele bir tanımlayıcıyla sunucu tarafında saklanır ve yalnız başarılı hesap bağlantısı tarafından tüketilir; başarısız kimlik doğrulama state'i süresi dolana kadar kullanılabilir tutar.

## Log ve audit

Serilog JSON kayıtları `logs/app-*.json` ve `logs/http-*.json` dosyalarına yazar. Correlation ID `X-Correlation-ID` ile taşınır; account ID auth sonrası structured scope'a girer. `HttpBodyLoggingMiddleware` istek başına tek yapılandırılmış tamamlanma olayı yazar: method, path, query, status, süre, account id ve sınırlı request/response gövdeleri (`HttpLogging:MaxRequestBodyBytes` / `MaxResponseBodyBytes`, varsayılan 64 KB, kırpılma işaretli). JSON gövdeler recursive, büyük/küçük harf duyarsız redaction sonrası iç içe nesne olarak gömülür; JSON olmayan ve binary yükler yalnız metadata olarak loglanır. Multipart send eki dosya adlarını, content type'larını ve boyutlarını loglar — byte'ları veya alan değerlerini asla — ve `bodyHtml`/`bodyText` değerleri uzunluk metadata'sıyla maskelenir. Authorization başlıkları, cookie'ler ve refresh token'lar asla yakalanmaz. Audit satırları nullable MailAccountId kullanır ve mailbox silindiğinde saklanır (account id NULL'a çekilir). Redaction; password, appSpecificPassword, newPassword, currentPassword, accessToken, refreshToken, providerRefreshToken, authorizationCode, codeVerifier, token, authorization, clientSecret, secret, credential, encryptedMaterial ve apiKey alanlarını kapsar.

## Sahiplik ve silme

Mail credential, session, folder, mail, attachment, sync state, skipped UID, send operation ve device token sahibi MailAccount ile cascade silinir. Audit log'ları mailbox silindiğinde NULL account id ile saklanır.

## Operasyon gereksinimleri

Production development JWT ve DB credential'larını değiştirmeli, Data Protection key'lerini korumalı kalıcı storage'da tutmalı, HTTPS'i doğru sonlandırmalı, CORS/proxy trust sınırlandırmalı ve reverse-proxy body limitlerini eşlemelidir. Yeni migration'lar `mailclient_v2` hedefler; legacy DB ve migration'lar ayrıdır.
