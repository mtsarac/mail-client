# API Reference (English)

> Türkçe: [API_REFERENCE.tr.md](API_REFERENCE.tr.md)

Base URL (dev): `http://localhost:5223`. Interactive docs (dev only): `/swagger`.
Auth: `Authorization: Bearer <JWT>` everywhere except `POST /api/auth/register`
and `POST /api/auth/login`. Field names below are the exact DTO names.
Error shape: RFC 9457 problem details (`errors: { field: [messages] }`).

## Authentication

### `POST /api/auth/register` (anonymous, `auth` rate limit)

```json
{ "email": "user@example.com", "password": "secret123", "displayName": "Ada" }
```

- `202 Accepted` always (valid syntax): `{ "message": "If the registration request can be accepted, it has been received." }` — identical for new/existing addresses.
- `400` validation: bad email, password not 8–128 chars, missing display name (>250), or `Registration:Mode=Disabled`.

### `POST /api/auth/login` (anonymous, `auth` rate limit)

```json
{ "email": "user@example.com", "password": "secret123" }
```

- `200`: `{ "accessToken": "...", "userId": "...", "email": "...", "role": "User|Admin" }` (12-hour token).
- `401` wrong credentials; `403` non-active user; `400` malformed body.

## Users / Admin (`Admin` role required)

| Method & Path | Request | Response | Codes |
|---|---|---|---|
| `GET /api/admin/users` | — | `[{ id, email, displayName, role, status, ... }]` | 200, 401, 403 |
| `POST /api/admin/users` | `{ email, password, displayName, role, status }` | `201` + created user, `Location: /api/admin/users/{id}` | 201, 400, 409 (duplicate), 401, 403 |
| `PATCH /api/admin/users/{id}/approve` | — | `204` (status→Active, tokens invalidated) | 204, 404, 401, 403 |
| `PATCH /api/admin/users/{id}/disable` | — | `204` (status→Disabled, tokens invalidated) | 204, 404, 401, 403 |
| `PATCH /api/admin/users/{id}/enable` | — | `204` (status→Active, tokens invalidated) | 204, 404, 401, 403 |
| `POST /api/admin/users/{id}/reset-password` | `{ password }` | `204` (tokens invalidated) | 204, 400, 404, 401, 403 |

## Mail accounts (JWT, owner-scoped)

Account body (create; update same fields, `password` optional):

```json
{
  "emailAddress": "user@example.com", "displayName": "Work",
  "username": "user@example.com", "password": "mailbox-password",
  "imapHost": "imap.example.com", "imapPort": 993, "imapSecurity": "SslOnConnect",
  "smtpHost": "smtp.example.com", "smtpPort": 587, "smtpSecurity": "StartTls",
  "saveSentCopy": true
}
```

Limits: email/username ≤ 320, display ≤ 250, mailbox password ≤ 1024,
ports 1–65535, valid DNS hosts (loopback/private/reserved rejected),
`imapSecurity/smtpSecurity` ∈ `SslOnConnect|StartTls` (`None` dev/test only).

| Method & Path | Response | Codes / Notes |
|---|---|---|
| `GET /api/mail-accounts` | `[MailAccountResponse{ id, emailAddress, displayName, username, imapHost, imapPort, imapSecurity, smtpHost, smtpPort, smtpSecurity, saveSentCopy, isActive }]` | 200 |
| `GET /api/mail-accounts/{id}` | `MailAccountResponse` | 200, 404 |
| `POST /api/mail-accounts` | `201` + account, `Location` header | 201, 400, 409 duplicate |
| `PUT /api/mail-accounts/{id}` | `200` + account | 200, 400, 404, 409. IMAP-identity change wipes cache |
| `DELETE /api/mail-accounts/{id}` | `204`, wipes cached mail + files | 204, 404 |
| `POST /api/mail-accounts/{id}/test` (`mail-operations` limit) | `MailAccountTestResponse{ succeeded, message }`; on success also refreshes folders | 200, 404 |
| `GET /api/mail-accounts/{id}/folders` | `[MailFolderResponse{ id, name, fullName, folderType, uidValidity, isSyncEnabled, isAvailable }]` | 200, 404 |
| `POST /api/mail-accounts/{id}/folders/refresh` (`mail-operations` limit) | `MailFolderRefreshResponse{ succeeded, message, folders }` | 200, 404 |
| `PATCH /api/mail-accounts/{id}/folders/{folderId}/sync` | `{ "isSyncEnabled": true }` → `MailFolderResponse` | 200, 400, 404 |

## Mail (JWT, owner-scoped)

- `GET /api/mails?folderType=Inbox&page=1&pageSize=30` — also
  `accountId`, `folderId` filters. `200 MailPageDto{ items: [MailSummaryDto{
  id, mailAccountId, mailAccountEmail, folderId, folderType,
  fromDisplayName, fromAddress, subject, receivedAt, isRead, hasAttachments
  }], totalCount, page, pageSize }`. `400` bad `folderType`/paging
  (`page ≥ 1`, `1 ≤ pageSize ≤ 100`), `404` unknown account/folder. Ordered
  `ReceivedAt DESC, Id DESC`. No bodies, no paths.
- `GET /api/mails/{id}` — `200 MailDetailDto` (summary + `messageId`,
  `toAddress`, `bodyHtml`, `bodyText`, `attachments: [AttachmentDto{ id,
  fileName, contentType, sizeBytes, isInline, contentId }]`). Else `404`.
- `GET /api/mails/{mailId}/attachments/{attachmentId}` — streams file bytes
  (`Results.File`). `404` on any mismatch or missing file.
- `PATCH /api/mails/{id}/read` (`mail-operations` limit),
  `{ "isRead": true }` → `200 MailReadDto{ id, isRead }`. `404` unknown,
  `409` folder changed server-side (refresh + retry), `502` mail-server
  failure, `400` validation.

## Sending (JWT, `mail-operations` limit)

`POST /api/mail-accounts/{accountId}/send` — `multipart/form-data` fields:
`toAddress`, `subject`, `bodyHtml` and/or `bodyText`, up to 20 `attachments`.
Header **`Idempotency-Key: <uuid>` mandatory** (missing/blank/>200 chars → 400).

- `200 { sent, sentCopySaved, warning }` when `sent=true`. `sentCopySaved=false`
  + warning = sent, Sent-folder copy failed — do not resend.
- `502` SMTP/transport failure (before any delivery proof may retry with same key).
- `404` unknown account; `409` key in use / uncertain / content mismatch;
  `400` validation; `429` rate limit.

## Devices (JWT, owner-scoped, `mail-operations` limit)

- `POST /api/devices/register` `{ "pushToken": "...", "platform": "android" }`
  → `200 DeviceTokenResponse{ id, platform, registeredAt, lastSeenAt }`.
  `400` blank/>500-char token or platform ∉ {android, ios}.
- `DELETE /api/devices/{id}` → `204`; `404` for foreign/unknown ids.

## Health (anonymous)

- `GET /health` → `200 { "status": "ok" }`.
- `GET /health/db` → `200 { status: "Healthy", checks: [{ name: "postgres",
  status: "Healthy", error: null }] }` or `503 Unhealthy`.

## HTTP error quick reference

| Code | Meaning here |
|---|---|
| 400 | validation (problem details with `errors`) |
| 401 | missing/invalid/expired JWT, invalidated session |
| 403 | authenticated but forbidden (non-active user, non-admin on admin routes) |
| 404 | not found **or** foreign-owned mail data (existence never leaked) |
| 409 | duplicate account, UIDVALIDITY change, idempotency conflict/uncertainty |
| 429 | rate limit (`auth` per IP / `mail-operations` per user, 20/min) |
| 502 | mail-server operation failed (IMAP/SMTP/provider) |
| 503 | `/health/db` unhealthy |
