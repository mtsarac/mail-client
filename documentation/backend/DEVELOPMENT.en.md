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

## Migrations

```fish
dotnet ef migrations list --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
dotnet ef migrations script --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
```

`legacy-backend/` is reference-only. Do not rewrite or apply its migrations as part of V2 work.

## Validation

Before commit: build with warnings as errors, run tests and format, inspect OpenAPI in Development, search new `backend/` for obsolete User concepts, and confirm `git diff -- frontend` is empty.
