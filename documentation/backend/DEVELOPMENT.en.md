# Backend Development (English)

## Requirements

- .NET 10 SDK
- PostgreSQL for runtime and migration integration

## Commands

```fish
dotnet restore backend/MailClient.slnx
dotnet build backend/MailClient.slnx --configuration Release --no-restore
dotnet test backend/MailClient.slnx --configuration Release --no-build
dotnet format backend/MailClient.slnx --verify-no-changes
```

Run API:

```fish
dotnet run --project backend/src/MailClient.Api/MailClient.Api.csproj
```

Use environment-specific secrets for `ConnectionStrings__Default` and `Jwt__Key`. Default development database name is `mailclient_v2`; never point V2 at legacy database accidentally.

## Integration tests

PostgreSQL and GreenMail integration tests are opt-in so a plain `dotnet test` never guesses at local service credentials. Enable them explicitly:

```fish
set -x MAILCLIENT_TEST_POSTGRES_ADMIN "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=change-me"
set -x MAILCLIENT_TEST_GREENMAIL 1
dotnet test backend/MailClient.slnx --configuration Release --no-build
```

- `MAILCLIENT_TEST_POSTGRES_ADMIN` — admin DSN. The fixture creates and migrates `mailclient_v2_tests`; it never touches `mailclient_v2`.
- `MAILCLIENT_TEST_GREENMAIL=1` — enables GreenMail tests. Override `MAILCLIENT_TEST_GREENMAIL_HOST`, `..._IMAP` (3143), `..._USER` (`test@localhost`), `..._PASSWORD` (`test123`) when not using the default container.
- `MAILCLIENT_SKIP_INTEGRATION=1` — skip integration tests even when the variables above are set.

GreenMail integration tests cover the real IMAP sync core (UID mapping, seen flags, UIDVALIDITY), production `MailKitRemoteMailFolder.AppendAsync`, and the production connection helper refusing the plaintext loopback endpoint. PostgreSQL integration tests cover unique-index enforcement, atomic refresh-token rotation under concurrency, and advisory-locked send idempotency.

## Migrations

```fish
dotnet ef migrations list --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
dotnet ef migrations script --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
```

`legacy-backend/` is reference-only. Do not rewrite or apply its migrations as part of V2 work.

## Validation

Before commit: build with warnings as errors, run tests and format, inspect OpenAPI in Development, search new `backend/` for obsolete User concepts, and confirm no frontend paths were introduced.
