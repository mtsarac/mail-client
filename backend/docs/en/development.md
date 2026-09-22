# Development

🇹🇷 [Türkçe](../tr/development.md)

For production deployment, see [Production](production.md).

## Requirements

.NET 10 SDK, PostgreSQL, Docker (optional, for compose and GreenMail).

## Run locally

With Docker (Postgres + GreenMail + migrations + API on `:8080`):

```bash
cp .env.example .env    # set Jwt__Key at minimum
docker compose up --build
```

Host ports default to `5432` (Postgres), `3143`/`3025` (GreenMail IMAP/SMTP),
`8080` (API) — override via `POSTGRES_PORT` / `GREENMAIL_IMAP_PORT` /
`GREENMAIL_SMTP_PORT` / `API_PORT` in `.env` if those collide with something
already running on your machine.

Without Docker: start PostgreSQL, set `ConnectionStrings__Default` in `.env`,
apply migrations, then run the API:

```bash
dotnet ef database update --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
dotnet run --project backend/src/MailClient.Api
```

Runtime files: `data/` (attachments, data-protection keys) and `logs/` are git-ignored.

### Rider `lan-http` launch profile

`Properties/launchSettings.json` also has a `lan-http` profile (used by the
Rider run configuration of the same name) that runs the API on `0.0.0.0:5071`
with `ASPNETCORE_ENVIRONMENT=Production` — for testing from another device on
the LAN (e.g. a Flutter app on a phone) against production-like behavior:
HTTPS-redirect/HSTS logic active (harmless over plain LAN HTTP — Kestrel logs
a warning and serves the request anyway since no HTTPS port is configured),
Swagger and dev CORS disabled, FCM enabled. It needs two files this profile's
`environmentVariables` expect and that are gitignored (`*.pfx`), so a fresh
clone must generate/obtain them first:

- `backend/src/MailClient.Api/data/lan-dataprotection.pfx` — a self-signed
  Data Protection cert, empty export password:
  `openssl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem -days 3650 -nodes -subj "/CN=mailclient-lan-dataprotection"`
  then `openssl pkcs12 -export -out backend/src/MailClient.Api/data/lan-dataprotection.pfx -inkey key.pem -in cert.pem -passout pass:`.
- `secrets/mail-client-37a01-firebase-adminsdk-fbsvc-588f026e0e.json` — the
  project's Firebase service-account key (ask whoever holds it); without it,
  either obtain the real file or set `Firebase__Enabled=false` in the profile
  to fall back to a no-op push sender.

## Firebase Cloud Messaging

Optional; push notifications no-op unless enabled. Set in `.env`:
`Firebase__Enabled=true`, `Firebase__ProjectId=<id>`,
`Firebase__CredentialsPath=<path to a service-account JSON>` (get one from
Firebase Console → Project settings → Service accounts). Under Docker,
set `FIREBASE_CREDENTIALS_HOST_PATH` instead — compose mounts it and fixes
`Firebase__CredentialsPath` to the in-container path. Full setup:
[Production → Firebase Cloud Messaging](production.md#5-firebase-cloud-messaging-optional)
(same variables apply in Development).

## Build, test, format

```bash
dotnet restore backend/MailClient.slnx
dotnet build backend/MailClient.slnx --configuration Release --no-restore
dotnet test backend/MailClient.slnx --configuration Release --no-build
dotnet format backend/MailClient.slnx --verify-no-changes
dotnet test backend/tests/MailClient.Tests/MailClient.Tests.csproj --filter 'FullyQualifiedName~TestName'
```

Warnings are errors (`Directory.Build.props`).

## Integration tests

External-service tests are skipped unless opted in:

| Scenario | Enable with |
|----------|-------------|
| PostgreSQL | `MAILCLIENT_TEST_POSTGRES_ADMIN` (admin connection string) |
| GreenMail (IMAP/SMTP) | `MAILCLIENT_TEST_GREENMAIL=1` or `MAILCLIENT_TEST_GREENMAIL_*` |

CI (`.github/workflows/ci.yml`) runs PostgreSQL and GreenMail and executes
restore, Release build, NuGet vulnerability audit, tests and format check;
CodeQL and dependency review run separately.

## EF Core migrations

- Create with `dotnet ef migrations add <Name>`; remove with `migrations remove`.
- Never hand-write migration files or edit `.Designer.cs` / the model snapshot.
  Editing `Up()`/`Down()` for backfills or custom SQL is fine.
- Never modify a migration already merged to `main`; add a new one.
- Docker applies migrations via a separate `migrate` step, not at API startup.

## Conventions

- Keep the layering; provider-specific MailKit code stays in Infrastructure.
- Keep account-owned operations scoped to the authenticated `MailAccount`.
- Mail mutations stay remote-first and respect IMAP UID/UIDVALIDITY.
- Preserve API contracts and stable error codes unless the change is the point.
- Never commit `.env`, credentials, certificates, key rings or `data/`.
- Conventional commits (`feat:`, `fix:`, `docs:`, `chore:`), author is the
  repository owner only. Full rules: [AGENTS.md](../../../AGENTS.md).
