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
| GET | `/api/account` | bearer | Current account (`signature` = plain-text body of the default new-mail signature, or null) |
| PUT | `/api/account/signature` | bearer | Legacy compat: set or clear the default new-mail signature (blank clears); body `{signature}` |
| POST | `/api/account/reconnect` | bearer | Update stored credentials (e.g. after password change) |
| DELETE | `/api/account` | bearer | Delete the account and its data |
| GET | `/api/account/sessions` | bearer | List signed-in devices/sessions |
| DELETE | `/api/account/sessions/{sessionId}` | bearer | Revoke a session remotely |
| GET | `/api/account/sync-status` | bearer | Per-folder sync/backfill state (last successful sync, last failure, backfill progress) |
| GET / PUT | `/api/account/sync-scope` | bearer | Read or update account-scoped background folder coverage (InboxAndSent, AllFolders, SelectedFolders) |
| GET / PUT | `/api/account/notification-settings` | bearer | Read or update account-scoped mail notification preferences (enabled, inbox only, privacy) |

`/api/account/sync-scope` returns `{scope, syncedFolderIds}`. PUT
`{scope:"SelectedFolders",folderIds:[...]}` selects one or more available folders
owned by the account; other modes omit `folderIds`. Invalid or foreign folder
IDs fail validation without changing the current setting. New folders are
included automatically only in `AllFolders` (or Inbox/Sent in the default
mode). Manual folder sync remains available regardless of scope.

`/api/account/notification-settings` returns
`{enabled, inboxOnly, privacy, previewsAllowedByServer}`; PUT takes
`{enabled, inboxOnly, privacy}`. They apply to every device signed into the
mailbox and never change device registration. `privacy` is `Full` (sender,
subject, short text preview), `Limited` (sender and subject; default) or
`Private` (generic text only). When the operator disables mail previews,
`previewsAllowedByServer` is false and pushes are sent as `Private`.
`inboxOnly: false` notifies new mail in every synced folder except Sent,
Drafts, Trash and Junk. `enabled: false` stops new-mail, snooze wake-up and
reply-reminder pushes only; account alerts (reauthentication) are still sent.

New-mail pushes are sent after server rules ran, so mail a rule moved out of
the notified folders or marked read is not announced. `new_mail`,
`snooze_expired` and `reply_reminder` are data-only on Android (the app
renders them with quick actions) and carry an APNs alert on iOS. Due snoozes
are ended on the server every 30 seconds; each one wakes once, and a snooze
cancelled or moved to a later time before it is claimed never wakes. Reply
reminders (`POST /api/mails/{id}/reply-reminder`, `GET /api/reply-reminders`,
`DELETE /api/mails/{id}/reply-reminder`) watch sent mail for a reply: one
pending reminder per mail, rescheduling overwrites the due time, and the
server checks due reminders every 30 seconds — pushing `reply_reminder` once
(`No reply yet`, recipient + subject per privacy) unless the mail was
answered, moved to Trash/Junk, or cancelled. Past due times are rejected
(`reply_reminder_in_past`), non-sent mail is rejected
(`reply_reminder_requires_sent_mail`), already-answered mail is rejected
(`reply_reminder_already_replied`).

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
| GET | `/api/folders` | bearer | List cached folders (with `delimiter` and `parentId`) |
| POST | `/api/folders` | bearer | Create a folder on the server (`name`, optional `parentId`) |
| PATCH | `/api/folders/{id}` | bearer | Rename a custom folder on the server; ids are kept |
| DELETE | `/api/folders/{id}` | bearer | Delete an empty custom folder without children |
| POST | `/api/folders/refresh` | bearer | Re-read the folder list from the server |
| POST | `/api/folders/{id}/sync` | bearer | Queue a folder sync (202) |

### Rules

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/rules` | bearer | List mail rules in evaluation order (`priority`, then creation time) |
| POST | `/api/rules` | bearer | Create a rule (201); with `legacyId`, repeating returns the migrated rule (200) |
| PUT | `/api/rules/{id}` | bearer | Replace a rule |
| DELETE | `/api/rules/{id}` | bearer | Delete a rule (204) |

Body: `{name, enabled, priority, logic:"And"|"Or", conditions:[{type,value}], actions:[{type,folderId?,labelId?}], legacyId?}`.
Condition types: `senderContains`, `senderEquals`, `senderDomain`, `subjectContains`,
`recipientContains` (To/Cc/Bcc), `hasAttachment` (no value), `folder` (folder id).
Action types: `markRead`, `markUnread`, `star`, `archive`, `move` (`folderId`),
`trash`, `spam`, `addLabel` (`labelId`), `stopProcessing`. Folder and label ids must
belong to the authenticated account. 1-16 conditions and actions; priority 0-99999.
Rules run on the server after new mail is persisted and threaded, even when no app
is open; backfilled older mail is not processed. Without a `folder` condition a rule
applies only to Inbox. Mail actions reuse the remote-first mail operation service;
actions run in order and `stopProcessing` skips later rules. A transient failure
leaves the mail pending for the next sync; one mail's failure never blocks the rest.

### Templates

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/templates` | bearer | List compose templates, ordered by name |
| POST | `/api/templates` | bearer | Create a template (201) |
| PUT | `/api/templates/{id}` | bearer | Replace a template |
| DELETE | `/api/templates/{id}` | bearer | Delete a template (204) |

Body: `{name, subject?, bodyText?, bodyHtml?}`; response adds `id`, `createdAt`, `updatedAt`.
`name` is trimmed, 1-100 characters and unique per account case-insensitively
(otherwise 409 `template_name_taken`). `subject` is trimmed, single-line and at most
500 characters (empty allowed). At least one of `bodyText`/`bodyHtml` must be
non-blank; each is limited like send (`MaxSendBodyChars`). Validation failures return
a 400 validation problem keyed by `name`, `subject` or `body`. Another account's
template id returns 404. Templates are deleted with their account.

### Snippets

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/snippets` | bearer | List quick snippets in display order (`sortOrder`, then creation time) |
| POST | `/api/snippets` | bearer | Create a snippet (201); without `sortOrder` it is appended last |
| PUT | `/api/snippets/{id}` | bearer | Replace a snippet; without `sortOrder` the position is kept |
| DELETE | `/api/snippets/{id}` | bearer | Delete a snippet (204) |

Body: `{title?, text, sortOrder?}`; response adds `id`, `createdAt`, `updatedAt`.
Snippets are short reusable body texts without a subject. `text` is trimmed and
1-2000 characters; `title` is optional, trimmed and at most 100 characters;
`sortOrder` is 0-99999. Validation failures return a 400 validation problem keyed by
`text`, `title` or `sortOrder`. Another account's snippet id returns 404. Snippets
are deleted with their account.

### Trusted senders

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/trusted-senders` | bearer | List senders and domains whose remote images load automatically |
| POST | `/api/trusted-senders` | bearer | Trust a sender or domain (201; an existing entry returns 200) |
| DELETE | `/api/trusted-senders/{id}` | bearer | Stop trusting a sender or domain (204) |

Body: `{kind: "Sender" | "Domain", value}`; response adds `id`, `createdAt`.
`value` is trimmed and lower-cased; a `Domain` value may start with `@`. An
invalid address or domain returns a 400 validation problem keyed by `value`.
When the sender address or its domain is trusted, `GET /api/mails/{id}` behaves
as if `remoteContent=allow` was passed, except for mail in Junk or mail whose
`Authentication-Results` report `dmarc=fail`. Sanitization is unchanged. Another
account's entry returns 404. Entries are deleted with their account.

### Mail

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/mails` | bearer | List mail (`folderId`, `isRead`, `hasAttachments`, `search`, `page`, `pageSize` ≤ 100) |
| GET | `/api/mails/{id}` | bearer | Mail detail (`remoteContent=allow` permits sanitized HTTP(S) image sources for this response; `isFromMe`: Sent/Drafts mail, or sender equals the account address case-insensitively, in any folder) |
| GET | `/api/mails/{id}/headers` | bearer | Fetch all original headers from IMAP (`{headers:[{name,value}]}`) |
| GET | `/api/mails/{id}/source` | bearer | Download original MIME as `message/rfc822` (requires online IMAP) |
| GET | `/api/mails/{id}/signature` | bearer | Verify original S/MIME signature, or return `Unverifiable` for OpenPGP; 422 if unsigned |
| GET | `/api/search` | bearer | Search cached mail (all filters optional, AND-combined; see below) |
| GET | `/api/search/remote` | bearer | User-triggered generic IMAP search; imports missing matches before a follow-up `/api/search` |
| GET | `/api/mails/{mailId}/attachments/{attachmentId}` | bearer | Download an attachment (Range/206 when storage is seekable) |
| GET | `/api/compose/limits` | bearer | Current attachment size and count limits |
| POST | `/api/mails/send` | bearer + `Idempotency-Key` | Send mail (multipart/form-data; optional `identityId` selects a sender identity, otherwise the account address) |
| GET | `/api/mails/{id}/compose/reply · reply-all · forward` | bearer | Prefilled compose context |

Remote image URLs are neutralized by default. `remoteContent=allow` restores only
sanitized HTTP(S) `<img src>` values; scripts, event handlers, forms, unsafe URI
schemes and non-image remote resources stay blocked. The response body includes
`remoteImageHosts` and `remoteImagesAllowed` for client state.

`trackingPixelHosts` lists hosts of remote images that look like tracking pixels (width or height of 1px or less, or hidden with `display:none`/`visibility:hidden`). These stay blocked even with `remoteContent=allow`, so clients can show that tracking content was blocked.

Mail detail `security` reports recognized MIME/inline OpenPGP `signed`/`encrypted` standards (`SMime` or `OpenPgp`) without implying trust or successful decryption. The signature endpoint returns `{standard,status,signers}`; `status` is `Valid`, `Untrusted`, `Invalid`, or `Unverifiable`. `Valid` means the cryptographic signature and certificate chain validated; never treat `Unverifiable` as valid. Source endpoints require a matching IMAP UIDVALIDITY and an online provider; errors include `mail_not_found` (404), `mail_account_needs_reauthentication` or `mail_operation_conflict` (409), and `mail_provider_unavailable` (502). Raw MIME and full headers contain private information: display or share only at the user’s request.

Send accepts optional `requestReadReceipt=true` (adds `Disposition-Notification-To` with the account/selected identity address) and `requestDeliveryReceipt=true` (SMTP DSN success/failure request, only if the server advertises DSN; otherwise 422 `delivery_receipt_not_supported` before sending). Neither guarantees a receipt: the recipient or server can ignore the request. Invalid booleans return 400 `invalid_receipt_option`. Receipt options are included in the idempotency fingerprint.

Mail detail includes `authentication` when the topmost stored `Authentication-Results` header contains an SPF, DKIM, or DMARC result. It is otherwise `null`. Values are limited to `pass`, `fail`, `softfail`, `neutral`, `none`, `temperror`, `permerror`, and `policy`, together with the header's `authservId`. The server does not re-run authentication; clients must present these values as informational only.

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

### Scheduled sends

| Method | Path | Auth | Description |
|---|---|---|---|
| POST | `/api/scheduled-sends` | bearer + `Idempotency-Key` | Schedule a mail for future delivery (multipart/form-data) |
| GET | `/api/scheduled-sends` | bearer | List account-scoped scheduled sends with attempts and failure state |
| GET | `/api/scheduled-sends/{id}` | bearer | Get editable content and staged attachment metadata |
| PUT | `/api/scheduled-sends/{id}` | bearer | Atomically replace a Pending send (multipart/form-data; repeated `keepAttachmentIds` retain staged files, new files are uploaded) |
| POST | `/api/scheduled-sends/{id}/reschedule` | bearer + new `Idempotency-Key` | Copy a Failed send to a new Pending send |
| DELETE | `/api/scheduled-sends/{id}` | bearer | Cancel Pending or discard Failed content |

`PUT` uses the same recipients, body, subject, time and attachment limits as
create. It returns 409 `scheduled_send_already_sent` after dispatch starts,
`scheduled_send_not_pending` for Failed/Cancelled rows, or
`scheduled_send_modified` when another edit won. None of those conflicts
change content. DeliveryUnknown sends are never retried or cancelled.
### Signatures

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/signatures` | bearer | List named signatures plus the per-mode defaults (`{items, defaults}`) |
| POST | `/api/signatures` | bearer | Create a signature (201) |
| PUT | `/api/signatures/{id}` | bearer | Replace a signature |
| DELETE | `/api/signatures/{id}` | bearer | Delete a signature (204); defaults pointing at it are cleared |
| PUT | `/api/signatures/defaults` | bearer | Replace all three defaults (`{newMailSignatureId?, replySignatureId?, forwardSignatureId?}`; null clears that mode) |

Body: `{name, bodyText, bodyHtml?}`; response adds `id`, `createdAt`, `updatedAt`.
Signatures are client-inserted text the server never appends to outgoing mail:
the app picks the default for the compose mode (new, reply/reply-all, forward)
and inserts its body into the editor. `name` is trimmed, 1-100 characters;
`bodyText` is trimmed, 1-10000 characters; `bodyHtml` is an optional variant
trimmed and at most 50000 characters. Validation failures return a 400
validation problem keyed by `name`, `bodyText` or `bodyHtml`. A defaults id
that is not owned by the account returns 404 `signature_not_found`, as does
another account's signature id. Signatures are deleted with their account.
`PUT /api/account/signature` is the legacy alias of the new-mail default:
non-blank text creates or updates it (and initializes the reply/forward
defaults when unset); blank text clears every mode pointing at it.

### Identities

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/identities` | bearer | List sender identities (default first) |
| POST | `/api/identities` | bearer | Create an identity (201) |
| PUT | `/api/identities/{id}` | bearer | Replace an identity |
| DELETE | `/api/identities/{id}` | bearer | Delete an identity (204); blocked while a pending scheduled send uses it |

Body: `{emailAddress, displayName?, replyTo?, signatureId?, isDefault}`;
response returns the same fields plus `id`. At most one default per account:
creating or updating with `isDefault: true` clears the previous default.
`emailAddress` must be a bare address (no display name) at most 320
characters and unique per account case-insensitively (otherwise 409
`identity_already_exists`); `displayName` is at most 250 characters;
`replyTo` is an optional bare address; `signatureId` must be owned by the
account (otherwise 404 `signature_not_found`). Validation failures return a
400 validation problem keyed by `emailAddress`, `displayName` or `replyTo`.
Another account's identity id returns 404 `identity_not_found`. Deleting an
identity referenced by a pending scheduled send returns 409 `identity_in_use`.
Send, draft and scheduled-send accept an optional `identityId` form field:
the message goes out with that identity's address, display name and Reply-To
(a draft sent later resolves the identity from its stored From address).
Identities are deleted with their account.

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
