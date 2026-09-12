# Mail Client

Mail Client pairs a Flutter app with an ASP.NET Core backend for email accounts hosted through standard IMAP and SMTP servers.

The app talks to the backend over HTTP. The backend manages mail metadata in PostgreSQL, syncs mailboxes with IMAP, sends through each account's SMTP server, stores attachments locally, and sends new-mail notifications through Firebase Cloud Messaging.

## At a glance

- JWT authentication and account management
- Encrypted IMAP and SMTP credentials with connection testing
- Background mailbox sync with two-way read-state updates
- Idempotent sending, attachment support, and Sent-folder copies
- Push notifications for new Inbox mail
- Rate limiting, SSRF-safe host validation, secret scanning, dependency review, and CodeQL

```text
Flutter app -> ASP.NET Core API -> PostgreSQL
                               -> IMAP/SMTP mailbox
                               -> local attachment storage
                               -> Firebase Cloud Messaging
```

## Run the backend

You need the .NET 10 SDK, Docker for integration tests, and PostgreSQL on port `5432` to run the API.

```bash
dotnet build backend/MailClient.slnx
cp backend/src/MailClient.Api/appsettings.Local.example.json backend/src/MailClient.Api/appsettings.Local.json
dotnet run --project backend/src/MailClient.Api/MailClient.Api.csproj
```

Swagger is available at `http://localhost:5223/swagger`.

Run the same checks as CI with:

```bash
dotnet restore backend/MailClient.slnx
dotnet build backend/MailClient.slnx --configuration Release --no-restore
dotnet test backend/MailClient.slnx --configuration Release --no-build
dotnet format backend/MailClient.slnx --verify-no-changes
```

## Documentation

The tracked [backend documentation](documentation/backend/) is the source of truth and is mirrored to the [GitHub Wiki](https://github.com/mtsarac/mail-client/wiki). It includes English and Turkish guides for setup, architecture, API use, development, and security.

## License

MIT
