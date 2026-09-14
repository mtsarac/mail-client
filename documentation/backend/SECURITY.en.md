# Security Reference (English)

> Türkçe: [SECURITY.tr.md](SECURITY.tr.md)

## Authentication

MailAccount is the principal. JWT validates issuer, audience, signing key, and lifetime; `sub` is MailAccountId. Access lifetime is configurable. Persistent MailSession refresh tokens are cryptographically random, stored only as SHA-256 hashes, rotated on refresh, and revoked on logout. Provider credentials are separate from session tokens and encrypted with ASP.NET Core Data Protection.

Password and AppSpecificPassword are implemented. OAuth2 storage shape exists, but provider authorization/callback integrations are deferred; no generic fake OAuth flow exists.

## Account isolation

Protected APIs derive MailAccountId from `ICurrentMailAccount`, never request account IDs. Database reads and writes scope folder, mail, attachment, device, and send records by MailAccountId. Foreign IDs return 404.

## Discovery and SSRF

Discovery order is known provider, DNS SRV, autoconfig, Microsoft Autodiscover, then controlled heuristics. HTTP discovery clients have finite timeouts and automatic redirects disabled. Candidate and manual hosts pass DNS and address validation before protocol authentication. Localhost, loopback, private IPv4, link-local, multicast, IPv6 unique-local, and unsafe destinations are rejected. Only `SslOnConnect` and `StartTls` are modeled. MailKit certificate validation is not disabled.

Manual setup bypasses discovery only. It cannot bypass host validation, DNS/IP checks, TLS, IMAP auth, or SMTP auth.

## Stored data

Credential material is encrypted before persistence and never returned. Refresh tokens are stored hashed. Attachment paths are canonicalized and required to remain below storage root. Send fields reject CR/LF header injection. Send idempotency is unique by MailAccountId and key with SHA-256 request fingerprint.

## Rate limiting and errors

Global fixed-window limiting partitions authenticated traffic by MailAccountId and pre-auth traffic by remote IP. Expected request, discovery, and provider failures use ProblemDetails with stable codes. Raw MailKit/network exceptions are not API contracts.

## Logging and audit

Serilog writes JSON to `logs/app-*.json` and `logs/http-*.json`. Correlation IDs are propagated through `X-Correlation-ID`; account IDs enter structured scope after authentication. Request bodies are not logged by the V2 middleware. Audit records use nullable MailAccountId. Recursive redaction covers password, appSpecificPassword, accessToken, refreshToken, providerRefreshToken, authorizationCode, codeVerifier, clientSecret, token, credential, and secret.

## Operational requirements

Production must replace development JWT and database credentials, persist Data Protection keys on protected storage, terminate HTTPS correctly, restrict CORS/proxy trust, and align reverse-proxy body limits. New migrations target `mailclient_v2`; legacy database and migrations remain separate.
