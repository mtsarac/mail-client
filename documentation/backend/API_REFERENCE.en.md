# API Reference (English)

> Türkçe: [API_REFERENCE.tr.md](API_REFERENCE.tr.md)

Base URL: configured deployment URL. Development Swagger: `/swagger`.

## Onboarding

### Automatic discovery

`POST /api/accounts/discover`

```json
{ "email": "person@example.com" }
```

Success returns `discoveryId`, email, provider, authentication methods, and `manualSetupAvailable`. Internal IMAP/SMTP settings remain server-side.

When all strategies fail, response is HTTP 422 ProblemDetails:

```json
{
  "title": "Mail server discovery failed.",
  "status": 422,
  "code": "mail_discovery_failed",
  "manualSetupAvailable": true
}
```

Nothing is persisted on failure. Discovery failure does not mean provider unsupported; client should offer manual fallback.

### Connect discovered mailbox

`POST /api/accounts/connect`

```json
{
  "discoveryId": "opaque-temporary-id",
  "authentication": { "type": "Password", "password": "ExamplePassword123!" },
  "deviceIdentifier": "optional-device-id"
}
```

The discovery ID is one-time and expires. Backend validates IMAP and SMTP credentials, creates or reuses normalized MailAccount, encrypts credential material, creates MailSession, and returns access/refresh tokens.

### Manual fallback

`POST /api/accounts/connect-manual`

```json
{
  "email": "person@example.com",
  "username": "person@example.com",
  "authentication": { "type": "Password", "password": "ExamplePassword123!" },
  "imap": { "host": "imap.example.com", "port": 993, "security": "SslOnConnect" },
  "smtp": { "host": "smtp.example.com", "port": 465, "security": "SslOnConnect" }
}
```

Manual mode is fallback only. It does not disable SSRF protection, safe DNS/IP checks, TLS certificate validation, protocol validation, IMAP auth, or SMTP auth. Password and AppSpecificPassword are implemented. OAuth2 is modeled but provider flows are deferred.

## Sessions

- `POST /api/auth/refresh` with `{ "refreshToken": "..." }`: rotates session and token. No access JWT required.
- `POST /api/auth/logout` with `{ "refreshToken": "..." }`: revokes this client session.

Refresh tokens are never stored raw. JWT `sub` equals MailAccountId.

## Account-scoped API

All routes below require bearer JWT and infer account from `sub`:

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/account` | current account |
| DELETE | `/api/account` | delete mailbox and cached data |
| GET | `/api/folders` | list folders |
| POST | `/api/folders/refresh` | enqueue folder refresh |
| POST | `/api/folders/{id}/sync` | request folder sync; explicit requests ignore `IsSyncEnabled`, unavailable folders return `409 mail_folder_unavailable` |
| GET | `/api/mails` | paged list of current-account messages (`folderId`, `isRead`, `hasAttachments`, `search`, `page`, `pageSize` ≤ 100 → `{ items, page, pageSize, total }`) |
| GET | `/api/mails/{id}` | message detail |
| PATCH | `/api/mails/{id}/read` | `{ "isRead": true }` |
| GET | `/api/mails/{mailId}/attachments/{attachmentId}` | stream owned attachment |
| POST | `/api/mails/send` | multipart send (to, subject, bodyHtml/bodyText, ≤20 attachments, Idempotency-Key header) → 200 `{ sent, sentCopySaved, warning }` |
| POST | `/api/devices` | register device token for this account |
| DELETE | `/api/devices/{id}` | remove owned device token |
| GET | `/health` | health response |

Foreign mail, attachment, folder, or device IDs return 404.

## Stable errors

ProblemDetails uses stable codes including `mail_discovery_failed`, `discovery_expired`, `mail_server_unsafe`, `mail_server_unreachable`, `mail_tls_failed`, `mail_authentication_failed`, `mail_smtp_authentication_failed`, `mail_provider_unavailable`, `unsupported_authentication_method`, `invalid_refresh_token`, `session_revoked`, `idempotency_key_required`, `idempotency_conflict`, and `invalid_mail_header`.

Expected provider/discovery failures map to 400/401/409/422/429/502/503. Raw MailKit and network exceptions are not API contracts.
