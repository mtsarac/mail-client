# Development

🇹🇷 [Türkçe](../tr/development.md)

## Requirements

.NET 10 SDK, PostgreSQL, Docker (optional, for compose and GreenMail).

## Run locally

With Docker (Postgres + GreenMail + migrations + API on `:8080`):

```bash
cp .env.example .env    # set Jwt__Key at minimum
docker compose up --build
```

Without Docker: start PostgreSQL, set `ConnectionStrings__Default` in `.env`,
apply migrations, then run the API:

```bash
dotnet ef database update --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
dotnet run --project backend/src/MailClient.Api
```

Runtime files: `data/` (attachments, data-protection keys) and `logs/` are git-ignored.

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
- Never modify a migration already merged to `master`; add a new one.
- Docker applies migrations via a separate `migrate` step, not at API startup.

## Conventions

- Keep the layering; provider-specific MailKit code stays in Infrastructure.
- Keep account-owned operations scoped to the authenticated `MailAccount`.
- Mail mutations stay remote-first and respect IMAP UID/UIDVALIDITY.
- Preserve API contracts and stable error codes unless the change is the point.
- Never commit `.env`, credentials, certificates, key rings or `data/`.
- Conventional commits (`feat:`, `fix:`, `docs:`, `chore:`), author is the
  repository owner only. Full rules: [AGENTS.md](../../../AGENTS.md).
