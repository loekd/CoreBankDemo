# Epic 2 Context: E1 — Messaging Kernel

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Rebuild `CoreBankDemo.Messaging` from scratch, test-first, as the single implementation of Inbox/Outbox machinery for the whole system: partition identity, message contracts, race-safe idempotent stores, the claim/retry/poison state machine, and the poll/lock/dispatch processor loops with pluggable delivery. Old kernel sources are deleted at epic start; every class returns only behind a red test, and the kernel finishes at ≥90% line coverage. This epic matters because all four downstream processors (payments outbox/inbox, corebank inbox/messaging-outbox) inherit exactly this behavior — divergence here was the core defect of the original system.

## Stories

- Story 2.1: Identity, constants, and message contracts
- Story 2.2: Idempotent store
- Story 2.3: Claiming, retry, and poison state machine
- Story 2.4: OutboxProcessorBase and delivery strategy port
- Story 2.5: InboxProcessorBase and handler dispatch
- Story 2.6: Kernel failure-path hardening

## Requirements & Constraints

- Inbox/Outbox behavior (idempotent store, partitioned polling, distributed locking, batch claiming, retry/poison handling, trace restoration, status constants) is implemented **once** here and reused by every processor; no service re-implements any of it.
- The kernel exposes seams (interfaces) for locking, delivery, persistence, and time so all pattern logic is unit-testable without infrastructure (Moq tier); store/repository behavior is tested on EF Core SQLite in-memory; Postgres-only semantics are left to the acceptance tier.
- Per-key ordering guarantee: messages with the same idempotency key land in the same partition, and one partition is processed by at most one worker at a time.
- Failure paths are proven by tests, not claims: lock acquisition failure, lock expiry mid-batch, delivery timeout, repository exception, cancellation during dispatch — no message lost or double-completed, next tick always proceeds.
- Coverage gate: ≥90% line, enforced by `tests/Directory.Build.props` through plain `dotnet test CoreBankDemo.Rebuild.slnf` (VSTest runner mode; Microsoft.Testing.Platform must stay off).

## Technical Decisions

Binding architecture rules for this epic: **AD-3, AD-4, AD-7, AD-8, AD-9, AD-11** (plus consistency conventions). Distilled:

- **AD-3 (one kernel, four processors):** every background processor derives from `InboxProcessorBase`/`OutboxProcessorBase`. Delivery is a port — `IOutboxDeliveryStrategy` — so HTTP-forward and Dapr-publish are strategies, never new loops. Polling, partition fan-out, locking, batching, claiming, **stale-claim reclaim** (Processing older than `ProcessingTimeout` becomes claimable again), retry, and trace restoration are all kernel-owned.
- **AD-4 (identity):** idempotency key is a string and is the ordering identity everywhere: `PartitionId = FNV-1a(key) % PartitionCount`, PartitionCount validated = 4. Dedupe identity is **per store**: command stores dedupe on the key alone; event stores dedupe on a composite event identity (e.g. `(TransactionId, EventType, AccountNumber?)`) because one transaction yields three events — the repository base must expose unique-index definition hooks for this. Idempotent stores use `StoreIfNewAsync` (unique index + violation catch), never check-then-insert.
- **AD-7 (locking):** expiry-based, no renewal. Cooperative cancellation fires at 5/6 of lock lifetime. `LockRenewIntervalSeconds` does not exist. No unused option members anywhere.
- **AD-8 (tracing):** `TraceParent`/`TraceState` persist on every message row and are restored as span parent during processing. Spans follow the `observability` skill: registered `ActivitySource` (not ad-hoc `new`), naming/tags including `IdempotencyKey` and `PartitionId`.
- **AD-9 (test tiers):** logic vs SQLite-store vs Postgres-acceptance. Repositories written provider-agnostic (LINQ); unique-violation detection through **one provider-aware helper** covering SQLite and Postgres error codes — never string matching at call sites. Provider-specific SQL pass-throughs (if any) carry individual `[ExcludeFromCodeCoverage]` with a comment, never class-level.
- **AD-11 (delivery outcome contract):** `Status` values are **transport states only**. Business rejection = successfully processed (Completed with cached failure response downstream) — never `Failed`, never retried. `Failed` means transport gave up after `MaxRetryCount`. HTTP strategy classification: any 2xx (incl. duplicate-accepted) → Completed; anything else (4xx/5xx/timeout/exception) → back to Pending with `RetryCount++`, terminal Failed at `MaxRetryCount`. Retry classification lives in the kernel, never inside a strategy.
- **Conventions:** statuses/limits only via `MessageConstants`; injected `TimeProvider` (no `DateTime.Now/UtcNow`); `ILogger<T>` structured with `IdempotencyKey`/`PartitionId`; lock names `<prefix>-partition-<id>` with per-store prefixes (`payments-outbox`, `payments-inbox`, `corebank-inbox`, `corebank-messaging-outbox`); central package management; xUnit v3 + AwesomeAssertions `Should()` + Moq; `EnsureCreated()` only, never migrations.
- Batch claiming semantics (story 2.3): at most BatchSize ids per claim, oldest-first, only rows with `RetryCount < MaxRetryCount`; claimed rows become Processing; stale Processing rows are reclaimable; `ExecuteInTransactionAsync` wraps multi-row updates atomically.
- Processor tick semantics (2.4/2.5): fan out over all 4 partitions in parallel; process a partition only while holding its lock; honor cancellation; no partition processed concurrently by two ticks; per-message dispatch inside a span restored from stored TraceParent; inbox handler resolution per message in a fresh DI scope.

## Cross-Story Dependencies

- **Old sources demolished at epic start:** delete `CoreBankDemo.Messaging/*.cs` (and `Inbox/`, `Outbox/`) in story 2.1 before writing new tests. Behavior to preserve is captured below — the code will be gone.
- **Tests must keep compiling at every story end:** `tests/CoreBankDemo.Messaging.Tests/SmokeTests.cs` and `GateProofTests.cs` both reference `typeof(MessageConstants)`. Story 2.1 must (re)introduce `MessageConstants` in the same demolition-and-rebuild step or these permanent tests break the gate.
- **Carry-forward from epic 1:** `tests/CoreBankDemo.Messaging.Tests/CoreBankDemo.Messaging.Tests.csproj` contains a `<Threshold>0</Threshold>` override (with a TODO naming story 2.1). Story 2.1 must **remove it** so the 90% gate from `tests/Directory.Build.props` applies to the kernel from then on. Keep the `<Include>[CoreBankDemo.Messaging]*</Include>` filter — GateProofTests asserts it verbatim.
- **Epic 3 boundary:** the kernel may keep referencing the *old* `CoreBankDemo.ServiceDefaults` `IDistributedLockService` (and old options classes) until epic 3 rebuilds ServiceDefaults; epic 3 delivers the new lock port with the 5/6-lifetime cancellation handle. Design the kernel's use of the lock seam so swapping the interface in epic 3 is localized. Story 2.6's 5/6-lock-lifetime cancellation test may need the new-style handle semantics — mock accordingly (the kernel consumes a seam; the Dapr implementation is epic 3's).
- Story order is a dependency chain: 2.1 contracts → 2.2 store → 2.3 state machine → 2.4/2.5 processors → 2.6 hardening; 2.5 mirrors 2.4's loop shape.
- Downstream epics 4 and 5 derive all four concrete processors/stores from this kernel; the epic-3 `NoOpDistributedLockService` and options validation also plug into these seams.

## Legacy Behavioral Reference

Extracted verbatim from the old `CoreBankDemo.Messaging` sources (deleted at epic start). **Copy the behavior, not the violations** — deviations required by the new ADs are flagged.

### PartitionHelper — exact FNV-1a algorithm

`public static int GetPartitionId(string key, int partitionCount)`:

- Throws `ArgumentException` for null/empty key and for `partitionCount <= 0`.
- Returns `Math.Abs(ComputeFnv1aHash(key)) % partitionCount`.
- Hash (32-bit FNV-1a over **chars** of the string, in `unchecked` int arithmetic):

```csharp
unchecked
{
    const int fnvPrime = 16777619;        // 0x01000193
    int hash = (int)2166136261;           // FNV offset basis 0x811C9DC5
    foreach (char c in key)
    {
        hash ^= c;
        hash *= fnvPrime;
    }
    return hash;
}
```

Note: char-based (not UTF-8 bytes), `int` (not uint) with `Math.Abs` afterward. Keep this exact algorithm — existing rows/known-vector tests depend on identical partition assignment. (Known-vector tests in 2.1 should pin outputs of representative GUID-string keys computed with this algorithm.)

### MessageConstants — exact values

```csharp
public static class MessageConstants
{
    public static class Status
    {
        public const string Pending = "Pending";
        public const string Processing = "Processing";
        public const string Completed = "Completed";
        public const string Failed = "Failed";
    }
    public static class Defaults
    {
        public const int MaxRetryCount = 5;
        public const int BatchSize = 10;
        public static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);
    }
}
```

### Message interfaces — member lists

- `IMessage`: `Guid Id`, `int PartitionId`, `string Status`, `DateTime? ProcessedAt`, `int RetryCount`, `string? LastError`, `string? TraceParent`, `string? TraceState` (all get/set).
- `IInboxMessage : IMessage`: adds `string IdempotencyKey`, `DateTime ReceivedAt`.
- `IOutboxMessage : IMessage`: adds `string IdempotencyKey`, `DateTime CreatedAt`.
- Story 2.1 requires the contracts to expose id, idempotency/dedupe identity, PartitionId, Status, RetryCount, timestamps, TraceParent/TraceState, LastError — the old lists satisfy this apart from the per-store dedupe identity (AD-4), which the old design did not model.

### Repository bases — signatures and semantics

Both `InboxMessageRepositoryBase<TMessage, TDbContext>` and `OutboxMessageRepositoryBase<TMessage, TDbContext>` (abstract, generic on message + DbContext) took `(TDbContext, TimeProvider)` in the ctor and exposed an abstract `DbSet<TMessage>` property (`InboxMessages` / `OutboxMessages`). Shared methods (all `virtual`, all `Task`-returning with `CancellationToken`):

- `Task<TMessage?> FindByIdempotencyKeyAsync(string idempotencyKey, ct)` — `FirstOrDefaultAsync` on key.
- `Task<bool> StoreIfNewAsync(TMessage message, ct)` — Add + `SaveChangesAsync`; returns `true` on insert; catches `DbUpdateException` where `InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }` and returns `false` without throwing ("loser reports already-exists"). Outbox variant additionally detached the losing entity (`DbContext.Entry(message).State = EntityState.Detached`) before returning false — keep that cleanup. **Violation to fix:** the inbox variant did a pre-check `AnyAsync` before insert (check-then-insert, forbidden by AD-4) and the catch was Postgres-only string-free but provider-specific — the rebuild must rely on the unique index alone and route violation detection through the shared provider-aware helper (SQLite error code + Postgres 23505).
- `Task<TMessage?> LoadMessageAsync(Guid messageId, ct)`.
- `Task<List<Guid>> GetPendingMessageIdsForPartitionAsync(int partitionId, ct)` — the stale-claim reclaim query:
  - `staleThreshold = TimeProvider.GetUtcNow().Subtract(Defaults.ProcessingTimeout).UtcDateTime`
  - Where: `PartitionId == partitionId && RetryCount < MaxRetryCount && (Status == Pending || (Status == Processing && <timestamp> < staleThreshold))` — timestamp is `ReceivedAt` (inbox) / `CreatedAt` (outbox).
  - `OrderBy(<timestamp>)` ascending (oldest first), `Take(BatchSize)`, select `Id` only.
  - **Violation to fix:** staleness was judged on the row's *creation/receipt* time, not when it was claimed, so a legitimately Processing row became reclaimable 5 min after receipt even mid-work; and claiming did not atomically mark rows Processing (story 2.3 requires claimed rows to become Processing as part of the claim).
- `Task MarkAsFailedWithRetryAsync(...)` — bulk `ExecuteUpdateAsync` setting `Status = Pending`, `RetryCount = RetryCount + 1`, `LastError = errorMessage`. Signature drift in the old code: inbox took `(Guid messageId, string errorMessage, ct)`, outbox took `(TMessage message, string errorMessage, ct)` — unify in the rebuild. **Violation to fix (AD-11):** it always reset to Pending; rows only stopped being picked up because the claim query filters `RetryCount < MaxRetryCount`. The new state machine must set terminal `Failed` explicitly at MaxRetryCount.
- Inbox-only `ExecuteInTransactionAsync(TMessage message, Func<CancellationToken, Task> work, ct)` — `CreateExecutionStrategy().ExecuteAsync` wrapping: begin transaction → `work(ct)` → bulk-update the row to `Completed` + `ProcessedAt = TimeProvider.GetUtcNow().UtcDateTime` → commit. (2.3 generalizes this to atomic multi-row updates.)
- Outbox-only `MarkAsCompletedAsync(TMessage, ct)` — bulk update `Status = Completed`, `ProcessedAt = now`.
- Inbox-only `MarkAsProcessingAsync(TMessage, ct)` — bulk update `Status = Processing` (existed but the old inbox loop never called it — messages were processed straight from Pending; the rebuild claims-to-Processing properly).
- `GetRecentMessagesAsync(int count = 50, ct = default)` — newest-first by ReceivedAt/CreatedAt, `Take(count)`; served the monitoring endpoint (`InboxControllerBase`: `GET api/[controller]` returning 50 recent — decide per AD scope whether the controller base survives; it contains no logic).
- All status/limit literals came from `MessageConstants` via `using static` — preserve.

### Processor bases — loop shape

Both `InboxProcessorBase<TMessage, TDbContext>` and `OutboxProcessorBase<TMessage, TDbContext>` were `BackgroundService` subclasses, generic as above, with ctor `(IServiceProvider, ILogger, IDistributedLockService, TimeProvider, IOptions<{In,Out}boxProcessingOptions>, string activitySourceName)`; abstract members `string LockNamePrefix` and `Task ProcessMessageAsync(TMessage, IServiceProvider scopedServiceProvider, ct)`.

Loop shape (identical for both):

1. `ExecuteAsync`: `while (!stoppingToken.IsCancellationRequested)` → try `ProcessPartitions` catch-all logs `LogError` and continues (tick survives any exception) → `Task.Delay(PollingIntervalMs, stoppingToken)`.
2. `ProcessPartitions`: fan out `Enumerable.Range(0, options.PartitionCount)` → one task per partition → `Task.WhenAll`.
3. Per partition: lock name `$"{LockNamePrefix}-partition-{partitionId}"`; work runs inside `lockService.ExecuteWithLockAsync(lockName, options.LockExpirySeconds, workload, ct)` — old interface: `Task<bool> ExecuteWithLockAsync(string lockName, int lockExpirySeconds, Func<CancellationToken, Task> workload, CancellationToken ct = default)`; not-acquired → skip silently (returns false, no throw). The workload receives the lock's own CancellationToken.
4. Inside the lock: create a DI scope, resolve the repository base from it, `GetPendingMessageIdsForPartitionAsync`, then process ids sequentially (per-key ordering within the partition).
5. Per message: a **fresh DI scope per message**, re-resolve repository, `LoadMessageAsync` (skip if null), open the restored-trace activity, call abstract `ProcessMessageAsync(message, scope.ServiceProvider, ct)`; on exception → `MarkAsFailedWithRetryAsync` + `LogWarning` (loop continues to next message).
6. Trace restoration (`CreateActivity`): if `TraceParent` non-blank and `ActivityContext.TryParse(TraceParent, TraceState, out var parentContext)` succeeds, start `"ProcessInboxMessage"`/`"ProcessOutboxMessage"` with `ActivityKind.Consumer`/`ActivityKind.Producer` and that parent context; otherwise start without parent. Tags: `inbox.id`/`outbox.id`, `idempotency.key`, `queue_duration_ms` (now − ReceivedAt/CreatedAt).

**Violations / changes for the rebuild:**

- Old processors created `new ActivitySource(activitySourceName)` in the ctor — the `observability` skill and AD-8 require a registered ActivitySource, and tags must include `PartitionId` (old code omitted it).
- Old code had **no `IOutboxDeliveryStrategy`** — delivery lived in each service's `ProcessMessageAsync` override (the A2 defect: `MessagingOutboxProcessor` bypassed the base entirely). The rebuilt `OutboxProcessorBase` dispatches to the strategy itself; success → Completed, failure → kernel retry path (AD-3, AD-11). The inbox equivalent dispatches to a handler port resolved per message from the fresh scope, rather than an abstract method doing its own resolution.
- Old loop never marked claimed rows Processing and never produced terminal `Failed` (see repository notes) — both change under 2.3/AD-11.
- No 5/6-lifetime cooperative cancellation existed (`LockRenewIntervalSeconds` was bound but dead — ruling A4/AD-7); the rebuild honors the lock handle's cancellation token and stops work at 5/6 expiry.
- Old options (`ProcessingOptionsBase`) members read by the kernel: `PartitionCount`, `LockExpirySeconds`, `PollingIntervalMs`; `LockRenewIntervalSeconds` existed but was never read — it must not exist in the rebuilt options (epic 3).
