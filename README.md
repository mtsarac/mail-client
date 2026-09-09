# Mail Client

Backend-first mail bridge project for a Flutter mail client.

The Flutter app talks only to this ASP.NET Core backend. The backend owns PostgreSQL persistence and will later handle IMAP/SMTP access, mail sync, attachments, authentication, and push notifications.

## Current Status

- ASP.NET Core `net10.0` backend solution under `backend/`
- EF Core PostgreSQL persistence foundation
- Core mail entities: `Mail`, `Attachment`, `DeviceToken`, `SyncState`, `SentMail`
- Swagger UI enabled in Development for API testing
- Frontend can be added under `flutter_client/`

## Repository Layout

```text
backend/
  MailClient.slnx
  src/
    MailClient.Api/
    MailClient.Application/
    MailClient.Domain/
    MailClient.Infrastructure/
flutter_client/
  # Flutter app goes here when added by the frontend team
docs/
  superpowers/plans/
```

## Backend Setup

Build the backend:

```bash
dotnet build backend/MailClient.slnx
```

Run the API:

```bash
dotnet run --project backend/src/MailClient.Api/MailClient.Api.csproj
```

Open Swagger at the URL shown by `dotnet run`, then visit `/swagger`.

## Local Configuration

Development PostgreSQL is expected on `localhost:5432`.

Use one of these local-only options for real credentials:

```bash
cp backend/src/MailClient.Api/appsettings.Local.example.json backend/src/MailClient.Api/appsettings.Local.json
```

Then edit `appsettings.Local.json`. It is ignored by git and overrides `appsettings.Development.json`.

Or use an environment variable:

```bash
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=PostaKoprusu;Username=postgres;Password=change-me"
```

Do not commit real credentials.

## License

MIT
