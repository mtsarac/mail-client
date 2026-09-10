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
    MailClient.Infrastructure/  # EF Core, PostgreSQL, future mail adapters
```

The frontend team can place the Flutter project in `flutter_client/`. That path is already expected by the repository docs and `.gitignore`.

## Current Status

- Target framework: `net10.0`
- Database: PostgreSQL via EF Core
- Domain entities: `User`, `MailAccount`, `MailFolder`, `Mail`, `Attachment`, `DeviceToken`, `SyncState`
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
  "MaxMessageAttachmentBytes": 52428800
}
```

Set `MailSync:Enabled=false` for development or tests that should not start the hosted worker.

Do not commit real credentials. Use either `appsettings.Local.json` or environment variables:

```bash
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=PostaKoprusu;Username=postgres;Password=change-me"
```

See `.env.example` and `backend/src/MailClient.Api/appsettings.Local.example.json` for examples.

## License

MIT
