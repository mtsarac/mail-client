# Mail Client Backend

🇹🇷 [Türkçe](README.tr.md)

IMAP/SMTP mail client API built with .NET 10 and PostgreSQL. One mailbox can be
signed in from several devices; the API syncs mail from the remote server,
serves it to the client, and applies mutations (read, move, trash, send…) on the
mail server first.

Mail details load ordinary remote images by default, except in Junk and messages
reporting `dmarc=fail`. Clients can request images for a blocked message with
`remoteContent=allow`. Tiny/hidden tracking pixels, scripts and non-image remote
resources remain blocked; loading other remote images may reveal when a message
was opened. See the [mail API](backend/docs/en/api-reference.md#mail).

## Quick start

```bash
cp .env.example .env        # set at least Jwt__Key (32+ chars)
docker compose up --build   # Postgres + GreenMail + migrations + API on :8080
```

Swagger UI is available at `/swagger` in the Development environment.
Health probes: `/health/live`, `/health/ready`.

Without Docker: see [Development](backend/docs/en/development.md).

## Repository layout

| Path | Contents |
|------|----------|
| `backend/src/MailClient.Api` | Program, middleware, endpoint groups |
| `backend/src/MailClient.Application` | Contracts, options, interfaces, runtime settings |
| `backend/src/MailClient.Domain` | Entities and enums |
| `backend/src/MailClient.Infrastructure` | EF Core, MailKit, OAuth, push, storage, sync |
| `backend/tests/MailClient.Tests` | Unit and integration tests |
| `backend/docs/` | Documentation |

## Documentation

| Topic | English | Türkçe |
|-------|---------|--------|
| Architecture | [architecture](backend/docs/en/architecture.md) | [architecture](backend/docs/tr/architecture.md) |
| API reference | [api-reference](backend/docs/en/api-reference.md) | [api-reference](backend/docs/tr/api-reference.md) |
| Authentication | [authentication](backend/docs/en/authentication.md) | [authentication](backend/docs/tr/authentication.md) |
| Configuration | [configuration](backend/docs/en/configuration.md) | [configuration](backend/docs/tr/configuration.md) |
| Development | [development](backend/docs/en/development.md) | [development](backend/docs/tr/development.md) |
| Production | [production](backend/docs/en/production.md) | [production](backend/docs/tr/production.md) |

Also: [Docker](backend/docs/DOCKER.md) · [Observability](backend/OBSERVABILITY.md) ·
[Flutter integration guide (TR)](backend/docs/flutter-api-integration.md)

## License

See [LICENSE](LICENSE).
