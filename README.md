# Mail Client

Flutter client plus .NET 10 mail backend. Each connected mailbox is an independent authenticated principal; no application User or Admin account exists.

```text
Flutter -> MailClient API -> PostgreSQL (mailclient_v2)
                         -> IMAP/SMTP
                         -> local attachment storage
```

Current backend lives in `backend/`. Preserved reference implementation lives unchanged in `legacy-backend/`.

## Run

```fish
dotnet restore backend/MailClient.slnx
dotnet build backend/MailClient.slnx --configuration Release --no-restore
dotnet test backend/MailClient.slnx --configuration Release --no-build
dotnet run --project backend/src/MailClient.Api/MailClient.Api.csproj
```

Development Swagger: `/swagger`.

## Onboarding

Preferred flow: `POST /api/accounts/discover`, then `POST /api/accounts/connect`. If discovery returns `mail_discovery_failed` with `manualSetupAvailable=true`, use `POST /api/accounts/connect-manual`. Manual configuration receives the same SSRF, DNS/IP, TLS, IMAP, SMTP, and credential validation.

See [backend documentation](documentation/backend/). Flutter changes are separate work.

## License

MIT
