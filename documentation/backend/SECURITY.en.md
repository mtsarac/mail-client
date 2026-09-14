# Security Reference (English)

> Türkçe: [SECURITY.tr.md](SECURITY.tr.md)

## Authentication

MailAccount is the principal. JWT validates issuer, audience, signing key, and lifetime; `sub` is MailAccountId. Access lifetime is configurable and stays short-lived. Refresh sessions live in the `Session` configuration section (`RefreshTokenLifetimeDays`, default 180, and `SlidingExpiration`, default true). Sliding rotation renews the session lifetime on every successful refresh; fixed mode keeps the original absolute expiry, so a session can never outlive its initial bound. Refresh tokens are cryptographically random, stored only as SHA-256 hashes, rotated with a single-winner claim under concurrency, and revoked on logout. Provider credentials are separate from session tokens and encrypted with ASP.NET Core Data Protection.

Password and AppSpecificPassword are implemented. OAuth2 storage shape exists, but provider authorization/callback integrations are deferred; no generic fake OAuth flow exists.

## Account isolation

Protected APIs derive MailAccountId from `ICurrentMailAccount`, never request account IDs. Database reads and writes scope folder, mail, attachment, device, and send records by MailAccountId. Foreign IDs return 404.

## Discovery and SSRF

Discovery order is known provider, DNS SRV, autoconfig, Microsoft Autodiscover, then controlled heuristics. HTTP discovery clients route through the SSRF-safe handler: the outbound host is resolved through `OutboundHostValidator` and the TCP connection is pinned to the validated IP, so TLS SNI and certificate validation still use the original URI hostname. Automatic redirects stay disabled. Candidate and manual hosts pass DNS and address validation before protocol authentication. Connections use the validated IP with the original hostname retained for TLS certificate/SNI validation, preventing a second DNS lookup and DNS-rebinding TOCTOU. Localhost, loopback, private IPv4, link-local, multicast, IPv6 unique-local, and unsafe destinations are rejected. Only `SslOnConnect` and `StartTls` are modeled. MailKit certificate validation is not disabled.

Manual setup bypasses discovery only. It cannot bypass host validation, DNS/IP checks, TLS, IMAP auth, or SMTP auth.

## Stored data

Credential material is encrypted before persistence and never returned. Refresh tokens are stored hashed. Attachment paths are canonicalized and required to remain below storage root. Send fields reject CR/LF header injection. Send idempotency is unique by MailAccountId and key with SHA-256 request fingerprint.

## Rate limiting and errors

Global fixed-window limiting partitions authenticated traffic by MailAccountId and pre-auth traffic by remote IP. Expected request, discovery, and provider failures use ProblemDetails with stable codes. Raw MailKit/network exceptions are not API contracts.

Discovery state is stored server-side behind a cryptographically random identifier and is only consumed by a successful account connection; a failed authentication keeps the state usable until it expires.

## Logging and audit

Serilog writes JSON to `logs/app-*.json` and `logs/http-*.json`. Correlation IDs are propagated through `X-Correlation-ID`; account IDs enter structured scope after authentication. `HttpBodyLoggingMiddleware` emits one structured completion event per request with method, path, query, status, elapsed time, account id, and bounded request/response bodies (`HttpLogging:MaxRequestBodyBytes` / `MaxResponseBodyBytes`, default 64 KB, truncation flagged). JSON bodies are embedded as nested objects after recursive case-insensitive redaction; non-JSON and binary payloads are logged as metadata only. Multipart send logs attachment file names, content types, and sizes — never bytes or field values — and `bodyHtml`/`bodyText` values are masked with length metadata. Authorization headers, cookies, and refresh tokens are never captured. Audit records use nullable MailAccountId and are retained (account id set to NULL) when the mailbox is deleted. Redaction covers password, appSpecificPassword, newPassword, currentPassword, accessToken, refreshToken, providerRefreshToken, authorizationCode, codeVerifier, token, authorization, clientSecret, secret, credential, encryptedMaterial, and apiKey.

## Ownership and deletion

Mail credentials, sessions, folders, mails, attachments, sync states, skipped UIDs, send operations, and device tokens cascade with their owning MailAccount. Audit logs are retained with a NULL account id when the mailbox is deleted.

## Operational requirements

Production must replace development JWT and database credentials, persist Data Protection keys on protected storage, terminate HTTPS correctly, restrict CORS/proxy trust, and align reverse-proxy body limits. New migrations target `mailclient_v2`; legacy database and migrations remain separate.
