---
title: 'Story 2.2: Idempotent store'
type: 'feature'
created: '2026-08-21'
status: 'done'
baseline_commit: 'f9a894565c7d473a7c199414ace6d04fd03859e3'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-2-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Producers need a race-safe `StoreIfNewAsync` so duplicates never create second rows, with unique-violation detection that works on both SQLite (test tier) and Postgres (runtime) — the legacy code check-then-inserted on the inbox and caught Postgres-only error codes (FR-3, FR-19; AD-4 violations flagged in epic context).

**Approach:** TDD-build the repository base classes' storage layer in the kernel: abstract `InboxMessageRepositoryBase<TMessage,TDbContext>` / `OutboxMessageRepositoryBase<TMessage,TDbContext>` with `StoreIfNewAsync` (insert + catch unique violation via one provider-aware helper — never check-then-insert), plus the base message entity configuration hooks that let each store define its dedupe unique index (command stores: key alone; event stores: composite — AD-4).

## Boundaries & Constraints

**Always:** Insert-then-catch, never check-then-insert; one shared `IsUniqueViolation(DbUpdateException)` helper covering SQLite error 19/2067 and Postgres 23505 (via exception shape, not provider assembly references — inspect inner exception type name/SqlState property reflectively or via Npgsql reference); losers report "already exists" without throwing and without corrupting the DbContext change tracker (detach the failed entity); repository behavior tested on SQLite in-memory per AD-9 tier 2.

**Ask First:** Adding new NuGet package references beyond what the old Messaging csproj had (EF Core).

**Never:** Claiming/retry/poison logic (story 2.3); processor loops (2.4/2.5); Postgres-only test dependencies.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| First store | New message, empty store | Row inserted, result "stored" | N/A |
| Duplicate | Same dedupe identity again | No second row, result "already exists", no throw | Unique violation swallowed via helper |
| Concurrent duplicates | Two contexts storing same identity | Exactly one row; loser gets "already exists" | Tracker left clean (subsequent ops work) |
| Distinct identities | Same key, different EventType (event store) | Both rows stored (composite dedupe) | N/A |
| Non-unique-violation failure | Other DbUpdateException | Propagates unchanged | rethrow |

</frozen-after-approval>

## Code Map

- `docs/bmad/implementation-artifacts/epic-2-context.md` — Legacy Behavioral Reference: old repository signatures/semantics (copy semantics, fix flagged violations); MessageConstants/interfaces from story 2.1 (committed f9a8945)
- `CoreBankDemo.Messaging/CoreBankDemo.Messaging.csproj` — check old EF Core package refs survive; has InternalsVisibleTo for the test project
- New: `CoreBankDemo.Messaging/InboxMessageRepositoryBase.cs`, `OutboxMessageRepositoryBase.cs` (or a shared `MessageRepositoryBase` if duplication demands — implementer's call), `UniqueViolation.cs` helper
- Tests: `tests/CoreBankDemo.Messaging.Tests/` — SQLite in-memory fixture (`Microsoft.EntityFrameworkCore.Sqlite` pinned; open connection kept alive), concrete test message entity + DbContext + repository subclasses exercising both dedupe-index shapes

## Tasks & Acceptance

**Execution:**
- [x] Tests first: SQLite fixture + concrete test store (command-shape unique index on key; event-shape composite index) covering the full I/O matrix
- [x] `UniqueViolation.cs` — provider-aware detection helper, unit-tested against both real SQLite violations and a faked Npgsql-shaped exception
- [x] Repository bases with `StoreIfNewAsync` + entity-config hooks for dedupe indexes
- [x] Coverage: kernel stays ≥90% (gate live)

**Acceptance Criteria:**
- Given two sequential and two concurrent stores of the same identity (SQLite), when both complete, then exactly one row exists and no exception escapes
- Given a store failure that is not a unique violation, when it occurs, then the original exception propagates
- Given the loser's DbContext, when reused after the violation, then subsequent operations succeed (tracker detached)

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — expected: green, Messaging ≥90%

## Spec Change Log

- 2026-08-21 (step-04): review patches: StoreIfNewAsync now detaches the entity on ANY save failure (not just unique-violation) via catch-all detach+rethrow; ConfigureDedupeIndex validates property names exist on TMessage and rejects duplicates; added null-array, non-unique-violation-reuse, composite-shape-concurrency, and direct-detached-state tests. Messaging: 100% line / 90% branch / 100% method, 70 tests green.
