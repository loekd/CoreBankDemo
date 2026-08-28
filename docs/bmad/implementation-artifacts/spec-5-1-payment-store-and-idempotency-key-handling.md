---
title: 'Story 5.1: Payment store and idempotency-key handling'
type: 'feature'
created: '2026-08-28'
status: 'done'
review_loop_iteration: 0
baseline_commit: '74fd01083d06b29c77f8f981b30d3723d3559909'
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-5-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** PaymentsAPI is still the incompatible legacy implementation and is excluded from the rebuild gate. Epic 5 needs a schema-enforced, race-safe payment store whose identity, partition, timestamp, and trace context are correct before the HTTP endpoint is rebuilt.

**Approach:** Demolish the legacy PaymentsAPI C# sources and rebuild only its message entities, DbContext, outbox repository, payment-storage handler, and minimal host. Admit the project to `CoreBankDemo.Rebuild.slnf`; endpoint mapping, forwarding, and event consumption remain later stories.

## Boundaries & Constraints

**Always:** `OutboxMessage` implements `IOutboxMessage` and stores internal `Id`, `IdempotencyKey`, identical `TransactionId`, payment fields, partition/status/retry/timestamps, and trace context. `InboxMessage` implements `IInboxMessage` and stores `TransactionId`, `EventType`, required `AccountNumber` (empty string for transaction-wide events), payload, partition/status/retry/timestamps, and trace context. Configure outbox uniqueness on `IdempotencyKey`; inbox uniqueness on `(TransactionId, EventType, AccountNumber)`; both get `(PartitionId, Status, ordering timestamp)`, status, and ordering-time indexes plus kernel concurrency tokens. The storage handler accepts `PaymentRequest` and an optional key: only `null` means absent and generates `Guid.NewGuid().ToString("D")`; every supplied string is preserved byte-for-byte. It uses `PartitionHelper` with validated `OutboxProcessingOptions.PartitionCount=4`, injected `TimeProvider`, `Activity.Current` trace context, `MessageConstants.Status.Pending`, and race-safe `StoreIfNewAsync`. A duplicate returns the persisted winner, never the unsaved candidate.

**Ask First:** Changing the frozen payment DTO, treating empty/whitespace keys as absent, changing the four-partition rule, or moving HTTP behavior into this story.

**Never:** Reimplement hashing or insert dedupe; call `DateTime.Now`/`UtcNow`; add controllers, CoreBank clients, hosted processors, Dapr event endpoints, or event handlers; modify Messaging or ServiceDefaults; remove centrally managed packages needed by later Epic 5 stories.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|---------------|---------------------------|----------------|
| Caller key | Any non-null key, including whitespace | Stored verbatim; `TransactionId` matches; partition uses that exact string | N/A |
| Missing key | `null` | GUID in canonical `D` format becomes both identities | N/A |
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
- `CoreBankDemo.PaymentsAPI/Handlers/PaymentStorageHandler.cs` -- key generation, partitioning, timestamps, trace capture, duplicate-winner result.
- `CoreBankDemo.PaymentsAPI/Program.cs` and `appsettings.json` -- minimal DbContext/options/handler wiring, `EnsureCreatedAsync`, partition count 4, no dead flags/options.
- `CoreBankDemo.Rebuild.slnf` and `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankDemo.PaymentsAPI.Tests.csproj` -- admit production project, target its coverage, reference SQLite, remove threshold bypass.
- `tests/CoreBankDemo.PaymentsAPI.Tests/{PaymentsDbContextTests,OutboxRepositoryTests,PaymentStorageHandlerTests}.cs` -- SQLite schema/race behavior and mocked handler matrix.
- `CoreBankDemo.Messaging/{MessageRepositoryBase.cs,OutboxMessageRepositoryBase.cs,PartitionHelper.cs}` -- reuse unchanged.

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.PaymentsAPI/**/*.cs` -- demolish legacy and recreate the minimal Story 5.1 model/host boundary.
- [x] `tests/CoreBankDemo.PaymentsAPI.Tests/*.cs` -- replace smoke test with test-first schema, repository, and handler coverage for every matrix row.
- [x] `CoreBankDemo.Rebuild.slnf` and PaymentsAPI test project -- enable the real rebuild/coverage gate.
- [x] `CoreBankDemo.PaymentsAPI/appsettings.json` -- align partition configuration and delete stale flags/options.

**Acceptance Criteria:**
- Given `PaymentsDbContext`, when its SQLite model and constraints are exercised, then outbox key uniqueness, inbox composite dedupe, partition/status/time indexes, lengths, and concurrency tokens match the approved schema.
- Given payment storage with or without a caller key, when the handler runs, then it persists the exact/provided-or-generated identity, `FNV-1a(key) % 4` partition, injected timestamp, and ambient trace context.
- Given concurrent duplicate storage, when both attempts complete, then exactly one row exists and both results reference the persisted winner.
- Given `dotnet test CoreBankDemo.Rebuild.slnf`, when Story 5.1 is complete, then PaymentsAPI builds in the filter and clears the real >=90% line gate.

## Spec Change Log

## Design Notes

The event inbox schema lands now because `PaymentsDbContext` is the Epic 5 storage foundation, but no event intake behavior is implemented. A required empty-string account sentinel keeps transaction-level event dedupe effective on PostgreSQL and SQLite, where nullable columns would otherwise allow repeated `NULL` values through a unique index.

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: all projects green and PaymentsAPI >=90% line coverage.
- `git diff --check` -- expected: no whitespace errors.

## Suggested Review Order

**Storage flow**

- Handler centralizes exact-key identity, partitioning, trace capture, and race-loser recovery.
  [`PaymentStorageHandler.cs:25`](../../../CoreBankDemo.PaymentsAPI/Handlers/PaymentStorageHandler.cs#L25)

- Repository reuses the kernel store while adding the winner lookup port.
  [`OutboxRepository.cs:17`](../../../CoreBankDemo.PaymentsAPI/Outbox/OutboxRepository.cs#L17)

**Schema guarantees**

- DbContext declares dedupe, claim indexes, lengths, and concurrency tokens.
  [`PaymentsDbContext.cs:13`](../../../CoreBankDemo.PaymentsAPI/PaymentsDbContext.cs#L13)

- Message entities retain kernel transport state and payment/event identities.
  [`OutboxMessage.cs:5`](../../../CoreBankDemo.PaymentsAPI/Outbox/OutboxMessage.cs#L5)
  [`InboxMessage.cs:5`](../../../CoreBankDemo.PaymentsAPI/Inbox/InboxMessage.cs#L5)

**Host and verification**

- Minimal host validates four partitions and wires only Story 5.1 services.
  [`Program.cs:6`](../../../CoreBankDemo.PaymentsAPI/Program.cs#L6)

- Handler tests cover exact keys, GUIDs, traces, failures, and concurrent duplicates.
  [`PaymentStorageHandlerTests.cs:21`](../../../tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStorageHandlerTests.cs#L21)

- SQLite tests prove schema metadata and database-enforced uniqueness.
  [`PaymentsDbContextTests.cs:13`](../../../tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsDbContextTests.cs#L13)
