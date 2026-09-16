# API KNOWLEDGE BASE

## OVERVIEW
.NET 10 Minimal API contract and composition root. `Program.cs` owns middleware, DI, auth, persistence, and endpoint mapping.

## WHERE TO LOOK
| Task | Location | Notes |
|---|---|---|
| Startup and middleware | `Program.cs`, `StartupConfig.cs` | Preserve order; validation runs during startup. |
| HTTP contracts | `Endpoints/*.cs` | Keep endpoint groups thin; delegate behavior to Application/Infrastructure. |
| Error responses | `Endpoints/ApiFailureMapper.cs` | Stable `mail_*` error codes and status mapping. |
| Account scope | `Auth.cs` | `ICurrentMailAccount` resolves authenticated `MailAccountId`. |
| Request logging | `HttpLogging.cs` | Redaction, size caps, and anonymous-account behavior are security-sensitive. |

## CONVENTIONS
- Endpoint registration uses `Map*Endpoints()` functions in `Endpoints/`.
- JWT `sub` is the `MailAccountId`; account-owned work must remain scoped to it.
- Credentials are never returned. Preserve SSRF, TLS, credential, HTML, and attachment safeguards.
- API contracts and stable error codes require explicit task scope before changing.

## ANTI-PATTERNS
- Do not reorder middleware casually; auth, correlation, logging, rate limiting, and exception mapping depend on composition order.
- Do not put MailKit/IMAP/SMTP implementation in API code; keep provider details in Infrastructure.
- Do not log request secrets or emit `MailAccountId` for anonymous requests.
