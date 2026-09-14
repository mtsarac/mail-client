# Backend Guide (English)

## Model

MailAccount is the authenticated principal and directly owns MailCredential, MailSession, MailFolder, Mail, Attachment, DeviceToken, SendOperation, and AuditLog. Application User, roles, registration, approval, and admin management were removed.

## Onboarding

1. Client submits email to `POST /api/accounts/discover`.
2. Backend tries known provider, DNS SRV, autoconfig, Microsoft Autodiscover, and safe host heuristics in order.
3. First candidate passing outbound-host and transport validation is stored temporarily behind opaque discovery ID.
4. Client submits credential to `POST /api/accounts/connect`.
5. Backend validates IMAP and SMTP auth, creates or reuses normalized account, encrypts credential, creates refresh session, returns JWT plus refresh token, and queues initial sync.

If discovery fails, HTTP 422 returns `mail_discovery_failed` and `manualSetupAvailable=true`. Nothing is persisted. Failure does not mean provider unsupported.

Manual fallback uses `POST /api/accounts/connect-manual`. Manual mode bypasses discovery only. It never bypasses SSRF, DNS/IP safety, TLS certificate checks, protocol checks, IMAP auth, or SMTP auth.

## Sessions

JWT `sub` is MailAccountId and access lifetime is configurable. Refresh tokens are random, stored as SHA-256 hashes, rotated at `/api/auth/refresh`, and revoked at `/api/auth/logout`. Provider refresh tokens belong to MailCredential and are never mixed with backend sessions.

## Persistence and migration

New backend has one clean Initial EF Core migration and uses `mailclient_v2` by default. Legacy migrations remain untouched in `legacy-backend/`. Credentials use Data Protection encryption. Normalized email, send idempotency, folder UID, device ownership, and session hashes have database uniqueness constraints.

## Mail and ownership

Protected endpoints derive account from JWT through `ICurrentMailAccount`. Caller-supplied account IDs are not trusted. Foreign mail/folder/attachment/device IDs return 404. Send idempotency is scoped by MailAccountId and key. Attachment paths cannot escape storage root.

Successful connection queues initial sync asynchronously. Cached mail remains account-scoped. Full production port of legacy incremental synchronization, Sent-folder append, Firebase delivery, and provider-specific OAuth2 remains deferred and must not be represented as complete.

## Errors and observability

API uses ProblemDetails with stable codes. Correlation IDs use `X-Correlation-ID`. Serilog writes JSON application and HTTP logs. Audit rows use nullable MailAccountId. Secret values are recursively redacted and request bodies are not logged by V2 middleware.

## Client flow

```text
email -> discover -> connect
           |
           +-- failure -> manual settings -> connect-manual
```

Frontend implementation is separate.
