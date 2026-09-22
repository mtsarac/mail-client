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

See [Development](en/development.md) / [Geliştirme](tr/development.md).

## Production (`docker-compose.prod.yml`)

See [Production](en/production.md) / [Production (TR)](tr/production.md).

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
