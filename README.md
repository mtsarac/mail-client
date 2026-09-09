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

Do not commit real credentials. Keep secrets in user secrets, environment variables, or a local-only config file.

## License

MIT
