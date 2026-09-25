# API reference

🇹🇷 [Türkçe](../tr/api-reference.md)

Summary of all endpoints. Swagger UI (`/swagger`, Development only) has the full
request/response schemas and error codes. Request/response examples for the
mobile client: [Flutter guide](../flutter-api-integration.md) (Turkish).

**Conventions**

- Auth `bearer` = `Authorization: Bearer <accessToken>`; `anon` = no token.
- Errors are RFC 7807 problem details with a stable snake_case `code` extension.
  A malformed JSON body → `400` `invalid_request`; a truncated/aborted multipart
  body gets a bare `400`.
- Every response carries `X-Correlation-ID`; send your own to trace a request.
- Rate limit: 60 requests/minute per account (per IP when anonymous) → `429`.
- Mail mutations are remote-first (applied on the IMAP server, then mirrored).
- `Idempotency-Key` (send endpoints): required, at most 200 characters
  (`idempotency_key_required` / `idempotency_key_too_long`).

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
| GET | `/api/account/sync-status` | bearer | Per-folder sync/backfill state (last successful sync, last failure, backfill progress) |
| GET / PUT | `/api/account/sync-scope` | bearer | Read or update account-scoped background folder coverage (InboxAndSent, AllFolders, SelectedFolders) |

`/api/account/sync-scope` returns `{scope, syncedFolderIds}`. PUT
`{scope:"SelectedFolders",folderIds:[...]}` selects one or more available folders
owned by the account; other modes omit `folderIds`. Invalid or foreign folder
IDs fail validation without changing the current setting. New folders are
included automatically only in `AllFolders` (or Inbox/Sent in the default
mode). Manual folder sync remains available regardless of scope.

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
| GET | `/api/mails/{id}` | bearer | Mail detail (`isFromMe`: Sent/Drafts mail, or sender equals the account address case-insensitively, in any folder) |
| GET | `/api/search` | bearer | Search cached mail (all filters optional, AND-combined; see below) |
| GET | `/api/search/remote` | bearer | User-triggered generic IMAP search; imports missing matches before a follow-up `/api/search` |
| GET | `/api/mails/{mailId}/attachments/{attachmentId}` | bearer | Download an attachment |
| POST | `/api/mails/send` | bearer + `Idempotency-Key` | Send mail (multipart/form-data) |
| GET | `/api/mails/{id}/compose/reply · reply-all · forward` | bearer | Prefilled compose context |

`/api/search` filters: `folderId`, `conversationId`, `isRead`, `flagged`,
`hasAttachment`, `labelId` match exactly; `from` = case-insensitive contains
on sender address or display name; `to` = case-insensitive contains on any
To/Cc/Bcc address or name; `fromDate`/`toDate` filter on received time,
`fromDate` inclusive, `toDate` exclusive; values without a UTC offset are
treated as UTC. `labelId` only matches labels owned by the authenticated
account.

`/api/search/remote` accepts the same search filters except pagination; at
least one of `q`, `from`, `to` must be non-blank. The response
`{matched, imported, remaining, complete}` reports not-yet-indexed matches
and whether the request covered the entire scope. Each call imports at most
25 messages with a 20-second deadline; retry if `complete` is false.
`hasAttachment` is applied by the subsequent cached search, not by IMAP.
Foreign folder IDs return 404.

### Drafts

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/drafts/{id}` | bearer | Get draft |
| POST | `/api/drafts` | bearer | Create draft (saved to server Drafts folder) |
| PUT | `/api/drafts/{id}` | bearer | Replace draft (returned `mailId` may differ) |
| DELETE | `/api/drafts/{id}` | bearer | Delete draft |
| POST | `/api/drafts/{id}/send` | bearer + `Idempotency-Key` | Send the draft and remove it; retrying a successful send with the same key replays the result (`sent: true`, `draftRemoved: true`) instead of `422 mail_not_draft` |

### Mail ops

| Method | Path | Auth | Description |
|---|---|---|---|
| PATCH | `/api/mails/{id}/read` | bearer | Set read state (body) |
| POST | `/api/mails/{id}/{op}` | bearer | `op`: read, unread, star, unstar, trash, restore, archive, spam, not-spam, delete (204, no body). `delete` permanently expunges the mail and is allowed only in Trash/Junk (otherwise 422 `mail_operation_not_supported`; server-side failure 502 `mail_delete_failed`) |
| POST | `/api/mails/{id}/move · copy` | bearer | Move/copy to a folder (body) |
| POST | `/api/mails/bulk/{action}` | bearer | Bulk op on up to 100 `mailIds`; `read`, `unread`, `star`, `unstar`, `archive`, `trash`, `restore`, `spam`, `not-spam`, `delete`, `move` (`move` requires `folderId`) |

### Conversations

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/conversations` | bearer | List threads, newest first (`page`, `pageSize` ≤ 100); `participants` = distinct sender names (display name, else address), alphabetical, max 10 |
| GET | `/api/conversations/{id}` | bearer | Thread with its messages (`includeTrash`, `include=body`); `isFromMe` as in mail detail |

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
