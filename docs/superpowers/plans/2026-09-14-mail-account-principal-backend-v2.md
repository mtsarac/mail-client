# MailAccount Principal Backend V2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Execute inline with test-driven-development. User explicitly forbids subagents.

**Goal:** Replace User-owned multi-mailbox backend with a .NET 10 backend where each MailAccount is an independent authenticated principal.

**Architecture:** Preserve current backend verbatim under `legacy-backend/`, then build a clean four-project backend. Reuse stable protocol, sync, storage, security, logging, and push code only after removing User ownership and direct password assumptions.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core 10, PostgreSQL/Npgsql, MailKit, Serilog, Data Protection, xUnit, Testcontainers/GreenMail.

## Global Constraints

- Do not modify `frontend/`.
- No application `User`, roles, approval, registration, password hashes, admin endpoints, or `UserId` in new backend.
- Manual setup bypasses discovery only; SSRF, DNS, TLS, protocol, and authentication checks remain mandatory.
- Password and AppSpecificPassword work initially; OAuth2 remains explicit and unimplemented.
- New schema uses one clean Initial migration and a separate `mailclient_v2` database default.
- Every network API accepts `CancellationToken` and finite timeout.
- No secrets in logs, audit metadata, responses, or plaintext persistence.

---

### Task 1: Preserve legacy and establish clean solution

**Files:** Move `backend/**` to `legacy-backend/**`; create new `backend/MailClient.slnx`, `Directory.Build.props`, four source projects, and test projects.

**Produces:** Buildable clean dependency graph: Domain <- Application <- Infrastructure; API references Application and Infrastructure.

- [ ] Record current tracked backend tree and verify clean source state.
- [ ] Rename tracked backend to legacy-backend without content edits.
- [ ] Create .NET 10 projects and project references.
- [ ] Add architecture tests that reject forbidden project references.
- [ ] Run architecture test and full build.
- [ ] Commit `chore: preserve legacy backend and scaffold v2`.

### Task 2: Domain and clean EF schema

**Files:** `backend/src/MailClient.Domain/**`, `backend/src/MailClient.Infrastructure/Persistence/**`, `backend/tests/MailClient.Infrastructure.Tests/Persistence/**`.

**Produces:** MailAccount-principal entities, direct MailAccountId ownership, indexes, and Initial migration.

- [ ] Write failing model and EF metadata tests for entity set, ownership, uniqueness, and forbidden User symbols.
- [ ] Add MailAccount, MailCredential, MailSession, mail/sync/send/device/audit entities and enums.
- [ ] Add EF configurations and DbContext.
- [ ] Generate clean Initial migration.
- [ ] Run model, migration, and compile checks.
- [ ] Commit `feat(db): add MailAccount principal schema`.

### Task 3: Credential protection and refresh sessions

**Files:** `backend/src/MailClient.Application/Authentication/**`, `backend/src/MailClient.Infrastructure/Security/**`, `backend/src/MailClient.Infrastructure/Authentication/**`, matching tests.

**Produces:** Encrypted provider credentials, JWT `sub=MailAccountId`, hashed rotating refresh sessions, logout.

- [ ] Write failing tests for encryption, JWT subject, secure token generation, hash-only persistence, rotation, reuse rejection, and revocation.
- [ ] Adapt Data Protection credential protector with versioned purpose.
- [ ] Implement refresh-token generator/hasher and transactional session service.
- [ ] Implement JWT issuer and current-account context contract.
- [ ] Run focused tests.
- [ ] Commit `feat(auth): add mailbox sessions and credential protection`.

### Task 4: Safe deterministic discovery

**Files:** `backend/src/MailClient.Application/Discovery/**`, `backend/src/MailClient.Infrastructure/Discovery/**`, `backend/src/MailClient.Infrastructure/Network/**`, matching tests.

**Produces:** Known provider, DNS SRV, autoconfig, Autodiscover, heuristic strategies; validation; temporary discovery state.

- [ ] Write failing ordering/fallback/first-valid/failure tests.
- [ ] Write failing SSRF, port, TLS mode, redirect, localhost/private address tests.
- [ ] Adapt outbound host validator and connection-time validated destination handling.
- [ ] Implement strategies in fixed order with per-strategy and overall timeouts.
- [ ] Implement bounded in-memory discovery state with opaque IDs and expiry.
- [ ] Run focused discovery/network tests.
- [ ] Commit `feat(discovery): add safe mail server discovery`.

### Task 5: Account connection use cases

**Files:** `backend/src/MailClient.Application/Accounts/**`, `backend/src/MailClient.Infrastructure/Accounts/**`, matching tests.

**Produces:** Automatic and manual connect paths converging on one validated persistence flow.

- [ ] Write failing tests for username order, IMAP/SMTP validation, no persistence on failure, duplicate account reuse, encrypted credential, session creation, and initial-sync enqueue.
- [ ] Implement Password and AppSpecificPassword authentication provider.
- [ ] Implement automatic connect consuming immutable discovery state.
- [ ] Implement manual connect applying identical host/TLS/protocol/auth validation.
- [ ] Implement normalized create-or-reuse transaction and folder discovery.
- [ ] Run focused tests.
- [ ] Commit `feat(accounts): connect discovered and manual mailboxes`.

### Task 6: Adapt proven mail infrastructure

**Files:** `backend/src/MailClient.Infrastructure/Email/**`, `Services/**`, `Storage/**`, `Push/**`, Application contracts, matching tests.

**Produces:** Account-scoped folder, sync, query, read, attachments, send/idempotency, push, reauthentication behavior.

- [ ] Port pure legacy tests first and confirm failures against missing implementation.
- [ ] Adapt MailKit helpers and credential loading abstraction.
- [ ] Adapt folder discovery, UIDVALIDITY, skipped UID, cursor, read-state, MIME, and attachment storage.
- [ ] Adapt send/idempotency to `(MailAccountId, IdempotencyKey)` and preserve SMTP/Sent-copy safety.
- [ ] Adapt Firebase/device ownership to MailAccount/session.
- [ ] Add transient-versus-auth failure policy and NeedsReauthentication transition.
- [ ] Run unit, PostgreSQL concurrency, and GreenMail tests.
- [ ] Commit `feat(mail): adapt account-scoped mail infrastructure`.

### Task 7: API, ProblemDetails, logging, audit, OpenAPI

**Files:** `backend/src/MailClient.Api/**`, audit implementation, API tests.

**Produces:** Requested endpoints, account context, stable errors, limits, redaction, audit, Swagger examples.

- [ ] Write failing API tests for discovery failure 422, manual fallback, refresh/logout, JWT subject, 404 isolation, rate partition, and OpenAPI metadata.
- [ ] Add focused DI extension modules and thin Program.cs.
- [ ] Map account/auth/folder/mail/device/health endpoints with complete metadata.
- [ ] Add stable ProblemDetails code mapper and correlation IDs.
- [ ] Adapt Serilog/HTTP logging and extend recursive secret redaction.
- [ ] Adapt audit logger/actions to nullable MailAccountId.
- [ ] Run API tests and generate/read OpenAPI document.
- [ ] Commit `feat(api): expose account-scoped mailbox API`.

### Task 8: Documentation and final verification

**Files:** root README, `.github/workflows/ci.yml`, `documentation/backend/*.en.md`, `*.tr.md`, backend configs.

**Produces:** Bilingual current docs, CI coverage for both backend paths as appropriate, verified release build.

- [ ] Update English and Turkish architecture, API, backend, development, and security docs.
- [ ] Document migration, automatic/manual flow, error codes, sessions, OAuth deferral, security, logs, and audit.
- [ ] Update CI path filters and backend commands.
- [ ] Run `dotnet restore`, Release build, tests, format, migration script/model check, OpenAPI check, and forbidden-term search.
- [ ] Verify no frontend diff and review complete repository diff.
- [ ] Commit docs and CI in independent semantic commits.
- [ ] Report exact results, deferred OAuth/provider work, and no push/PR.
