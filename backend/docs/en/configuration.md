# Configuration

🇹🇷 [Türkçe](../tr/configuration.md)

Two kinds of settings:

- **Static deployment config** — environment variables / `appsettings*.json`,
  read at startup (this page).
- **Runtime settings** — one versioned document in PostgreSQL, changed live
  through the management API (see below).

ASP.NET Core rules apply: `Section:Key` in JSON is `Section__Key` as an
environment variable. Copy `.env.example` to `.env` (never commit it).

## Static settings

| Key | Purpose | Default |
|-----|---------|---------|
| `ConnectionStrings__Default` | PostgreSQL connection string | local Postgres |
| `Jwt__Issuer`, `Jwt__Audience` | JWT issuer/audience | `MailClient` |
| `Jwt__Key` | HMAC key, **≥ 32 chars**; the built-in dev key is rejected outside Development | dev key |
| `Jwt__AccessTokenMinutes` | Access token lifetime | 15 |
| `Session__RefreshTokenLifetimeDays`, `Session__SlidingExpiration` | Refresh session | 180, true |
| `DataProtection__KeyPath` | Key ring directory | `data/protection-keys` |
| `DataProtection__CertificatePath` | PFX (no export password) protecting the key ring; **required in production** | unset |
| `Proxy__KnownProxies__N`, `Proxy__KnownNetworks__N` | Trusted reverse proxies (enables `X-Forwarded-*`) | none |
| `Storage__Provider` | `Local` (`data/attachments`) or `S3` | `Local` |
| `Storage__S3__Bucket/Region/ServiceUrl/Prefix/ForcePathStyle` | S3 target; empty key pair uses the ambient AWS credential chain | — |
| `Storage__S3__AccessKeyId/SecretAccessKey` | Optional explicit S3 credentials | unset |
| `Firebase__ProjectId`, `Firebase__CredentialsPath` | Push notifications; unset disables push | unset |
| `OAuth__Google__ClientId/ClientSecret/RedirectUris__N` | Google OAuth; unset disables it | unset |
| `OAuth__Microsoft__ClientId/ClientSecret/Tenant/RedirectUris__N` | Microsoft OAuth | tenant `organizations` |
| `OAuth__StateLifetimeMinutes` | OAuth `state` validity | 10 |
| `Management__Enabled`, `Management__ApiKey` | Management API (needs a key in production) | disabled |
| `HttpLogging__*` | Body logging (`Enabled`, `Max*BodyBytes`, `ExcludedPaths`) | on, 64 KiB |
| `Observability__*`, `OTEL_*` | Tracing, metrics, OTLP, Prometheus, log retention — see [OBSERVABILITY.md](../../OBSERVABILITY.md) | — |
| `MailDiscovery__*` | Mail server discovery options | — |

Docker-only variables (`POSTGRES_USER/PASSWORD/DB`, `DATAPROTECTION_CERT_HOST_PATH`,
ports) are documented in [DOCKER.md](../DOCKER.md).

Startup fails fast on: missing connection string, key path or certificate path in production, short
or development `Jwt__Key` in production, enabled Management API without key.

## Environments

| | Development | Production |
|--|-------------|------------|
| Swagger UI `/swagger` | yes | no |
| CORS | any origin | none |
| Private/LAN mail hosts | allowed | blocked (SSRF) |
| HTTPS redirection | no | yes (terminate TLS at a proxy and set `Proxy__*`) |
| HSTS | no | yes |
| Kestrel `Server` header | sent | suppressed |
| Error `detail` | exception text | hidden |

## Runtime settings

`GET/PUT /api/management/runtime-settings` (header `X-Management-Key`). `PUT`
replaces the whole document and must carry `expectedVersion`; a stale version is
rejected. Sections:

| Section | Controls |
|---------|----------|
| `Providers` | Per provider (Google, Microsoft, iCloud, Yahoo, Custom): enabled, new/existing accounts, password / app-password / OAuth2 |
| `Sync` | Enabled, poll interval (30 s), flag sync (120 s), max messages per run (100), concurrency per account/host, queue capacity, retry attempts/delays, failure threshold |
| `Limits` | Max attachment (25 MiB), per-message attachments (50 MiB), message (100 MiB), send body chars |
| `Search` | Max page size (100), max query length (200) |
| `Push` | Master switch and per-event toggles, mail preview |
| `Whitelist` | Allowlist enforcement (off), reconciliation interval (15 min), data retention grace (30 days) |
