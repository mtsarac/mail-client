# Mail Client Backend

🇬🇧 [English](README.md)

.NET 10 ve PostgreSQL ile yazılmış IMAP/SMTP posta istemcisi API'si. Bir posta
kutusu birden çok cihazdan oturum açabilir; API uzak sunucudan postayı senkronize
eder, istemciye sunar ve değişiklikleri (okundu, taşı, çöpe at, gönder…) önce
posta sunucusunda uygular.

## Hızlı başlangıç

```bash
cp .env.example .env        # en az Jwt__Key ayarla (32+ karakter)
docker compose up --build   # Postgres + GreenMail + migration + API (:8080)
```

Development ortamında Swagger arayüzü `/swagger` adresindedir.
Sağlık uçları: `/health/live`, `/health/ready`.

Docker olmadan çalıştırma için: [Geliştirme](docs/tr/development.md).

## Repo yapısı

| Yol | İçerik |
|-----|--------|
| `backend/src/MailClient.Api` | Program, middleware, endpoint grupları |
| `backend/src/MailClient.Application` | Sözleşmeler, options, arayüzler, runtime ayarları |
| `backend/src/MailClient.Domain` | Entity ve enum'lar |
| `backend/src/MailClient.Infrastructure` | EF Core, MailKit, OAuth, push, depolama, sync |
| `backend/tests/MailClient.Tests` | Birim ve entegrasyon testleri |
| `docs/` | Dokümantasyon |

## Dokümantasyon

| Konu | English | Türkçe |
|------|---------|--------|
| Mimari | [architecture](docs/en/architecture.md) | [architecture](docs/tr/architecture.md) |
| API referansı | [api-reference](docs/en/api-reference.md) | [api-reference](docs/tr/api-reference.md) |
| Kimlik doğrulama | [authentication](docs/en/authentication.md) | [authentication](docs/tr/authentication.md) |
| Yapılandırma | [configuration](docs/en/configuration.md) | [configuration](docs/tr/configuration.md) |
| Geliştirme | [development](docs/en/development.md) | [development](docs/tr/development.md) |

Ayrıca: [Docker](docs/DOCKER.md) · [Observability](backend/OBSERVABILITY.md) ·
[Flutter entegrasyon rehberi](docs/flutter-api-integration.md)

## Lisans

Bkz. [LICENSE](LICENSE).
