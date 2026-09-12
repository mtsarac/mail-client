# Development Guide (English)

> Türkçe: [DEVELOPMENT.tr.md](DEVELOPMENT.tr.md)

## Prerequisites

- .NET 10 SDK (`dotnet --version` → 10.x)
- PostgreSQL on `localhost:5432` for running the API
- Docker **only** for integration tests (Testcontainers pulls
  `postgres:16-alpine` + GreenMail); local development must **not** require Docker
- Rider (or VS Code + C#) for the `lan-http` profile

## Configure

```bash
cp backend/src/MailClient.Api/appsettings.Local.example.json \
   backend/src/MailClient.Api/appsettings.Local.json
```

Edit the copy (git-ignored). Minimum working values:

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=PostaKoprusu;Username=postgres;Password=change-me"
  },
  "Jwt": {
    "Key": "set-a-local-development-key-with-at-least-32-characters"
  }
}
```

Or use environment variables: `ConnectionStrings__Default`, `Jwt__Key`,
`Firebase__ProjectId`, `GOOGLE_APPLICATION_CREDENTIALS`, …

## Database

```bash
dotnet ef database update \
  --project backend/src/MailClient.Infrastructure \
  --startup-project backend/src/MailClient.Api
# missing tool? dotnet tool install --global dotnet-ef
```

## Run

```bash
dotnet run --project backend/src/MailClient.Api/MailClient.Api.csproj
```

- Swagger: `http://localhost:5223/swagger` (Development only)
- Health: `http://localhost:5223/health`, `http://localhost:5223/health/db`

Full quality gate (what CI runs):

```bash
dotnet restore backend/MailClient.slnx
dotnet build backend/MailClient.slnx --configuration Release --no-restore
dotnet test backend/MailClient.slnx --configuration Release --no-build
dotnet format backend/MailClient.slnx --verify-no-changes
```

`TreatWarningsAsErrors` is on: warnings fail the build.

## LAN development

Backend dev (Rider, this machine): start the **`lan-http`** profile
(`http://0.0.0.0:5223`, plain HTTP, no browser). Find the LAN IP
(`ip -4 addr show | grep inet`; DHCP changes it, so never commit it).
API is at `http://<LAN-IP>:5223`.

For LAN CORS, cleartext rules, and firewall troubleshooting:

- Dev never redirects HTTP→HTTPS and allows permissive CORS; production does
  the opposite.
- Only the API binds LAN; Postgres stays on localhost.
- Flutter Web works via the LAN URL thanks to dev CORS.
- Android LAN URLs are cleartext: allow it **debug-only**
  (`networkSecurityConfig`/`usesCleartextTraffic`), never in release.
- If localhost works but LAN times out: suspect firewall (allow TCP 5223 for
  the trusted subnet only) or guest-Wi-Fi client isolation.

## Firebase modes

- `Firebase:Enabled=false` (default): push is a no-op. Develop and test here.
- `Enabled=true`: needs `ProjectId` + service-account JSON
  (`Firebase:CredentialsPath` → `GOOGLE_APPLICATION_CREDENTIALS` → ADC
  well-known path → `GetApplicationDefault()`); startup fails fast otherwise.
  Never commit the JSON; mount it as a secret in production-like setups.

## Testing

| Project | Kind | How |
|---|---|---|
| `MailClient.Api.Tests` | full-stack integration | `WebApplicationFactory` + Testcontainers Postgres (+ GreenMail for mail flows): auth, isolation, token invalidation, mail endpoints, send, rate limits, LAN/config, persistence |
| `MailClient.Infrastructure.Tests` | unit | EF InMemory + fakes: validation, password policy, idempotent send, reconfiguration, device + push (fake gateway, no network), discovery, mapping, limits, storage, classifier, validator |
| `MailClient.Infrastructure.Tests` | Postgres semantics | Testcontainers: sync backlog/flags/availability, Postgres idempotency, migrations upgrade, race tests |

Run locally only what you need; the suite needs Docker for the
Testcontainers parts. **CI is where Testcontainers are validated.**
Do not claim coverage beyond this table.

## CI (no deploy pipeline)

`master` is protected: `build-test` + `format` are required checks.

### `ci.yml`

- `changes`: `dorny/paths-filter`: backend jobs run only when
  `backend/**`, `ci.yml`, or `.editorconfig` changed (a skip counts as
  passing for required checks).
- `build-test`: restore → Release build → **NuGet vulnerability audit**
  (`--vulnerable --include-transitive`, fails on findings) → full test run.
- `format`: `dotnet format --verify-no-changes`.
- Triggers: push to `master`, all PRs. `contents: read`, per-ref concurrency
  with cancel-in-progress.

### `codeql.yml`

C# analysis on backend-path pushes/PRs plus a **weekly Monday 06:00**
scheduled scan (`0 6 * * 1`). Results → Security tab.

## Production checklist

Enforced by code: strong `Jwt:Key`, HTTPS behind a proxy with
`Proxy:KnownProxies/KnownNetworks`, Data Protection X509 certificate +
persistent shared key ring, Firebase secret-mounted credentials,
`MailSecurity.None` rejected. Operationally add: DB backups,
logging/monitoring, proxy body limit ≈ 65M, shared attachment storage,
restrictive file permissions. Single instance unless storage + key ring are
shared (advisory locks already coordinate sync across instances).
