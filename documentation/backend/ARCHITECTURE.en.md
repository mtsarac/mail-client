# Architecture Reference (English)

> Türkçe: [ARCHITECTURE.tr.md](ARCHITECTURE.tr.md)

## Identity

`MailAccount` is the authenticated principal. New backend has no application User, role, registration, approval, admin management, password hash, or UserId ownership. Each connected mailbox owns credentials, sessions, folders, mail, attachments, device registrations, send operations, and audit records.

Access JWT `sub` contains `MailAccountId`. Persistent login uses short-lived access JWTs plus rotating refresh sessions. PostgreSQL stores only refresh-token hashes. Mail-provider credentials are separate and encrypted through ASP.NET Core Data Protection.

## Layers

```text
MailClient.Domain          entities and enums only
        ↑
MailClient.Application     contracts, DTOs, discovery model
        ↑
MailClient.Infrastructure  EF Core, PostgreSQL, MailKit, DNS/HTTP, storage
        ↑
MailClient.Api             DI, JWT, middleware, endpoints, OpenAPI
```

Domain has no EF Core, MailKit, ASP.NET Core, or network dependencies. Application depends only on Domain. Infrastructure implements external connectivity and persistence. API composes services and maps HTTP contracts.

## Discovery and connection

Automatic discovery runs sequentially:

1. known provider catalog
2. DNS SRV
3. Thunderbird-style autoconfiguration
4. Microsoft Autodiscover
5. controlled secure hostname heuristics

Candidates use only supported secure modes and ports. Hostnames and all resolved addresses pass outbound-host validation before MailKit connects. TLS certificate validation remains enabled. Redirect following is disabled for discovery HTTP clients. First validated candidate wins.

Successful discovery is held temporarily server-side behind a random opaque ID. Failure returns HTTP 422 with `code=mail_discovery_failed` and `manualSetupAvailable=true`; no account or credential is persisted.

Manual setup bypasses discovery only. It still validates host syntax, DNS/IP safety, transport mode, TLS, IMAP authentication, and SMTP authentication before persistence. Automatic and manual paths converge on the same MailAccount/MailCredential/MailSession model.

## Persistence

New backend uses one clean Initial migration and defaults to database `mailclient_v2`. Legacy migrations remain under `legacy-backend/`.

Key constraints:

- unique normalized mailbox email
- unique credential authentication method per account
- unique refresh-token hash
- unique folder name per account
- unique UID per folder
- unique `(MailAccountId, IdempotencyKey)` send operation
- unique `(MailAccountId, Token)` device registration

## Mail scope and background work

Every protected endpoint obtains MailAccountId from `ICurrentMailAccount`. Folder, mail, attachment, device, and send queries include that account ID. Foreign IDs return 404.

A successful connection enqueues initial synchronization without waiting for full mailbox download. The in-process worker is deliberately small; provider-specific OAuth2 and full legacy incremental synchronization port remain separate work.

## Security and observability

Outbound validation blocks localhost, loopback, private, link-local, multicast, and unsafe destinations. Attachments resolve beneath configured storage root. SMTP fields reject CR/LF injection. HTTP limits partition by JWT account ID or pre-auth IP.

Serilog writes JSON application and HTTP streams to `logs/app-*.json` and `logs/http-*.json`. Correlation IDs are returned in `X-Correlation-ID`. Audit rows use nullable MailAccountId. Secret JSON keys are recursively redacted, including passwords, refresh tokens, OAuth material, client secrets, tokens, and credentials.

## Legacy classification

- REUSE/ADAPT: Data Protection pattern, MailKit connectivity approach, outbound host rules, attachment path confinement, structured logging conventions.
- REWRITE: identity, credentials, sessions, discovery, account connection, current-account context, schema, endpoints.
- REMOVE: User, roles/status, registration/login application accounts, approval/admin management, token version, UserId ownership.
