# SERVICE KNOWLEDGE BASE

## OVERVIEW
Core mail behavior: synchronization, remote-first mutations, sending, reads, reconciliation, and conversation threading.

## WHERE TO LOOK
| Task | Location | Notes |
|---|---|---|
| Folder synchronization | `MailFolderSyncService.cs` | Largest service; checkpoint, flags, UIDVALIDITY, and recovery logic. |
| Send flow | `MailSendService.cs`, `SendOperationStore.cs` | Remote send plus idempotency and attachment hashing. |
| Mutations | `MailOperationService.cs` | Move, flag, delete; remote-first conflict behavior. |
| Reconciliation | `MailReconciliationService.cs` | Align remote and local state. |
| Reads | `MailReadService.cs`, `MailQueryService.cs` | Account-scoped projections and content handling. |
| Threads | `ConversationService.cs` | Message-ID/reference graph with subject fallback. |

## CONVENTIONS
- Keep remote-first ordering. Local state must not claim success before provider mutation succeeds.
- Preserve UIDVALIDITY handling, checkpointing, flag reconciliation, idempotency keys, and conflict errors.
- Keep services focused; avoid adding wrappers or shared mutable state without a concrete need.
- Test ordinary behavior with in-memory doubles. Use GreenMail/PostgreSQL only for provider, SQL, concurrency, or UID behavior.

## ANTI-PATTERNS
- Do not replace remote-first operations with local-first updates.
- Do not ignore UIDVALIDITY changes or silently reuse stale UIDs.
- Do not add coverage-only integration tests; test behavior that requires the external service.
