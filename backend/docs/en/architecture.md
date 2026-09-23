# Architecture

🇹🇷 [Türkçe](../tr/architecture.md)

## Layers

```
Api ──► Infrastructure ──► Application ──► Domain
 └────────────────────────► Application
```

| Project | Responsibility |
|---------|----------------|
| `MailClient.Domain` | Entities (`MailAccount`, `MailSession`, `MailFolder`, `Mail`, `Conversation`, `Attachment`, `SyncState`, `SendOperation`, `DeviceToken`, `AuditLog`, `AllowlistedEmail`, …) and enums. No dependencies. |
| `MailClient.Application` | Request/response contracts, options, service interfaces, runtime settings model, sync queue and retry policy. |
| `MailClient.Infrastructure` | EF Core (`AppDbContext`, migrations), MailKit IMAP/SMTP, discovery, OAuth, push (Firebase), attachment storage (local/S3), sync coordinator, all service implementations. |
| `MailClient.Api` | `Program.cs` wiring, middleware, endpoint groups (`Endpoints/`), JWT, telemetry, Swagger. |

Rule of thumb: provider-specific MailKit/IMAP/SMTP code stays in Infrastructure;
mail, account, send and sync behavior lives in services. Simple read projections
(account, folders, conversations, attachment download) and plain CRUD (device
tokens, allowlist entries) query `AppDbContext` directly in `Endpoints/*.cs`.

## Request pipeline

Middleware order in `Program.cs`:

1. `CorrelationMiddleware` — reads/creates `X-Correlation-ID`
2. `HttpBodyLoggingMiddleware` — redacted request/response body logs
3. Exception handler — maps failures to stable error codes (RFC 7807 problem details)
4. Forwarded headers (only when `Proxy:*` is configured)
5. HTTPS redirection (non-Development)
6. CORS (Development only)
7. Authentication (JWT bearer) → log enrichment
8. Rate limiter — 60 requests/min per account (or per IP when anonymous)
9. Authorization → endpoints

## Account scope

`MailAccount` is the security scope. The JWT `sub` claim is the account id;
`ICurrentMailAccount` exposes it and every account-owned query is filtered by it.

## Mail flow

- **Read path:** a background `SyncCoordinator` polls each account's folders over
  IMAP (interval, concurrency and retry limits come from runtime settings) and
  stores mail, conversations and attachments in PostgreSQL. Accounts sync
  concurrently up to `MaxConcurrentAccounts`; a slow account does not hold up the
  others. Endpoints read the local copy. `POST /api/folders/{id}/sync` queues an
  immediate sync (202).
- **Write path:** mutations (read, star, move, trash…) are *remote-first*: applied
  on the IMAP server using UID/UIDVALIDITY, then mirrored locally. A
  reconciliation service repairs drift.
- **Send:** drafts and direct sends go through SMTP; `Idempotency-Key` makes
  retries safe (`SendOperation`). Afterwards the Sent/Drafts folder is synced
  inline only if the account sync lock is free; otherwise a user-priority sync is
  queued (`InlineFolderSync`).
- **Push:** Firebase notifications for new mail, state changes, re-authentication
  and sync errors (each toggle is a runtime setting).

## Security safeguards

- Mail credentials encrypted with ASP.NET Data Protection (`ICredentialProtector`).
- SSRF protection for discovery and outbound hosts (`OutboundHostValidator`,
  `SsrfSafeDiscoveryHttpHandler`); private hosts only allowed in Development.
- Log redaction (`LogRedactor`); no addresses or mail IDs in metric labels.
- Sanitized HTML mail rendering; attachments stored via `IFileStorage`.
- Optional email allowlist (see [Authentication](authentication.md)).

## Runtime settings

Operational knobs (provider policy, sync, limits, search, push, allowlist) live in
the database as one versioned document, changed through the management API with
optimistic concurrency. Static deployment config (connection string, JWT key,
storage, observability) stays in environment/`appsettings`. See
[Configuration](configuration.md).

## Observability

Serilog JSON logs (`logs/app-*.json`, `logs/http-*.json`), OpenTelemetry
traces/metrics, optional Prometheus `/metrics`. Details:
[OBSERVABILITY.md](../../OBSERVABILITY.md).
