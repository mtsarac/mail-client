# Docker packaging

Backend only (`backend/`). Single-host, single-instance target — no multi-replica
orchestration here.

## Image layout (`backend/Dockerfile`)

Three stages, built from a `build` stage that publishes the API and produces a
self-contained EF Core **migration bundle**:

- `runtime` — the API (`mcr.microsoft.com/dotnet/aspnet:10.0`), runs as non-root,
  listens on `8080`, healthcheck hits `/health/live`.
- `migrator` — just the migration bundle executable, no SDK/ASP.NET. Applies
  pending EF Core migrations and exits. Run it as a separate step, never inside
  the API process (avoids running `Database.Migrate()` at API startup, which the
  project's migration strategy
  treats as a deliberate, reviewable step rather than something silently baked
  into app boot).

## Local development (`docker-compose.yml`)

Postgres + GreenMail (fake IMAP/SMTP for integration tests) + `migrate` (one-shot)
+ `api`.

```bash
cp .env.example .env   # fill in Jwt__Key at minimum
docker compose up --build
```

Host ports default to `5432` (Postgres), `3143`/`3025` (GreenMail IMAP/SMTP),
`8080` (API) — override via `POSTGRES_PORT` / `GREENMAIL_IMAP_PORT` /
`GREENMAIL_SMTP_PORT` / `API_PORT` in `.env` if those collide with something
already running on your machine.

## Production (`docker-compose.prod.yml`)

Postgres + `migrate` + `api`. No GreenMail. No TLS termination — put a reverse
proxy (Caddy/nginx/Traefik) in front and point it at `api:8080`; set
`Proxy__KnownProxies`/`Proxy__KnownNetworks` in `.env` so forwarded-header
handling trusts it.

```bash
cp .env.example .env
```

Fill in `.env`:

- `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB` — used for both the
  `postgres` container and the interpolated `ConnectionStrings__Default`.
- `Jwt__Key` — 32+ random characters.
- `DataProtection__CertificatePath` is fixed by compose to
  `/run/secrets/dataprotection.pfx` inside the container; set
  `DATAPROTECTION_CERT_HOST_PATH` to the `.pfx` file on the host. Generate one
  with **no export password** (the app loads it via
  `X509CertificateLoader.LoadCertificateFromFile(path)`, no password argument):
  ```bash
  openssl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem -days 3650 -nodes -subj "/CN=mailclient-dataprotection"
  openssl pkcs12 -export -out dataprotection.pfx -inkey key.pem -in cert.pem -passout pass:
  ```
  **Back this file up.** Losing it makes every stored mail credential
  undecryptable — every account needs reauthentication. Same for the
  `protection-keys` named volume.
- Any OAuth/Storage/Observability/Management values you need — see the comments
  in `.env.example`, they're written for exactly this deployment.

```bash
docker compose -f docker-compose.prod.yml up --build -d
```

`migrate` runs once, applies pending migrations, exits `0`; `api` only starts
after that succeeds (`depends_on: condition: service_completed_successfully`).
To re-run migrations after pulling a new image (e.g. after an update):

```bash
docker compose -f docker-compose.prod.yml up --build migrate
docker compose -f docker-compose.prod.yml up --build -d api
```

## Volumes

| Volume | Contents | Notes |
|---|---|---|
| `pgdata` | Postgres data | Postgres 18 stores this at `/var/lib/postgresql` (not `.../data` — that's the 17-and-earlier layout). |
| `protection-keys` | Data Protection key ring | Back up. Losing it forces reauthentication on every account. |
| `attachments` | Local attachment storage (`Storage__Provider=Local`, the default) | Switch to `Storage__Provider=S3` with an external bucket if you outgrow single-host local disk; no MinIO container is included here since prod is meant to point at a real managed S3-compatible endpoint when that's needed. |
| `logs` | Serilog file sink output | |

Inspect/back up a named volume without stopping anything:

```bash
docker run --rm -v mailclient_pgdata:/data -v "$PWD":/backup alpine \
  tar czf /backup/pgdata-$(date +%F).tar.gz -C /data .
```

## Known trade-offs (accepted for a single-instance deployment)

- The migrator's DB connection string is passed as a container `command` arg,
  which is visible via `docker inspect`/`docker top` on the host. Acceptable on
  a single trusted host; wouldn't be on a shared one.
- No image build/push wired into CI — build locally on the deploy host for now.
- `DataProtection` doesn't call `SetApplicationName`; this matters for sharing
  a key ring across instances with different content roots, which isn't the
  case here since the Dockerfile fixes `WORKDIR /app` for every build. Revisit
  if this ever moves to multiple replicas.
