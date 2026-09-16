# TEST KNOWLEDGE BASE

## OVERVIEW
Single active xUnit project covering API behavior, service units, security invariants, architecture, persistence, and gated external integrations.

## WHERE TO LOOK
| Test type | Location | Notes |
|---|---|---|
| API tests | `AccountApiTests.cs`, `MailListApiTests.cs`, `ConversationApiTests.cs` | `WebApplicationFactory<Program>` with isolated EF InMemory databases. |
| Sync/service tests | `SyncServiceTests.cs`, `MailOperationServiceTests.cs`, `SendServiceTests.cs` | Reuse fakes from `SyncDoubles.cs`. |
| Security tests | `SecurityTests.cs`, `SessionSecurityTests.cs`, `MailBodySecurityTests.cs` | Host validation, auth/session hardening, and HTML/scheme filtering. |
| Architecture/model tests | `ArchitectureTests.cs`, `DomainModelTests.cs`, `PersistenceModelTests.cs` | Domain dependency boundary and EF model behavior. |
| PostgreSQL | `PostgresIntegrationTests.cs` | Requires `MAILCLIENT_TEST_POSTGRES_ADMIN`; isolated test DB. |
| IMAP/SMTP | `GreenMailIntegrationTests.cs` | Requires GreenMail opt-in variables. |

## FIXTURES
- `MailClientApiFactory` and subclasses in `AccountApiTests.cs` configure in-memory API tests and validator doubles.
- `SyncDoubles.cs` contains fake remote folders, file storage, push notifications, DNS, and protector implementations.
- `IntegrationEnvironment.cs` centralizes `MAILCLIENT_SKIP_INTEGRATION`, PostgreSQL, and GreenMail gates.

## CONVENTIONS
- Plain `dotnet test` must skip external-service tests safely; no hidden service dependency.
- Prefer focused tests for behavior, security, account isolation, UID/UIDVALIDITY, and error contracts.
- Keep external tests gated and use them only where PostgreSQL, MailKit, IMAP, SMTP, or concurrency behavior matters.

## ANTI-PATTERNS
- Do not delete or skip failing tests to make CI green.
- Do not add integration tests only to increase coverage.
- Do not expose credentials or account identifiers in logs/assertions for anonymous requests.
