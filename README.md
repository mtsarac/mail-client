# Mail Client

Mail Client is a Flutter + ASP.NET Core mail bridge for hosting-provider email accounts.

The Flutter app does not connect to IMAP or SMTP directly. It calls this backend over HTTP. The backend stores mail metadata in PostgreSQL, runs a background IMAP sync worker, sends mail through the user's own SMTP server, and delivers new-mail push notifications through Firebase Cloud Messaging.

## Architecture

```text
Flutter app
  -> ASP.NET Core Web API
      -> PostgreSQL (mail metadata, identity, sync state)
      -> IMAP/SMTP hosting mailbox (MailKit)
      -> local file storage (attachments)
      -> Firebase Cloud Messaging (new-mail push)
```

```text
backend/
  MailClient.slnx
  src/
    MailClient.Api/             # HTTP API, Swagger, configuration
    MailClient.Application/     # application services and contracts
    MailClient.Domain/          # mail domain entities
    MailClient.Infrastructure/  # EF Core, PostgreSQL, MailKit mail adapters, local file storage
  tests/
    MailClient.Api.Tests/            # full-stack integration tests (Testcontainers PostgreSQL)
    MailClient.Infrastructure.Tests/ # unit tests + PostgreSQL sync semantics (Testcontainers)
```

The frontend team can place the Flutter project in `flutter_client/`. That path is already expected by the repository docs and `.gitignore`.

## What the backend does

- **Auth**: JWT register/login, admin user lifecycle, token-version session invalidation.
- **Mail accounts**: per-user IMAP/SMTP configuration with encrypted credentials, connection testing, folder discovery.
- **Background sync**: polls active accounts, caches mail in PostgreSQL, keeps read/unread two-way with IMAP.
- **Sending**: idempotent SMTP send with Sent-folder copy and attachment support.
- **Push**: Firebase Cloud Messaging for new Inbox mail, with invalid-token cleanup.
- **Security**: rate limiting, SSRF-safe host validation, encrypted secrets, CI vulnerability audit + CodeQL.

Details live in [`documentation/backend/`](documentation/backend/) (English + Türkçe).

## Backend Documentation

Full bilingual guides (canonical source, also mirrored to the
[GitHub Wiki](https://github.com/mtsarac/mail-client/wiki)):

- English: [Backend Guide](documentation/backend/BACKEND_GUIDE.en.md) ·
  [Architecture](documentation/backend/ARCHITECTURE.en.md) ·
  [API Reference](documentation/backend/API_REFERENCE.en.md) ·
  [Development](documentation/backend/DEVELOPMENT.en.md) ·
  [Security](documentation/backend/SECURITY.en.md)
- Türkçe: [Backend Kılavuzu](documentation/backend/BACKEND_GUIDE.tr.md) ·
  [Mimari](documentation/backend/ARCHITECTURE.tr.md) ·
  [API Referansı](documentation/backend/API_REFERENCE.tr.md) ·
  [Geliştirme](documentation/backend/DEVELOPMENT.tr.md) ·
  [Güvenlik](documentation/backend/SECURITY.tr.md)

Flutter integration contract: [`FLUTTER_HANDOFF.md`](FLUTTER_HANDOFF.md).

## Backend Setup

Prerequisites:

- .NET 10 SDK
- Docker (for integration tests); PostgreSQL running locally on port `5432` for the API itself

Build:

```bash
dotnet build backend/MailClient.slnx
```

Run the full quality gate (also what CI runs):

```bash
dotnet restore backend/MailClient.slnx
dotnet build backend/MailClient.slnx --configuration Release --no-restore
dotnet test backend/MailClient.slnx --configuration Release --no-build
dotnet format backend/MailClient.slnx --verify-no-changes
```

`backend/Directory.Build.props` sets `TreatWarningsAsErrors` — the build fails on any warning.

Configure local database credentials:

```bash
cp backend/src/MailClient.Api/appsettings.Local.example.json backend/src/MailClient.Api/appsettings.Local.json
```

Edit `backend/src/MailClient.Api/appsettings.Local.json`. This file is ignored by git and overrides the committed Development config.

Run the API:

```bash
dotnet run --project backend/src/MailClient.Api/MailClient.Api.csproj
```

Open Swagger:

```text
http://localhost:5223/swagger
```

## Local LAN development

To expose the API to another machine on the same trusted local network, start the **`lan-http`** launch profile (listens on `http://0.0.0.0:5223`), find this machine's LAN IP (`ip -4 addr show | grep inet`), and use `http://<LAN-IP>:5223`. Check `http://<LAN-IP>:5223/health` for connectivity.

Details (CORS, Android cleartext, firewall): [`FLUTTER_HANDOFF.md`](FLUTTER_HANDOFF.md) and `documentation/backend/DEVELOPMENT.en.md`.

## Database

Apply EF migrations:

```bash
dotnet ef database update --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
```

If `dotnet ef` is missing:

```bash
dotnet tool install --global dotnet-ef
```

## Local Secrets

Do not commit real credentials. Use either `appsettings.Local.json` or environment variables:

```bash
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=PostaKoprusu;Username=postgres;Password=change-me"
export Jwt__Key="a-local-development-key-with-at-least-32-characters"
```

See `.env.example` and `backend/src/MailClient.Api/appsettings.Local.example.json` for examples.

## License

MIT
