---
title: 'Story 5.1: Payment store and idempotency-key handling'
type: 'feature'
created: '2026-08-28'
status: 'done'
review_loop_iteration: 1
baseline_commit: '74fd01083d06b29c77f8f981b30d3723d3559909'
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-5-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** PaymentsAPI is still the incompatible legacy implementation and is excluded from the rebuild gate. Epic 5 needs a schema-enforced, race-safe payment store whose identity, partition, timestamp, and trace context are correct before the HTTP endpoint is rebuilt.

**Approach:** Demolish the legacy PaymentsAPI C# sources and rebuild only its message entities, DbContext, outbox repository, payment-storage handler, and minimal host. Admit the project to `CoreBankDemo.Rebuild.slnf`; endpoint mapping, forwarding, and event consumption remain later stories.

## Boundaries & Constraints

**Always:** `OutboxMessage` implements `IOutboxMessage` and stores internal `Id`, `IdempotencyKey`, identical `TransactionId`, payment fields (`Amount` precision 18,2), partition/status/retry/timestamps, and trace context. `InboxMessage` implements `IInboxMessage` and stores `TransactionId`, `EventType`, required `AccountNumber` (empty string for transaction-wide events), payload, partition/status/retry/timestamps, and trace context. Configure outbox uniqueness on `IdempotencyKey`; inbox uniqueness on `(TransactionId, EventType, AccountNumber)`; both get `(PartitionId, Status, ordering timestamp)`, status, and ordering-time indexes plus kernel concurrency tokens. The storage handler accepts `PaymentRequest` and an optional key: `null` generates `Guid.NewGuid().ToString("D")`; a supplied key with length 1–100 is preserved byte-for-byte (including whitespace); zero-length or longer keys return a domain validation result without calling the repository. It uses `PartitionHelper` with validated `OutboxProcessingOptions.PartitionCount=4`, injected `TimeProvider`, `Activity.Current` trace context, `MessageConstants.Status.Pending`, structured idempotency/partition logging, and race-safe `StoreIfNewAsync`. Results expose an immutable payment snapshot rather than a tracked entity. A duplicate returns a snapshot of the persisted winner, never the unsaved candidate.

**Ask First:** Changing the frozen payment DTO, treating whitespace-only keys as absent, changing the 1–100 key boundary or four-partition rule, or moving HTTP behavior into this story.

**Never:** Reimplement hashing or insert dedupe; call `DateTime.Now`/`UtcNow`; add controllers, CoreBank clients, hosted processors, Dapr event endpoints, or event handlers; modify Messaging or ServiceDefaults; remove centrally managed packages needed by later Epic 5 stories.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|---------------|---------------------------|----------------|
| Caller key | Length 1–100, including whitespace | Stored verbatim; `TransactionId` matches; partition uses that exact string | N/A |
| Missing key | `null` | GUID in canonical `D` format becomes both identities | N/A |
| Invalid caller key | Empty or length >100 | Rejected domain result; no repository call | Validation error identifies the 1–100 limit |
| New payment | No matching row | Pending row stored with injected time and current trace; result is new | Infrastructure errors propagate |
| Duplicate/race loser | Existing unique key or `StoreIfNewAsync=false` | Re-query and return persisted winner; one row remains | Missing winner throws explicit invalid-state exception |
| No ambient activity | `Activity.Current=null` | Trace fields persist as null | N/A |
| Event dedupe | Same transaction/type/account twice | Second SQLite insert/store is rejected/deduped; a different account succeeds | Unique violation follows kernel behavior |

</frozen-after-approval>

## Code Map

- Delete tracked legacy `CoreBankDemo.PaymentsAPI/**/*.cs`; recreate only the files below -- Epic 5 demolition boundary.
- `CoreBankDemo.PaymentsAPI/Models/PaymentRequest.cs` -- recreate the frozen DataAnnotations DTO unchanged for handler input.
- `CoreBankDemo.PaymentsAPI/{PaymentsDbContext.cs,Outbox/OutboxMessage.cs,Inbox/InboxMessage.cs}` -- schema, kernel contracts, dedupe indexes, query indexes, lengths, and concurrency tokens.
- `CoreBankDemo.PaymentsAPI/Outbox/OutboxRepository.cs` -- extend `OutboxMessageRepositoryBase`; narrow store/find port for the handler and future processor reuse.
- `CoreBankDemo.PaymentsAPI/Handlers/PaymentStorageHandler.cs` -- validation, key generation, immutable results, partitioning, timestamps, trace capture, logging, duplicate-winner result.
- `CoreBankDemo.PaymentsAPI/PaymentStorageServiceCollectionExtensions.cs`, `Program.cs`, and `appsettings.json` -- testable exact-four validation plus minimal DbContext/options/handler wiring, `EnsureCreatedAsync`, and no obsolete flags.
- `CoreBankDemo.Rebuild.slnf` and `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankDemo.PaymentsAPI.Tests.csproj` -- admit production project, target its coverage, reference SQLite, remove threshold bypass.
- `tests/CoreBankDemo.PaymentsAPI.Tests/{PaymentsDbContextTests,OutboxRepositoryTests,PaymentStorageHandlerTests,PaymentStorageRegistrationTests}.cs` -- SQLite schema/real concurrent race behavior, handler matrix, and startup-option validation.
- `CoreBankDemo.Messaging/{MessageRepositoryBase.cs,OutboxMessageRepositoryBase.cs,PartitionHelper.cs}` -- reuse unchanged.
- `.claude/skills/messaging-patterns/SKILL.md` -- replace the deleted PaymentsAPI processor reference with the rebuilt CoreBank outbox processor.

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.PaymentsAPI/**/*.cs` -- demolish legacy and recreate the minimal Story 5.1 model/host boundary.
- [x] `tests/CoreBankDemo.PaymentsAPI.Tests/*.cs` -- replace smoke test with test-first schema, repository, and handler coverage for every matrix row.
- [x] `CoreBankDemo.Rebuild.slnf` and PaymentsAPI test project -- enable the real rebuild/coverage gate.
- [x] `CoreBankDemo.PaymentsAPI/appsettings.json` -- align partition configuration and delete stale flags/options.
- [x] `.claude/skills/messaging-patterns/SKILL.md` and dead-option consumer map -- repair references invalidated by demolition and document the temporary Story 5.4 lock/poll consumer gap truthfully.

**Acceptance Criteria:**
- Given `PaymentsDbContext`, when its SQLite model and constraints are exercised, then outbox key uniqueness, inbox composite dedupe, partition/status/time indexes, lengths, and concurrency tokens match the approved schema.
- Given payment storage with or without a caller key, when the handler runs, then it persists the exact/provided-or-generated identity, `FNV-1a(key) % 4` partition, injected timestamp, and ambient trace context.
- Given concurrent duplicate storage through independent repositories against one SQLite database, when both attempts complete, then exactly one row exists and both results identify the persisted winner.
- Given missing or non-four partition configuration, when payment-storage service validation runs, then validation fails before handling payments.
- Given `dotnet test CoreBankDemo.Rebuild.slnf`, when Story 5.1 is complete, then PaymentsAPI builds in the filter and clears the real >=90% line gate.

## Spec Change Log

- 2026-08-28 (review loop 1): resolved the approved intent's key-length contradiction. `null` still generates a canonical GUID; caller keys of length 1–100 remain verbatim; empty or longer values now return a domain validation result before storage. Also required real-store concurrent dedupe coverage, testable exact-four startup validation, explicit amount precision, immutable handler results, structured logs, and repair of the deleted skill reference. KEEP: legacy demolition, kernel repository/hash reuse, trace capture, composite event identity, minimal host, and later-story boundaries.

## Design Notes

The event inbox schema lands now because `PaymentsDbContext` is the Epic 5 storage foundation, but no event intake behavior is implemented. A required empty-string account sentinel keeps transaction-level event dedupe effective on PostgreSQL and SQLite, where nullable columns would otherwise allow repeated `NULL` values through a unique index.

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: all projects green and PaymentsAPI >=90% line coverage.
- `git diff --check` -- expected: no whitespace errors.

## Suggested Review Order

**Storage flow**

- Handler centralizes validation, identity, partitioning, trace capture, and race-loser recovery.
  [`PaymentStorageHandler.cs:47`](../../../CoreBankDemo.PaymentsAPI/Handlers/PaymentStorageHandler.cs#L47)

- Repository reuses the kernel store while adding the winner lookup port.
  [`OutboxRepository.cs:17`](../../../CoreBankDemo.PaymentsAPI/Outbox/OutboxRepository.cs#L17)

**Schema guarantees**

- DbContext declares dedupe, claim indexes, lengths, and concurrency tokens.
  [`PaymentsDbContext.cs:13`](../../../CoreBankDemo.PaymentsAPI/PaymentsDbContext.cs#L13)

- Message entities retain kernel transport state and payment/event identities.
  [`OutboxMessage.cs:5`](../../../CoreBankDemo.PaymentsAPI/Outbox/OutboxMessage.cs#L5)
  [`InboxMessage.cs:5`](../../../CoreBankDemo.PaymentsAPI/Inbox/InboxMessage.cs#L5)

**Host and verification**

- Registration enforces exactly four partitions and exposes only storage services.
  [`PaymentStorageServiceCollectionExtensions.cs:12`](../../../CoreBankDemo.PaymentsAPI/PaymentStorageServiceCollectionExtensions.cs#L12)

- Minimal host wires the database, storage foundation, and schema creation.
  [`Program.cs:6`](../../../CoreBankDemo.PaymentsAPI/Program.cs#L6)

- Handler tests cover exact keys, GUIDs, traces, failures, and concurrent duplicates.
  [`PaymentStorageHandlerTests.cs:21`](../../../tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStorageHandlerTests.cs#L21)

- SQLite tests prove schema metadata and database-enforced uniqueness.
  [`PaymentsDbContextTests.cs:13`](../../../tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsDbContextTests.cs#L13)
