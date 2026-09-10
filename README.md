# Mail Client

Mail Client is a Flutter + ASP.NET Core mail bridge for hosting-provider email accounts.

The Flutter app does not connect to IMAP or SMTP directly. It calls this backend over HTTP. The backend stores mail metadata in PostgreSQL, runs a background IMAP sync worker, and sends mail through the user's own SMTP server. Push notifications are planned but not implemented yet.

## Architecture

```text
Flutter app
  -> ASP.NET Core Web API
      -> PostgreSQL (mail metadata, identity, sync state)
      -> IMAP/SMTP hosting mailbox (MailKit)
      -> local file storage (attachments)
```

Planned: Firebase Cloud Messaging for push notifications.

Current backend structure:

```text
backend/
  MailClient.slnx
  src/
    MailClient.Api/             # HTTP API, Swagger, configuration
    MailClient.Application/     # application services and contracts
    MailClient.Domain/          # mail domain entities
    MailClient.Infrastructure/  # EF Core, PostgreSQL, MailKit mail adapters, local file storage
  tests/
    MailClient.Api.Tests/            # full-stack integration tests (Testcontainers PostgreSQL)
    MailClient.Infrastructure.Tests/ # unit tests + PostgreSQL sync semantics (Testcontainers)
```

The frontend team can place the Flutter project in `flutter_client/`. That path is already expected by the repository docs and `.gitignore`.

## Current Status

- Target framework: `net10.0`
- Database: PostgreSQL via EF Core (migrations applied on startup in tests, `dotnet ef database update` locally)
- Domain entities: `User`, `MailAccount`, `MailFolder`, `Mail`, `Attachment`, `DeviceToken`, `SyncState`, `SyncSkippedUid`
- Auth: JWT (register/login, admin user lifecycle, token-version invalidation), Swagger UI with Bearer support in Development
- Mail accounts: per-user IMAP/SMTP configuration with encrypted credentials and connection testing
- Folder discovery: IMAP special-use mapping, Inbox/Sent sync-enabled by default
- Background IMAP sync: active accounts and enabled folders poll every 30 seconds; per-folder UIDVALIDITY checkpoints, durable poison-skip records, advisory-lock serialization across instances; attachment files are stored locally under `data/attachments`
- Read/unread is two-way with IMAP as the source of truth: new mail maps `\Seen` to `IsRead`, `PATCH /api/mails/{id}/read` applies flag changes to IMAP before touching local state, and background flag reconciliation (default every 120 seconds, `FlagSyncIntervalSeconds`) syncs external flag changes without downloading bodies
- Mail APIs: `GET /api/mails` (unified Inbox/Sent with account/folder/type filters, stable pagination), `GET /api/mails/{id}` (full body plus attachment metadata, never storage paths), `GET /api/mails/{mailId}/attachments/{attachmentId}` (streamed download, fully ownership-scoped)
- SMTP sending: `POST /api/mail-accounts/{accountId}/send` (multipart form, recipient validation, outgoing size limits); SMTP success is never retried — if `SaveSentCopy` is on, the same message is IMAP-APPENDed to the discovered Sent folder and the response reports `sent` vs `sentCopySaved` separately
- Rate limiting: fixed-window limiter on auth endpoints per client IP and on mail operations per user
- Tests: xUnit; integration tests run against throwaway PostgreSQL via Testcontainers

## Backend Setup

Prerequisites:

- .NET 10 SDK
- Docker (for integration tests); PostgreSQL running locally on port `5432` for the API itself

Build:

```bash
dotnet build backend/MailClient.slnx
```

Run the full quality gate (also what CI runs):

```bash
dotnet restore backend/MailClient.slnx
dotnet build backend/MailClient.slnx --configuration Release --no-restore
dotnet test backend/MailClient.slnx --configuration Release --no-build
dotnet format backend/MailClient.slnx --verify-no-changes
```

`backend/Directory.Build.props` sets `TreatWarningsAsErrors` — the build fails on any warning.

Configure local database credentials:

```bash
cp backend/src/MailClient.Api/appsettings.Local.example.json backend/src/MailClient.Api/appsettings.Local.json
```

Edit `backend/src/MailClient.Api/appsettings.Local.json`. This file is ignored by git and overrides the committed Development config.

Run the API:

```bash
dotnet run --project backend/src/MailClient.Api/MailClient.Api.csproj
```

Open Swagger:

```text
http://localhost:5223/swagger
```

## Database

The committed placeholder connection string points at:

```text
Host=localhost;Port=5432;Database=PostaKoprusu
```

Apply EF migrations:

```bash
dotnet ef database update --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
```

If `dotnet ef` is missing:

```bash
dotnet tool install --global dotnet-ef
```

## Local Secrets

Do not commit real credentials. Use either `appsettings.Local.json` or environment variables:

```bash
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=PostaKoprusu;Username=postgres;Password=change-me"
export Jwt__Key="a-local-development-key-with-at-least-32-characters"
```

See `.env.example` and `backend/src/MailClient.Api/appsettings.Local.example.json` for examples.

## Mail Sync

```json
"MailSync": {
  "Enabled": true,
  "PollIntervalSeconds": 30,
  "FlagSyncIntervalSeconds": 120,
  "MaxMessagesPerRun": 100,
  "MaxAttachmentBytes": 26214400,
  "MaxMessageAttachmentBytes": 52428800,
  "MaxMessageBytes": 104857600,
  "MaxSendBodyChars": 1000000
}
```

Set `MailSync:Enabled=false` for development or tests that should not start the hosted worker.

Behavior:

- The worker polls active accounts and sync-enabled folders sequentially and serializes each folder across instances with a PostgreSQL advisory lock. Folders that disappeared from the last successful discovery are marked unavailable: their mail is kept, but they are never synchronized until they reappear (reappearing restores availability without changing the user's sync preference).
- `LastUid` checkpoints per folder; each mail insert commits atomically with its checkpoint.
- New-UID discovery is bounded per run (`MaxMessagesPerRun`) using windowed UID-range SEARCH against `UidNext`, so a huge initial backlog never materializes as one huge UID list. MailKit exposes no server-side SEARCH LIMIT; sparse UID ranges may take several small roundtrips instead.
- Transient failures (database, network, IMAP, storage) abort the run and retry the same UID on the next poll. Only permanently malformed, vanished, or oversized messages are recorded in `SyncSkippedUids` and skipped past.
- `MaxMessageBytes` is checked via an IMAP SIZE fetch before download; bodies are never fetched to determine size. Allowed messages are still fully parsed, so peak memory per message exceeds the raw size (parsed MIME plus `HtmlBody`/`TextBody` strings). Lower all three limits together if memory is constrained; the configuration validation requires `MaxMessageBytes >= MaxMessageAttachmentBytes >= MaxAttachmentBytes`.
- `HasAttachments` reflects attachments actually stored, not merely present in the MIME part list.
- IMAP is the source of truth for read/unread state. New mail maps `\Seen` to `IsRead` from the same bounded summary fetch (no extra roundtrip). `PATCH /api/mails/{id}/read` adds/removes `\Seen` on the server first and only then updates the local row; a UIDVALIDITY change returns `409 Conflict` and leaves both sides untouched. Flag reconciliation runs at most every `FlagSyncIntervalSeconds` per folder: FLAGS-only batched fetch, bounded chunks, only changed rows updated, `LastFlagSyncAt` stamped only after a fully successful pass. Failures never touch `LastUid` or checkpoints.

## Mail APIs

All mail endpoints require JWT and are strictly scoped to the caller's own accounts. Admin role does not bypass ownership.

```text
GET   /api/mails?folderType=Inbox&page=1&pageSize=30
GET   /api/mails?accountId={accountId}&folderType=Sent&page=1&pageSize=30
GET   /api/mails?folderId={folderId}&page=1&pageSize=30
GET   /api/mails/{id}
GET   /api/mails/{mailId}/attachments/{attachmentId}
PATCH /api/mails/{id}/read            { "isRead": true }
```

- Pagination: `page >= 1`, `1 <= pageSize <= 100`, ordered by `ReceivedAt DESC, Id DESC`. The list never returns full bodies.
- List summaries expose metadata only; detail adds `BodyHtml`/`BodyText` plus attachment metadata (`Id`, `FileName`, `ContentType`, `SizeBytes`, `IsInline`, `ContentId`). `StoragePath` and credentials are never exposed.
- Attachment download streams the file from local storage (no Base64, no full buffering). The `mailId`/`attachmentId` chain is verified end to end; a missing file returns a controlled 404.

## Sending mail

```text
POST /api/mail-accounts/{accountId}/send    multipart/form-data
```

Fields: `toAddress`, `subject`, `bodyHtml` and/or `bodyText` (at least one required), up to 20 `attachments` files. Recipients are validated with MimeKit parsing (local and domain parts required). Subjects are truncated to 500 characters; each body is rejected past `MaxSendBodyChars`. Outgoing attachment filenames/content types are normalized (255/150 chars, safe fallback); filenames never touch the filesystem. Outgoing attachments reuse the configured `MaxAttachmentBytes` / `MaxMessageAttachmentBytes` limits, and uploaded streams are disposed after the operation without extra in-memory copies.

Upload limits end to end: Kestrel's max request body and the multipart limit are configured at startup from `MaxMessageAttachmentBytes` plus both bodies at worst-case UTF-8 plus 1 MiB framing headroom, so the server never accepts a request the application would later reject for size. Reverse proxies in front of the API need a matching body limit (e.g. `client_max_body_size` ≈ 65M with defaults).

Idempotent sending: pass `Idempotency-Key: <unique-value>` to deduplicate HTTP retries per user. State machine (`SendOperations`, unique per user+key): `InProgress → Failed` (SMTP never accepted; same key may retry) or `→ Sent → SentWithCopy`. A retry after `Sent`/`SentWithCopy` replays the stored result and never touches SMTP again; a busy key returns `409`; the same key with a different account/recipients/subject/body/attachments returns `409`. Fingerprints hash metadata only, never attachment contents or passwords.

Critical semantics: **SMTP success = sent.** The message is sent exactly once through the account's own SMTP server (same SSRF/DNS/TLS protections as all other mail traffic). Afterwards:

- `SaveSentCopy == false` → done.
- `SaveSentCopy == true` → the same `MimeMessage` is IMAP-APPENDed to the persisted Sent folder (no second SMTP send, no fake local row; normal Sent sync imports it later).
- APPEND failure or missing Sent folder → `sent: true, sentCopySaved: false` with a warning, so the client never resends:

```json
{
  "sent": true,
  "sentCopySaved": false,
  "warning": "Message was sent, but the Sent copy could not be stored."
}
```

Attachments:

- Stored locally under `data/attachments/{accountId}/{mailId}/{attachmentId}` (override the root with `MailSync:AttachmentRoot`).
- Account deletion removes the whole account directory. Multi-instance deployments must share this storage (shared volume or object store); local disk only works for a single instance.

Data Protection:

- Mailbox passwords are encrypted with ASP.NET Core Data Protection; keys live at `DataProtection:KeyPath` (default `data/protection-keys`, relative paths resolve under the content root).
- Keys must survive restarts and be shared by all instances via a persistent shared volume. Use restrictive file permissions and protect keys at rest in production. Non-development startup logs a warning when this applies.

Reverse proxy:

- When running behind a reverse proxy, set `Proxy:KnownProxies` (IP list) and/or `Proxy:KnownNetworks` (CIDR list). Only then are `X-Forwarded-For` / `X-Forwarded-Proto` honored, and rate limiting sees the real client IP. Forwarded headers from untrusted networks are never trusted.

Email HTML:

- The backend stores raw email HTML. The Flutter client must treat it as hostile: no scripts, no unrestricted WebView JavaScript, remote resources blocked by default, links opened safely, no privileged JavaScript bridge exposed to email content.

## License

MIT
