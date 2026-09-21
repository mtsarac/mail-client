# API reference

🇹🇷 [Türkçe](../tr/api-reference.md)

Summary of all endpoints. Swagger UI (`/swagger`, Development only) has the full
request/response schemas and error codes. Request/response examples for the
mobile client: [Flutter guide](../flutter-api-integration.md) (Turkish).

**Conventions**

- Auth `bearer` = `Authorization: Bearer <accessToken>`; `anon` = no token.
- Errors are RFC 7807 problem details with a stable snake_case `code` extension.
- Every response carries `X-Correlation-ID`; send your own to trace a request.
- Rate limit: 60 requests/minute per account (per IP when anonymous) → `429`.
- Mail mutations are remote-first (applied on the IMAP server, then mirrored).

### Accounts

| Method | Path | Auth | Description |
|---|---|---|---|
| POST | `/api/accounts/discover` | anon | Discover IMAP/SMTP settings for an email address |
| POST | `/api/accounts/connect` | anon | Create a mailbox from a discovery result; returns tokens |
| POST | `/api/accounts/connect-manual` | anon | Create a mailbox with manual server settings |
| POST | `/api/accounts/login` | anon | Sign in to an existing mailbox from another device |
| GET | `/api/account` | bearer | Current account |
| POST | `/api/account/reconnect` | bearer | Update stored credentials (e.g. after password change) |
| DELETE | `/api/account` | bearer | Delete the account and its data |
| GET | `/api/account/sessions` | bearer | List signed-in devices/sessions |
| DELETE | `/api/account/sessions/{sessionId}` | bearer | Revoke a session remotely |

### OAuth

| Method | Path | Auth | Description |
|---|---|---|---|
| POST | `/api/accounts/oauth/{provider}/start` | anon | Start OAuth (Authorization Code + PKCE); `google` or `microsoft` |
| POST | `/api/accounts/oauth/{provider}/complete` | anon | Complete OAuth with `state` + `code`; returns tokens |

### Auth

| Method | Path | Auth | Description |
|---|---|---|---|
| POST | `/api/auth/refresh` | anon (refresh token in body) | Rotate refresh token, get a new pair |
| POST | `/api/auth/logout` | anon (refresh token in body) | Revoke this device's session |

### Folders

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/folders` | bearer | List cached folders |
| POST | `/api/folders/refresh` | bearer | Re-read the folder list from the server |
| POST | `/api/folders/{id}/sync` | bearer | Queue a folder sync (202) |

### Mail

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/mails` | bearer | List mail (`folderId`, `isRead`, `hasAttachments`, `search`, `page`, `pageSize` ≤ 100) |
| GET | `/api/mails/{id}` | bearer | Mail detail |
| GET | `/api/search` | bearer | Search cached mail (all filters optional, AND-combined) |
| GET | `/api/mails/{mailId}/attachments/{attachmentId}` | bearer | Download an attachment |
| POST | `/api/mails/send` | bearer + `Idempotency-Key` | Send mail (multipart/form-data) |
| GET | `/api/mails/{id}/compose/reply · reply-all · forward` | bearer | Prefilled compose context |

### Drafts

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/drafts/{id}` | bearer | Get draft |
| POST | `/api/drafts` | bearer | Create draft (saved to server Drafts folder) |
| PUT | `/api/drafts/{id}` | bearer | Replace draft (returned `mailId` may differ) |
| DELETE | `/api/drafts/{id}` | bearer | Delete draft |
| POST | `/api/drafts/{id}/send` | bearer + `Idempotency-Key` | Send the draft and remove it |

### Mail ops

| Method | Path | Auth | Description |
|---|---|---|---|
| PATCH | `/api/mails/{id}/read` | bearer | Set read state (body) |
| POST | `/api/mails/{id}/{op}` | bearer | `op`: read, unread, star, unstar, trash, restore, archive, spam, not-spam (204, no body) |
| POST | `/api/mails/{id}/move · copy` | bearer | Move/copy to a folder (body) |
| POST | `/api/mails/bulk/{action}` | bearer | Bulk op on up to 100 `mailIds`; `read`, `unread`, `star`, `unstar`, `archive`, `trash`, `restore`, `spam`, `not-spam`, `move` |

### Conversations

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/conversations` | bearer | List threads, newest first (`page`, `pageSize` ≤ 100) |
| GET | `/api/conversations/{id}` | bearer | Thread with its messages (`includeTrash`, `include=body`) |

### Devices

| Method | Path | Auth | Description |
|---|---|---|---|
| POST | `/api/devices` | bearer | Register a push token (idempotent per mailbox+token) |
| DELETE | `/api/devices/{id}` | bearer | Remove a device |

### Management

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/management/runtime-settings` | `X-Management-Key` | Current runtime settings + `version` |
| PUT | `/api/management/runtime-settings` | `X-Management-Key` | Replace settings; guarded by `expectedVersion` |
| GET | `/api/management/whitelist` | `X-Management-Key` | List allowlisted emails |
| POST | `/api/management/whitelist/emails` | `X-Management-Key` | Add emails (duplicates/invalid skipped) |
| DELETE | `/api/management/whitelist/emails/{email}` | `X-Management-Key` | Remove an email (disables the mailbox if enforced) |

### Health

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/health · /health/live · /health/ready` | anon | Liveness / readiness (ready checks Postgres + storage) |
