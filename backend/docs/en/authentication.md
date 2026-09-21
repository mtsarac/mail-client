# Authentication

🇹🇷 [Türkçe](../tr/authentication.md)

The API has no separate user table: signing in **is** connecting a mailbox.
A `MailAccount` (one per mailbox) can have many `MailSession`s, one per device.

## Tokens

| Token | Format | Lifetime |
|-------|--------|----------|
| Access token | JWT (HS256), `sub` = account id | 15 min (`Jwt__AccessTokenMinutes`) |
| Refresh token | Opaque, stored hashed per session | 180 days, sliding (`Session:*`) |

Send `Authorization: Bearer <accessToken>`. On `401`, call `POST /api/auth/refresh`.
Refresh tokens **rotate**: each call invalidates the old one and returns a new
pair; reusing an old token fails with `invalid_refresh_token`.

## Sign-in flows

1. **Password / app password (discovered)** — `POST /api/accounts/discover` →
   `POST /api/accounts/connect` with `discoveryId` + password. The server
   validates the credentials against IMAP/SMTP before creating the account.
2. **Manual servers** — `POST /api/accounts/connect-manual` with host/port settings
   (outbound hosts are checked against SSRF rules).
3. **OAuth2 (Google, Microsoft)** — `POST /api/accounts/oauth/{provider}/start`
   returns the authorization URL and `state`; after the provider redirect, the app
   sends `state` + `code` to `.../complete`. Authorization Code + PKCE; provider
   tokens never reach the client. Requires `OAuth__*` config.
4. **Existing mailbox, new device** — `POST /api/accounts/login` verifies the
   mailbox credentials and creates a new session on the existing account.

All four return the same `TokenResponse` (access + refresh + expiry).
`connect*` only *creates*; use `login` for an account that already exists.

## Sessions and devices

- `GET /api/account/sessions` lists sessions; `DELETE /api/account/sessions/{id}`
  revokes one remotely (that device gets `401` on next refresh).
- `POST /api/auth/logout` revokes the calling device's session.
- `POST /api/devices` registers a Firebase push token for the account.
- `POST /api/account/reconnect` updates stored credentials after the mailbox
  password changed (a `reauthentication` push tells the app when credentials stop working).

## Credential storage

Mailbox passwords/OAuth tokens are encrypted with ASP.NET Data Protection
(`CredentialProtector`). Keys live in `DataProtection__KeyPath`; in production
protect them with a certificate (`DataProtection__CertificatePath`, see
[Docker](../DOCKER.md)). Losing the key ring makes stored credentials unreadable.

## Email allowlist (optional)

Off by default. When `Whitelist.Enabled` is on (runtime setting):

- only allowlisted emails can create or sign in to a mailbox;
- removing an email disables that mailbox immediately;
- a background job (`ReconciliationIntervalMinutes`, default 15) disables accounts
  that are no longer allowed and, after `DataRetentionGraceDays` (default 30),
  deletes their data.

Manage it with the management API (`X-Management-Key` header, requires
`Management__Enabled=true`):

```bash
curl -X POST "$API/api/management/whitelist/emails" \
  -H "X-Management-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"emails":["person@example.com"]}'
```


## Management API

`/api/management/*` is not JWT-protected. It requires the `X-Management-Key`
header, is disabled unless `Management__Enabled=true`, and in production refuses
to start without `Management__ApiKey`. Keep it off the public internet.
