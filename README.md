# Mail Client

Mail Client is a Flutter + ASP.NET Core mail bridge for hosting-provider email accounts.

The Flutter app does not connect to IMAP or SMTP directly. It calls this backend over HTTP. The backend stores mail metadata in PostgreSQL and will handle mail sync, sending, attachments, authentication, and push notifications.

## Architecture

```text
Flutter app
  -> ASP.NET Core Web API
      -> PostgreSQL
      -> IMAP/SMTP hosting mailbox
      -> Firebase Cloud Messaging
```

Current backend structure:

```text
backend/
  MailClient.slnx
  src/
    MailClient.Api/             # HTTP API, Swagger, configuration
    MailClient.Application/     # application services and contracts
    MailClient.Domain/          # mail domain entities
    MailClient.Infrastructure/  # EF Core, PostgreSQL, MailKit mail adapters, local file storage
```

The frontend team can place the Flutter project in `flutter_client/`. That path is already expected by the repository docs and `.gitignore`.

## Current Status

- Target framework: `net10.0`
- Database: PostgreSQL via EF Core
- Domain entities: `User`, `MailAccount`, `MailFolder`, `Mail`, `Attachment`, `DeviceToken`, `SyncState`, `SyncSkippedUid`
- Auth: JWT (register/login, admin user lifecycle), Swagger UI with Bearer support in Development
- Mail accounts: per-user IMAP/SMTP configuration with encrypted credentials and connection testing
- Folder discovery: IMAP special-use mapping, Inbox/Sent sync-enabled by default
- API testing: Swagger UI in Development
- Background IMAP sync: active accounts and enabled folders poll every 30 seconds; attachment files are stored locally under `data/attachments`.

## Backend Setup

Prerequisites:

- .NET 10 SDK
- PostgreSQL running locally on port `5432`

Build:

```bash
dotnet build backend/MailClient.slnx
```

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
http://localhost:<shown-port>/swagger
```

## Database

The committed placeholder connection string points at:

```text
Host=localhost;Port=5432;Database=PostaKoprusu
```

After credentials are set, create and apply the first EF migration from the API project:

```bash
dotnet ef migrations add InitialSchema --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
dotnet ef database update --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
```

If `dotnet ef` is missing:

```bash
dotnet tool install --global dotnet-ef
```

## Local Secrets

## Mail Sync

```json
"MailSync": {
  "Enabled": true,
  "PollIntervalSeconds": 30,
  "MaxMessagesPerRun": 100,
  "MaxAttachmentBytes": 26214400,
  "MaxMessageAttachmentBytes": 52428800,
  "MaxMessageBytes": 104857600
}
```

Set `MailSync:Enabled=false` for development or tests that should not start the hosted worker.

Behavior:

- The worker polls active accounts and sync-enabled folders sequentially and serializes each folder across instances with a PostgreSQL advisory lock.
- `LastUid` checkpoints per folder; each mail insert commits atomically with its checkpoint.
- Transient failures (database, network, IMAP, storage) abort the run and retry the same UID on the next poll. Only permanently malformed, vanished, or oversized messages are recorded in `SyncSkippedUids` and skipped past.
- `MaxMessageBytes` is checked via an IMAP SIZE fetch before download; bodies are never fetched to determine size. Allowed messages are still fully parsed, so peak memory per message exceeds the raw size (parsed MIME plus `HtmlBody`/`TextBody` strings). Lower all three limits together if memory is constrained; the configuration validation requires `MaxMessageBytes >= MaxMessageAttachmentBytes >= MaxAttachmentBytes`.
- `HasAttachments` reflects attachments actually stored, not merely present in the MIME part list.

Attachments:

- Stored locally under `data/attachments/{accountId}/{mailId}/{attachmentId}` (override the root with `MailSync:AttachmentRoot`).
- Account deletion removes the whole account directory. Multi-instance deployments must share this storage (shared volume or object store); local disk only works for a single instance.

Data Protection:

- Mailbox passwords are encrypted with ASP.NET Core Data Protection; keys live at `DataProtection:KeyPath` (default `data/protection-keys`, relative paths resolve under the content root).
- Keys must survive restarts and be shared by all instances via a persistent shared volume. Use restrictive file permissions and protect keys at rest in production. Non-development startup logs a warning when this applies.

Reverse proxy:

- When running behind a reverse proxy, set `Proxy:KnownProxies` (IP list) and/or `Proxy:KnownNetworks` (CIDR list). Only then are `X-Forwarded-For` / `X-Forwarded-Proto` honored, and rate limiting sees the real client IP. Forwarded headers from untrusted networks are never trusted.

Email HTML:

- The backend stores raw email HTML. The Flutter client must treat it as hostile: no scripts, no unrestricted WebView JavaScript, remote resources blocked by default, links opened safely, no privileged JavaScript bridge exposed to email content.

Do not commit real credentials. Use either `appsettings.Local.json` or environment variables:

```bash
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=PostaKoprusu;Username=postgres;Password=change-me"
```

See `.env.example` and `backend/src/MailClient.Api/appsettings.Local.example.json` for examples.

## License

MIT
