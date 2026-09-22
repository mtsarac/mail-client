# INFRASTRUCTURE KNOWLEDGE BASE

## OVERVIEW
Provider, persistence, mail transport, security, storage, and external-service integrations. This is largest backend layer.

## STRUCTURE
- `Services/` — sync, send, read, mutate, reconciliation, conversation logic.
- `Mail/` — MailKit and IMAP transport.
- `Email/` — MIME mapping, HTML rendering, message construction.
- `Push/` — Firebase setup and notification delivery.
- `Migrations/` — generated EF Core migrations; not hand-maintained source.

## WHERE TO LOOK
| Task | Location | Notes |
|---|---|---|
| EF model and persistence | `AppDbContext.cs`, `Migrations/` | Use EF commands for schema changes. |
| Remote mail | `Mail/`, `MailConnectionHelper.cs` | Preserve TLS/auth and IMAP UID semantics. |
| Account connection | `AccountConnectionService.cs`, `MailSessionService.cs` | Keep credentials protected and sessions bounded. |
| Discovery/SSRF | `NetworkDiscoveryStrategies.cs`, validators/handlers | Validate outbound hosts and manual setup. |
| Attachments | storage services | Keep paths account-safe and runtime data out of Git. |

## CONVENTIONS
- Mail mutations are remote-first; respect IMAP UID/UIDVALIDITY and reconciliation behavior.
- MailKit/IMAP/SMTP details stay here, not in Domain, Application, or API.
- Account-owned queries and writes use authenticated `MailAccount` scope.
- Preserve secret redaction, HTML sanitization, attachment limits, and outbound-host validation.

## ANTI-PATTERNS
- Do not hand-create migration files or edit `*.Designer.cs` / `AppDbContextModelSnapshot.cs`.
- Do not modify migrations already merged to `main`; add a new migration.
- Do not broaden warning suppressions. Generated EF suppressions and narrowly scoped Firebase compatibility suppression are intentional.
