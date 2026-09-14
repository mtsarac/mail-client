# MailAccount Principal Backend V2 Design

## Goal

Replace application-user identity with independently authenticated mailboxes while preserving proven mail behavior and keeping the old backend intact under `legacy-backend/`.

## Repository transition

- Rename tracked `backend/` to `legacy-backend/` without modifying its contents.
- Create a new .NET 10 `backend/` with Domain, Application, Infrastructure, API, and tests projects.
- Keep Flutter/frontend unchanged.
- Use a new PostgreSQL database connection default named `mailclient_v2`; never apply new migrations to the legacy database implicitly.

## Architecture

`MailAccount` is the sole authenticated principal. No application `User`, role, approval, registration, password hash, admin management, or `UserId` ownership exists in new backend.

Domain owns entities and enums only. Application owns DTOs, contracts, and use cases. Infrastructure owns EF Core/PostgreSQL, MailKit, DNS/HTTP discovery, Data Protection, storage, Firebase, and connectivity. API owns composition, JWT authentication, middleware, rate limiting, ProblemDetails, endpoints, and OpenAPI metadata. `Program.cs` delegates registration and endpoint mapping to focused extension methods.

## Persistence

Core entities:

- `MailAccount`: normalized unique email, working username, provider, authentication method, validated IMAP/SMTP configuration, discovery source, status, timestamps.
- `MailCredential`: one-to-one encrypted provider credential material, authentication method, provider, token metadata where applicable. Backend session refresh tokens never live here.
- `MailSession`: account ownership, SHA-256 refresh-token hash, optional device identifier, creation/activity/expiry/revocation/replacement timestamps.
- `MailFolder`, `Mail`, `Attachment`, `SyncState`, `SyncSkippedUid`, `SendOperation`, `DeviceToken`, `AuditLog`: adapted to direct `MailAccountId` ownership.

Normalized email has a unique index. Send idempotency uses `(MailAccountId, IdempotencyKey)`. Device token uniqueness uses `(MailAccountId, Token)`, allowing one physical token across accounts. New backend contains one clean `Initial` migration.

## Discovery

`IMailServerDiscoveryService` executes `IMailDiscoveryStrategy` implementations in fixed order:

1. known provider catalog
2. DNS SRV
3. standard autoconfiguration
4. Microsoft Autodiscover
5. controlled hostname heuristics

Each strategy returns normalized candidates. Candidate validation applies allowed transport/port policy, outbound-host validation, safe DNS resolution, blocked-address rejection, TLS certificate validation, reachability, and expected protocol checks. First validated candidate wins. Strategy failure continues to next strategy. No arbitrary hosts, ports, redirect chains, plaintext downgrade, or broad scans.

Successful discovery state is stored server-side in bounded in-memory cache with random opaque ID and finite expiry. Client receives provider, supported authentication methods, and manual fallback availability, not modifiable server configuration.

All-strategy failure returns HTTP 422 ProblemDetails with `code=mail_discovery_failed` and `manualSetupAvailable=true`. It persists no account or credential.

## Connection

Automatic `POST /api/accounts/connect` consumes intact, unexpired discovery state plus Password or AppSpecificPassword material. Manual `POST /api/accounts/connect-manual` accepts email, username, explicit secure IMAP/SMTP endpoints, and supported credential material.

Manual mode bypasses discovery only. Both flows enforce the same host, DNS, IP, TLS, protocol, IMAP authentication, and SMTP authentication checks before persistence. Username attempts are deterministic: full email, local part, then provider-supplied format without duplicates. Working username is persisted.

After validation, both paths call one account connection use case: create or reuse account by normalized email, encrypt credential, discover/persist folders, create refresh session, issue access and refresh tokens, commit, and enqueue initial sync. Full mailbox download never blocks response.

## Authentication and sessions

Access JWT `sub` equals `MailAccountId`; no role, User status, or token version claims. Configurable short lifetime applies.

Refresh tokens use cryptographically secure random bytes. Only SHA-256 hashes are stored. Refresh rotates token in one transaction, revokes old session, links replacement, updates activity, and rejects expired/revoked/reused tokens. `/api/auth/refresh` is anonymous. `/api/auth/logout` revokes current refresh session. Account deletion remains separate.

`ICurrentMailAccount` resolves `MailAccountId` once from authenticated principal. Application services never trust caller-supplied account IDs for ownership.

## Mail behavior

Adapt legacy MailKit connection helpers, folder discovery/classification, UID/UIDVALIDITY handling, skipped UID and cursor logic, incremental flag sync, MIME mapping, attachment limits/storage, SMTP header safety, Sent-folder copy, PostgreSQL advisory locks, and send idempotency.

Every mail/folder/attachment/send query scopes directly to current `MailAccountId`. Foreign IDs return 404. Background sync selects active accounts, decrypts their credential, and differentiates transient connectivity failures from permanent authentication rejection. Permanent rejection marks `NeedsReauthentication`; cached mail remains readable.

## Security

Reuse and strengthen legacy outbound-host validation, Data Protection key persistence/X509 production protection, attachment path confinement, request/body limits, CORS and proxy policy, SMTP/MIME limits, and rate limiting. Validate resolved destination at connection time to reduce DNS-rebinding risk. Secure modes are `SslOnConnect` and `StartTls`; plaintext requires an explicit disabled-by-default policy.

Pre-auth discovery/connect/refresh limits partition primarily by remote IP. Authenticated limits partition by MailAccountId. Every external operation accepts `CancellationToken` and finite configured timeout.

## Errors and observability

RFC 7807 responses expose stable codes, never raw MailKit/network exceptions. Expected provider/discovery failures map to 400/401/409/422/429/502/503 rather than 500.

Preserve JSON Serilog application and HTTP logs, correlation IDs, exception logging, bounded body capture, and nested JSON. Redact password, appSpecificPassword, accessToken, refreshToken, providerRefreshToken, authorizationCode, codeVerifier, clientSecret, token, credential, and secret recursively.

Audit rows use nullable `MailAccountId` for pre-auth events and never include secret material. Discovery logs record domain, strategy, candidate host/port, outcome, elapsed time, and correlation ID.

## API

Implement account-scoped endpoints requested in task: discovery, automatic/manual connect, refresh/logout, current account/delete, folders/refresh/sync, mail list/detail/read/send, attachment download, devices, and health. Each endpoint includes name, summary, description, request/response metadata, ProblemDetails variants, tags, and safe examples. Swagger explains automatic flow and manual fallback security.

## Tests

Use xUnit with unit tests for ordering, candidate validation, username fallback, refresh hashing/rotation/revocation, redaction, credential protection, status policy, and mail algorithms. API/integration tests verify failed discovery persists nothing, manual rejection persists nothing, JWT subject, account isolation, ProblemDetails contracts, and OpenAPI metadata. Preserve adapted PostgreSQL concurrency and GreenMail coverage where environment supports them.

## Legacy classification

- REUSE: low-level MIME normalization and pure mail algorithms where ownership-free.
- ADAPT: MailKit helpers, sync, folders, query/read/send, storage, Firebase, Data Protection, outbound host validation, Serilog/HTTP logging, audit, Swagger/ProblemDetails.
- REWRITE: identity, account connection, discovery, credentials, sessions, current-account context, EF model/migration, endpoint ownership.
- REMOVE: User, roles/status, registration/login password accounts, approval/admin management, User session validation, old account CRUD onboarding, UserId ownership.

## Deferred

Provider-specific OAuth2 implementations and Flutter integration are deferred. OAuth-ready enums/contracts and encrypted credential shape are included; no fake generic OAuth endpoints or Sign in with Apple behavior are implemented.

Automatic discovery failure does not mean provider unsupported. Users may configure IMAP/SMTP manually through fallback flow. Manual configuration receives identical SSRF, TLS, protocol, and credential validation.
