# Flutter Backend Handoff

Backend API contract for the Flutter mail client. Base URL (dev): `http://localhost:5223`. Interactive docs (dev): `/swagger`.

All endpoints below except `POST /api/auth/register` and `POST /api/auth/login` require `Authorization: Bearer <JWT>`.

## Local LAN development

When the backend runs on another machine on the same LAN, never use `localhost` from the frontend device — `localhost` there means the device itself. Ask the backend developer for their current LAN IP and set the API base URL to:

```text
http://<BACKEND-PC-IP>:5223
```

Example only: `http://192.168.1.50:5223` (the real IP changes with DHCP).

- Another Windows PC (browser/desktop): `http://<BACKEND-PC-IP>:5223`.
- Physical Android device on the same Wi-Fi: `http://<BACKEND-PC-IP>:5223`.
- Android emulator running on your own PC: still `http://<BACKEND-PC-IP>:5223` — the backend is on a different physical machine, so `10.0.2.2` does not apply.
- Flutter Web: same LAN URL; the backend's Development CORS policy allows it.
- Connectivity check: open `http://<BACKEND-PC-IP>:5223/health` (expect `{ "status": "ok" }`) and `http://<BACKEND-PC-IP>:5223/swagger` for interactive docs.
- Both devices must be on a network that allows peer-to-peer LAN traffic; guest Wi-Fi with client isolation blocks this.

Android cleartext note: the LAN URL is plain HTTP, and Android may block cleartext traffic depending on the app's network-security configuration. Allow cleartext HTTP **for debug/development builds only** (e.g. a debug-only `networkSecurityConfig` / `usesCleartextTraffic` scoped to development). Never enable it for release/production builds.

## Authentication

```text
POST /api/auth/register   { "email": "...", "password": "...", "displayName": "..." }
POST /api/auth/login      { "email": "...", "password": "..." }
```

- Register always returns **`202 Accepted`** with a generic message for syntactically valid requests:
  ```json
  { "message": "If the registration request can be accepted, it has been received." }
  ```
  This is identical for new and already-registered addresses (account existence is never disclosed). Do not show "email already registered" based on this response. Invalid input (bad email syntax, password shorter than 8 or longer than 128 chars, missing display name) returns `400` validation errors.
- Depending on server `Registration:Mode`, a new account may be `Pending` (needs admin approval) rather than `Active`. `login` with a non-active account returns `403`.
- Login returns `{ "accessToken": "...", "userId": "...", "email": "...", "role": "..." }`. Send the token as `Authorization: Bearer <token>` on every other call. Tokens expire after 12 hours; re-login afterwards. Admin actions (approve/disable/password reset) invalidate existing tokens — treat sudden `401`s as "session expired, log in again".

## Mail accounts

```text
GET    /api/mail-accounts
GET    /api/mail-accounts/{id}
POST   /api/mail-accounts            # 201, duplicate email -> 409
PUT    /api/mail-accounts/{id}       # 200, duplicate email -> 409, unknown id -> 404
DELETE /api/mail-accounts/{id}       # 204 (also wipes cached mail + files)
POST   /api/mail-accounts/{id}/test  # connection test + folder discovery
GET    /api/mail-accounts/{id}/folders
POST   /api/mail-accounts/{id}/folders/refresh
PATCH  /api/mail-accounts/{id}/folders/{folderId}/sync   { "isSyncEnabled": true }
```

Account body (create; update takes the same fields with optional `password`):

```json
{
  "emailAddress": "user@example.com",
  "displayName": "Work",
  "username": "user@example.com",
  "password": "mailbox-password",
  "imapHost": "imap.example.com",
  "imapPort": 993,
  "imapSecurity": "SslOnConnect",
  "smtpHost": "smtp.example.com",
  "smtpPort": 587,
  "smtpSecurity": "StartTls",
  "saveSentCopy": true
}
```

Limits: email/username ≤ 320 chars, display name ≤ 250, hosts must be valid DNS (loopback/private/reserved ranges are rejected), mailbox password ≤ 1024 chars. `imapSecurity`/`smtpSecurity`: `SslOnConnect`, `StartTls`, or (`None` in dev/test only).

Reconfiguration semantics (matter for settings UI):

- Changing **IMAP identity** (`username`, `imapHost`, `imapPort`, `imapSecurity`) **wipes the locally cached mailbox** for that account and rebuilds it from the server on the next refresh/sync. Warn the user before saving such a change.
- Changing only display name, email address, SMTP settings, `saveSentCopy`, or the mailbox password keeps all cached mail.

## Mail

```text
GET /api/mails?folderType=Inbox&page=1&pageSize=30
GET /api/mails?accountId={accountId}&folderType=Sent&page=1&pageSize=30
GET /api/mails?folderId={folderId}&page=1&pageSize=30
GET /api/mails/{id}
GET /api/mails/{mailId}/attachments/{attachmentId}
PATCH /api/mails/{id}/read            { "isRead": true }
```

- Pagination: `page >= 1`, `1 <= pageSize <= 100`, ordered newest-first. The list returns metadata only (no bodies, no storage paths — never persist or display a `storagePath`; it is never exposed).
- Detail adds `bodyHtml`/`bodyText` plus attachment metadata (`id`, `fileName`, `contentType`, `sizeBytes`, `isInline`, `contentId`).
- Attachment download streams the file bytes; missing file → `404`. Both IDs in the path are verified end to end.
- Read/unread is two-way with the server: the API applies the flag to IMAP first, so external mail clients stay in sync. A `409` on mark-read means the folder changed server-side — refresh and retry.

## Sending

```text
POST /api/mail-accounts/{accountId}/send    multipart/form-data
```

Fields: `toAddress`, `subject`, `bodyHtml` and/or `bodyText` (at least one required), up to 20 `attachments` files.

**`Idempotency-Key` is mandatory.** Send it as a request header, 1–200 chars, else `400`:

```text
Idempotency-Key: <uuid-v4>
```

Rules:

- Generate **one UUID per logical send action** (per tap on "send").
- **Reuse the SAME key when retrying the same logical send** after an HTTP/network failure or timeout. The backend replays the original result instead of sending twice.
- A **new send action gets a new key**. Reusing a key with different recipients/subject/body/attachments returns `409 Conflict`.
- `DeliveryUnknown`/`409` on the delivery field means "may have been sent — do NOT auto-resend"; show the message as uncertain and let the user decide.
- Response: `{ "sent": true, "sentCopySaved": true|false, "warning": "..." }`. `sentCopySaved=false` with a warning means the mail WAS sent but the Sent-folder copy failed — do not resend; the warning text is user-presentable.

## Device registration (push)

```text
POST   /api/devices/register     { "pushToken": "<FCM token>", "platform": "android" }
DELETE /api/devices/{id}
```

- `platform` is `android` or `ios` (case-insensitive, stored lowercase); anything else → `400`. `pushToken` required, non-blank, ≤ 500 chars.
- Register returns `200` with `{ "id": "...", "platform": "...", "registeredAt": "...", "lastSeenAt": "..." }`. Keep the returned `id` for later deletion (e.g. on logout).
- **Re-register whenever Firebase rotates the token** — registration is idempotent per user (updates in place). If the same physical device logs in as a different user, the token is silently transferred, so the previous user stops receiving pushes there. No extra work needed on logout/login beyond registering the current token and deleting it on explicit logout.
- Delete returns `204`; deleting another user's (or unknown) id returns `404`.
- Registration endpoints are rate-limited per user (generous: token refresh loops are safe; do not retry in a tight loop).

## Push payload

Data payload (all values strings):

```json
{
  "type": "new_mail",
  "mailId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "accountId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "folderId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

Display notification: **title = sender display name (or sender address if none), body = subject**. No mail body, HTML, attachments, or credentials are ever included.

Behavior:

- Only **new Inbox mail** triggers a push. Sent mail, re-syncs, and read/unread changes never do.
- On notification tap, route to the mail detail view: `GET /api/mails/{mailId}` (fetch fresh — the push carries no content).
- If the fetch returns `404` (e.g. account reconfigured and cache rebuilt), fall back to the Inbox list.

## HTML security

Email HTML from `bodyHtml` is **untrusted**:

- JavaScript disabled, no WebView JavaScript bridge exposed to email content.
- Remote resources (images, CSS, fonts) blocked by default; load only on explicit user opt-in per message.
- Open links in the system browser, never inside the mail WebView.

## Error-shape conventions

- Validation failures: `400` with RFC 9457 problem details (`errors: { field: [messages] }`).
- Ownership violations on mail data: `404` (never `403`, to avoid leaking existence).
- Duplicate mail account / idempotency misuse: `409`.
- Rate limiting: `429`.
