# Production

🇹🇷 [Türkçe](../tr/production.md)

Single-host, single-instance deployment via `docker-compose.prod.yml`: Postgres +
a one-shot `migrate` step + the API. No GreenMail. No TLS termination — that's a
reverse proxy's job. For local development instead, see
[Development](development.md).

## 1. Prerequisites

- Docker + Docker Compose on the deploy host.
- A reverse proxy (Caddy/nginx/Traefik) terminating TLS in front of the API.
  The compose file does not do this for you.
- A domain/certificate for that proxy.

## 2. Configure

```bash
cp .env.example .env
```

Fill in `.env`:

| Variable | Notes |
|----------|-------|
| `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB` | Used for the `postgres` container and interpolated into `ConnectionStrings__Default`. |
| `Jwt__Key` | 32+ random characters. Startup rejects the built-in development key outside `Development`. |
| `DATAPROTECTION_CERT_HOST_PATH` | Host path to a `.pfx` protecting the Data Protection key ring (compose maps it to `/run/secrets/dataprotection.pfx`, fixed by `DataProtection__CertificatePath` in the compose file). Generate one with an **empty export password** — the app loads it via `X509CertificateLoader.LoadPkcs12FromFile(path, password: "")`: `openssl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem -days 3650 -nodes -subj "/CN=mailclient-dataprotection"` then `openssl pkcs12 -export -out dataprotection.pfx -inkey key.pem -in cert.pem -passout pass:`. **Back this file up** — losing it makes every stored mail credential undecryptable; every account needs reauthentication. Same for the `protection-keys` volume. |
| `Proxy__KnownProxies__0` / `Proxy__KnownNetworks__0` | The reverse proxy's address(es), so `X-Forwarded-*` is trusted. **Required** if you're behind a reverse proxy: without it, `UseHttpsRedirection`/`UseHsts` see every request as plain HTTP (the proxy talks HTTP internally) and redirect it to HTTPS forever — an infinite redirect loop. |
| Any OAuth / Storage / Observability / Management values you need | See the comments in `.env.example` — written for exactly this deployment. Full reference: [Configuration](configuration.md). |

## 3. Run

```bash
docker compose -f docker-compose.prod.yml up --build -d
```

`migrate` runs once, applies pending EF Core migrations, exits `0`; `api` only
starts after that succeeds (`depends_on: condition: service_completed_successfully`).

To re-run migrations after pulling a new image (e.g. after an update):

```bash
docker compose -f docker-compose.prod.yml up --build migrate
docker compose -f docker-compose.prod.yml up --build -d api
```

## 4. What Production mode enables

Set automatically by `ASPNETCORE_ENVIRONMENT: Production` in the compose file
(see [Configuration → Environments](configuration.md#environments) for the full
table):

- Startup fails fast if `ConnectionStrings__Default`, `DataProtection__KeyPath`,
  or `DataProtection__CertificatePath` is missing, or `Jwt__Key` is short or the
  development default.
- Swagger UI and the open dev CORS policy are disabled.
- Private/LAN mail hosts are blocked (SSRF guard).
- `UseHttpsRedirection` and `UseHsts` are enabled — only correct once a
  reverse proxy is in front and `Proxy__KnownProxies`/`Proxy__KnownNetworks`
  are set (see the table above).
- The Kestrel `Server` response header is suppressed.
- Unhandled-exception responses omit exception details (only `code` and
  `correlationId`).

## 5. Firebase Cloud Messaging (optional)

New mail, mail-state-change, sync-error, and reauthentication-required push
notifications go out over FCM. Leave `Firebase__Enabled` unset (or `false`) to
skip this entirely — the API falls back to a no-op sender and everything else
works normally.

To enable it:

1. Firebase Console → Project settings → Service accounts → **Generate new
   private key**. Downloads a service-account JSON.
2. Set in `.env`:
   - `Firebase__Enabled=true`
   - `Firebase__ProjectId=<your-project-id>`
   - `FIREBASE_CREDENTIALS_HOST_PATH=` the host path to that JSON file (e.g.
     `./secrets/firebase-service-account.json`). Compose mounts it read-only
     into the container and fixes `Firebase__CredentialsPath` to
     `/run/secrets/firebase-service-account.json` — don't set that key
     yourself under Docker.
3. Redeploy: `docker compose -f docker-compose.prod.yml up --build -d api`.

Startup validates the file structurally (valid JSON, `service_account` type,
parseable RSA key) before the API accepts traffic; a malformed or missing
file fails fast with a clear error instead of silently disabling push. Mobile
clients register their FCM token via `POST /api/devices` after signing in.

## 6. Volumes and backups

See [DOCKER.md → Volumes](../DOCKER.md#volumes) for the full table
(`pgdata`, `protection-keys`, `attachments`, `logs`) and the backup command.

## 7. Known trade-offs

Accepted for a single-instance deployment — see
[DOCKER.md](../DOCKER.md#known-trade-offs-accepted-for-a-single-instance-deployment)
for the full list (migrator connection string visibility via `docker
inspect`, no CI image build/push yet, Data Protection application-name
scoping).

## Related

[Configuration](configuration.md) (full env var reference) ·
[Docker image layout](../DOCKER.md) (Dockerfile stages) ·
[Observability](../../OBSERVABILITY.md) (logs, metrics, tracing)
