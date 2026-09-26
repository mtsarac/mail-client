# Mail Client Backend

🇬🇧 [English](README.md)

.NET 10 ve PostgreSQL ile yazılmış IMAP/SMTP posta istemcisi API'si. Bir posta
kutusu birden çok cihazdan oturum açabilir; API uzak sunucudan postayı senkronize
eder, istemciye sunar ve değişiklikleri (okundu, taşı, çöpe at, gönder…) önce
posta sunucusunda uygular.

Posta ayrıntısında normal uzak görseller kendiliğinden yüklenir. Junk
klasöründeki veya `dmarc=fail` olan postalarda normal görseller ancak
`remoteContent=allow` isteğiyle yüklenir. Küçük veya görünmez takip pikselleri,
betikler ve görsel olmayan uzak kaynaklar engellenir. Diğer uzak görsellerin
yüklenmesi, postayı açtığınızı göndericiye gösterebilir. Ayrıntılar için
[posta API'sine](backend/docs/tr/api-reference.md#mail) bakın.

## Hızlı başlangıç

```bash
cp .env.example .env        # en az Jwt__Key ayarla (32+ karakter)
docker compose up --build   # Postgres + GreenMail + migration + API (:8080)
```

Development ortamında Swagger arayüzü `/swagger` adresindedir.
Sağlık uçları: `/health/live`, `/health/ready`.

Docker olmadan çalıştırma için: [Geliştirme](backend/docs/tr/development.md).

## Repo yapısı

| Yol | İçerik |
|-----|--------|
| `backend/src/MailClient.Api` | Program, middleware, endpoint grupları |
| `backend/src/MailClient.Application` | Sözleşmeler, options, arayüzler, runtime ayarları |
| `backend/src/MailClient.Domain` | Entity ve enum'lar |
| `backend/src/MailClient.Infrastructure` | EF Core, MailKit, OAuth, push, depolama, sync |
| `backend/tests/MailClient.Tests` | Birim ve entegrasyon testleri |
| `backend/docs/` | Dokümantasyon |

## Dokümantasyon

| Konu | English | Türkçe |
|------|---------|--------|
| Mimari | [architecture](backend/docs/en/architecture.md) | [architecture](backend/docs/tr/architecture.md) |
| API referansı | [api-reference](backend/docs/en/api-reference.md) | [api-reference](backend/docs/tr/api-reference.md) |
| Kimlik doğrulama | [authentication](backend/docs/en/authentication.md) | [authentication](backend/docs/tr/authentication.md) |
| Yapılandırma | [configuration](backend/docs/en/configuration.md) | [configuration](backend/docs/tr/configuration.md) |
| Geliştirme | [development](backend/docs/en/development.md) | [development](backend/docs/tr/development.md) |
| Production | [production](backend/docs/en/production.md) | [production](backend/docs/tr/production.md) |

Ayrıca: [Docker](backend/docs/DOCKER.md) · [Observability](backend/OBSERVABILITY.md) ·
[Flutter entegrasyon rehberi](backend/docs/flutter-api-integration.md)

## Lisans

Bkz. [LICENSE](LICENSE).
