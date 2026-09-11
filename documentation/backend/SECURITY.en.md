# Security Reference (English)

> Türkçe: [SECURITY.tr.md](SECURITY.tr.md)

Every control below exists in code. Nothing here is aspirational.

## Authentication & sessions

- JWT bearer: issuer, audience, signing key, and lifetime validated; startup
  throws when `Jwt:Key` < 32 chars. Claims: `sub` (user id), `role`, `tv`
  (TokenVersion); 12-hour expiry; HMAC-SHA256.
- Per-request session validation (`UserSessionValidator`): user must exist,
  be `Active`, and present the current `TokenVersion`. Role claim is rebuilt
  from the DB each request; status/version changes kill sessions immediately.
- Passwords: Identity `PasswordHasher<User>` (PBKDF2), 8–128 chars.
  Login/register errors are generic (no user enumeration beyond the
  always-`202` register contract).
- Registration modes (`Registration:Mode`): `Open` / `ApprovalRequired`
  (default) / `Disabled`. Admin approve/disable/enable + password reset bump
  `TokenVersion`.

## Authorization & ownership

- Roles `User`/`Admin`; admin routes require `Admin`.
- **Admin never bypasses mail ownership**: every mail/account/folder/device
  query filters by caller `UserId`. Foreign ids return `404`, never `403`
  (existence not leaked).
- Device delete and token cleanup re-check ownership inside the write.

## Rate limiting

Fixed-window, `429` (`RejectionStatusCode`) on excess: `auth` per client IP
(register/login, 20/min); `mail-operations` per authenticated user
(test/refresh/send/mark-read/device register+delete, 20/min). Real client IP
comes from forwarded headers only with trusted proxy config.

## SSRF / outbound network security

User-controlled IMAP/SMTP hosts pass `OutboundHostValidator` before any
connection (`MailConnectionHelper.ResolveAllowedAsync`):

1. Literal check: empty, `localhost`, or IP literal → loopback/special ranges
   denied inline.
2. Otherwise DNS-resolve **all** addresses (`IDnsResolver`) — failure denies.
3. Every resolved address checked: IPv4 denies loopback, private
   (10/8, 172.16/12, 192.168/16), link-local (169.254/16), CGNAT
   (100.64/10), multicast, reserved/broadcast, documentation/benchmark/relay
   ranges; IPv6 denies multicast, link-local (fe80::/10), unique-local
   (fc00::/7), documentation; IPv4-mapped IPv6 normalized first.
4. The validated IP is used for the connection (no TOCTOU re-resolve gap in
   the helper path).

Why: a mailbox host field is attacker-controlled input; without this, the
server could be made to probe internal networks. Tests cover the validator
(`OutboundHostValidatorTests`) with GreenMail/local servers used in tests.

## Transport security

`MailSecurity`: `SslOnConnect`, `StartTls`, `None`. `None` is rejected with a
`security` validation error outside Development/Test — production mail
traffic is always encrypted. (MailKit default certificate validation applies;
no custom callback weakens it.)

## Data Protection & credential storage

- Mailbox passwords encrypted with ASP.NET Core Data Protection
  (`DataProtectionCredentialProtector`); key ring at `DataProtection:KeyPath`
  (default `data/protection-keys`, git-ignored).
- Keys must persist across restarts and be shared by all instances via a
  persistent shared volume with restrictive permissions.
- Production: ring encrypted with an X509 PFX (`CertificatePath` +
  `CertificatePassword` via env/mounts, never committed); non-dev startup
  fails fast when missing/unloadable/without private key. Dev/Test may omit.
- Passwords are decrypted only inside short-lived mail operations; API
  responses never contain credentials or storage paths.

## Attachments & request sizes

- On-disk paths built only from GUID segments; `LocalFileStorage` pins with
  `GetFullPath` + root-prefix check (escape → exception). Filenames never
  touch the filesystem (outgoing names normalized to 255/150 chars).
- Incoming caps: per-attachment 25 MiB, per-message attachments 50 MiB,
  message 100 MiB (SIZE-prefetch, bodies never fetched to measure),
  validated `MaxMessageBytes ≥ MaxMessageAttachmentBytes ≥ MaxAttachmentBytes`.
- Send caps: body ≤ 1M chars each, ≤ 20 attachments; Kestrel + multipart
  limits derived from the same numbers (+1 MiB framing) so the edge rejects
  what the app would reject. Proxies need matching limits.

## Send idempotency (anti-double-send)

`SendOperations` unique per user+key, transactional claim
(`pg_advisory_xact_lock`), SHA-256 content fingerprint. Replays on
same-key/same-content; `409` on reuse-conflict; `DeliveryUnknown` terminal —
the app never auto-resends once SMTP may have accepted. See
[BACKEND_GUIDE.en.md](BACKEND_GUIDE.en.md) §9.

## Push credential handling

Fail-fast startup validation (local only, no network): project id required,
service-account JSON must exist/parse/be `type: service_account` with email +
valid PEM private key. Resolution: explicit path → env →
well-known ADC → `GetApplicationDefault()`. Secrets never logged.
Only `Unregistered` prunes tokens. The server JSON must never ship in Flutter.

## Proxy handling

`X-Forwarded-For/Proto` honored only when `Proxy:KnownProxies/KnownNetworks`
non-empty and only from those networks (parsed IPs/CIDRs; invalid entries
ignored). Otherwise direct-connection values are used — spoofed headers from
untrusted networks have no effect.

## Supply chain & static analysis

- CI NuGet audit (`dotnet list … --vulnerable --include-transitive`) fails
  the build on any vulnerable package, transitive included.
- CodeQL C# on backend pushes/PRs + weekly schedule.
- `TreatWarningsAsErrors` (+ the CS0618 pragma is scoped to the single FCM
  `Token` usage with a justifying comment — registration tokens are not FIDs).

## Error handling

Unhandled exceptions → ProblemDetails via `UseExceptionHandler` (no stack
traces to clients). Validation/auth/rate-limit/mail/provider errors map to
400/401/403/429/404/409/502 per [API_REFERENCE.en.md](API_REFERENCE.en.md).
Sync failures log server-side (user id + host, never passwords); push
failures never disturb committed state.

## Dev concessions vs production requirements

| Development (do not ship) | Production (required) |
|---|---|
| Plain HTTP, no HTTPS redirect | HTTPS + trusted reverse proxy |
| Permissive CORS (`AllowAnyOrigin/Headers/Methods`) | No CORS policy (browsers blocked by default) |
| Dev JWT key in committed config | Strong secret via env/mount |
| `MailSecurity.None` allowed | Encrypted mail transport only |
| No Data Protection certificate | X509-encrypted shared key ring |
| Firebase disabled | Secret-mounted service account |
| Swagger/OpenAPI mapped | No Swagger surface |
