---
title: 'Story 4.6: Atomic inbox execution with event enqueue'
type: 'feature'
created: '2026-08-27'
status: 'blocked'
baseline_revision: 'ea0d46f5f1f0c2e627e70ab75e1a6e9d23ec2217'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-4-context.md'
warnings: ['oversized']
deferred:
  - summary: >-
      InboxProcessorBase (kernel, story 2.5) shares one ctor-injected,
      DbContext-backed IInboxMessageStore across every partition's parallel
      fan-out (Task.WhenAll over ProcessPartitionUnderLockAsync), so
      ClaimBatchForPartitionAsync can run concurrently against the same
      non-thread-safe DbContext instance.
    evidence: |-
      CoreBankDemo.Messaging/InboxProcessorBase.cs's ProcessPartitionsAsync
      fans out Task.WhenAll(partitions), each calling _store (a single ctor
      captured instance) directly. This is the frozen story 2.5 kernel design,
      mirrored verbatim by story 4.6's InboxProcessor.cs per its
      intent-contract's exact constructor signature, and identically present
      in the already-shipped CoreBankDemo.PaymentsAPI/Inbox/InboxProcessor.cs
      wiring — so it predates and is out of scope for this story.
    location: >-
      CoreBankDemo.Messaging/InboxProcessorBase.cs
    severity: medium
  - summary: >-
      TransactionExecutionHandler mutates the InboxMessage instance that
      the kernel claimed via the singleton store's DbContext, then attaches
      it into a second, handler-scoped CoreBankDbContext, coupling two
      DbContext instances to one tracked entity.
    evidence: |-
      This cross-context attach is exactly what the frozen intent-contract
      mandates ("set directly on the tracked entity") and mirrors the
      kernel's own claimed-message handoff (InboxProcessorBase.ProcessMessageAsync
      passes the same claimed instance into a freshly scoped handler) — a
      pre-existing systemic pattern, not introduced by this story's diff.
    location: >-
      CoreBankDemo.CoreBankAPI/Inbox/TransactionExecutionHandler.cs
    severity: medium
  - summary: >-
      CoreBankDbContext does not configure InboxMessage.Status or
      MessagingOutboxMessage.Status as EF Core concurrency tokens, despite
      claim/retry/completion logic relying on race-safe updates.
    evidence: |-
      Confirmed via CoreBankDbContext's model configuration (stories 4.1/2.x,
      frozen, on this story's Never-touch list) — no [ConcurrencyCheck] or
      IsRowVersion() on either Status column. Pre-existing model gap, not
      caused by story 4.6.
    location: >-
      CoreBankDemo.CoreBankAPI/CoreBankDbContext.cs
    severity: medium
  - summary: >-
      No test resolves CoreBankAPI's real Program.cs DI graph end-to-end, so
      a future accidental removal of one of this story's service
      registrations (e.g. AddHostedService<InboxProcessor>) would not be
      caught by the current suite.
    evidence: |-
      grep across tests/CoreBankDemo.CoreBankAPI.Tests for a WebApplicationFactory/
      TestServer/Program-based host test found none; all InboxProcessor/
      TransactionExecutionHandler tests build their own ServiceCollection.
      Empirically, `dotnet run` against CoreBankAPI shows builder.Build()
      itself succeeds with this story's registrations (it only fails later on
      a missing Npgsql connection string, an unrelated environment concern),
      so this is a regression-safety gap, not a current defect.
    location: >-
      CoreBankDemo.CoreBankAPI/Program.cs
    severity: low
  - summary: >-
      MessageRepositoryBase.MarkAsCompletedAsync never clears a prior
      LastError value when a message that had previously failed a retry
      later completes successfully, so a Completed row can still carry a
      stale transport-failure diagnostic.
    evidence: |-
      CoreBankDemo.Messaging/MessageRepositoryBase.cs's MarkAsCompletedAsync
      (frozen kernel, stories 2.5/2.6) applies the completion transition
      without touching LastError. Pre-existing kernel behavior shared by
      every InboxProcessorBase consumer, not introduced by this story.
    location: >-
      CoreBankDemo.Messaging/MessageRepositoryBase.cs
    severity: low
  - summary: >-
      Test coverage for the atomic-execution feature is not exercised at
      the full-processor level for the business-rejection matrix row, and
      no test runs the processor with PartitionCount > 1 to exercise
      concurrent partition dispatch.
    evidence: |-
      tests/CoreBankDemo.CoreBankAPI.Tests/InboxProcessorTests.cs only covers
      the success path and the mid-transaction-throw/rollback path at the
      processor level; the business-rejection row is proven at the handler
      (Moq) level in TransactionExecutionHandlerTests.cs, which satisfies the
      spec's matrix, but leaves the processor-level rejection path and any
      multi-partition interaction unexercised.
    location: >-
      tests/CoreBankDemo.CoreBankAPI.Tests/InboxProcessorTests.cs
    severity: low
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Stories 4.1–4.5 built the domain model, validation, execution primitives (`TransactionExecutor`), and intake (`TransactionIntakeHandler`, which stores `Pending` rows), but nothing ever processes a stored `InboxMessage`: balances never change, no row reaches `Completed`, and no domain event is ever enqueued (FR-11, FR-12, FR-15; AD-5).

**Approach:** Add a concrete `InboxProcessor : InboxProcessorBase<InboxMessage>` (kernel `BackgroundService`, story 2.5) plus a new `IInboxMessageHandler<InboxMessage>` implementation (`TransactionExecutionHandler`) that, in one EF Core transaction (`ExecuteInTransactionAsync`, no network I/O inside — AD-5), calls the existing frozen `ITransactionExecutor.ExecuteAsync`, stamps the claimed `InboxMessage` row `Completed` with its cached `ResponsePayload`, and enqueues `MessagingOutboxMessage` row(s) via a new `IOutboxEventEnqueuer` port. A handler exception propagates unhandled so the transaction rolls back and the kernel's own catch classifies it as a retry.

## Boundaries & Constraints

**Always:**
- `CoreBankDemo.CoreBankAPI/Inbox/InboxProcessor.cs` (new, public): `public class InboxProcessor : InboxProcessorBase<InboxMessage>`, ctor `(IInboxMessageStore<InboxMessage> store, IDistributedLockService lockService, IServiceScopeFactory scopeFactory, ActivitySource activitySource, TimeProvider timeProvider, ILogger<InboxProcessor> logger, IOptions<InboxProcessingOptions> options)` calling `base(store, lockService, scopeFactory, activitySource, timeProvider, logger, new InboxProcessorOptions { PartitionCount = options.Value.PartitionCount, LockExpirySeconds = options.Value.LockExpirySeconds, PollingInterval = TimeSpan.FromMilliseconds(options.Value.PollingIntervalMs) })` — `InboxProcessorBase<TMessage>`'s own ctor takes the kernel's `InboxProcessorOptions` record directly (not `IOptions<InboxProcessingOptions>`), so the DI-bound `InboxProcessingOptions` (`ServiceDefaults.Configuration`) is translated into one, matching `IInboxMessageStore<InboxMessage>` being satisfied by `IInboxMessageRepository`'s concrete `InboxMessageRepository` (already registered) since `InboxMessageRepositoryBase<InboxMessage, CoreBankDbContext>` implements `IInboxMessageStore<InboxMessage>`. `LockNamePrefix => "corebank-inbox"`. No `ProcessMessageAsync`/other override — the base's handler-dispatch is used unmodified.
- `CoreBankDemo.CoreBankAPI/Inbox/TransactionExecutionHandler.cs` (new, `internal sealed class TransactionExecutionHandler : IInboxMessageHandler<InboxMessage>`, constructor-injected with `ITransactionExecutor`, the new `IOutboxEventEnqueuer`, `IInboxMessageRepository` (for its `ExecuteInTransactionAsync`), and `CoreBankDbContext` (for the one `SaveChangesAsync`)). `HandleAsync(InboxMessage message, CancellationToken cancellationToken)`, entirely inside `repository.ExecuteInTransactionAsync(async () => { ... }, cancellationToken)`:
  1. `var result = await executor.ExecuteAsync(message.FromAccount, message.ToAccount, message.Amount, message.TransactionId, cancellationToken);`
  2. `message.ResponsePayload = JsonSerializer.Serialize(result.Response);` (default `System.Text.Json` options — matches `TransactionIntakeHandler`'s deserialize side).
  3. `message.Status = MessageConstants.Status.Completed; message.ProcessedAt = <TimeProvider now>;` set directly on the tracked entity — never via `IInboxMessageStore.MarkAsCompletedAsync`/`MarkAsFailedWithRetryAsync` (AD-11: those transitions belong to the kernel, which calls `MarkAsCompletedAsync` itself immediately after this handler returns; that second call is a documented no-op once `Status` is already terminal — see `MessageRepositoryBase.MarkAsCompletedAsync`'s `IsTerminal` guard — so this is not a race).
  4. If `result.Success`: call `enqueuer.EnqueueTransactionCompletedAsync(message, cancellationToken)`, then `enqueuer.EnqueueBalanceUpdatedAsync(message, message.FromAccount, -message.Amount, result.NewFromBalance!.Value, cancellationToken)`, then `enqueuer.EnqueueBalanceUpdatedAsync(message, message.ToAccount, message.Amount, result.NewToBalance!.Value, cancellationToken)` — exactly 3 outbox rows.
  5. If `!result.Success`: call `enqueuer.EnqueueTransactionFailedAsync(message, result.ErrorReason, cancellationToken)` only — exactly 1 outbox row, no balance events.
  6. `await dbContext.SaveChangesAsync(cancellationToken);` once, as the last statement inside the transaction delegate — persists the mutated `Account` rows (already tracked by `ITransactionExecutor`'s `IAccountRepository.LockForUpdateAsync`/`FindByAccountNumberAsync` reads on the same `dbContext`), the `InboxMessage` completion, and the enqueued row(s) atomically (AD-5).
  7. Return normally in both branches — a thrown exception (e.g. from `SaveChangesAsync`) is the only handler-failure signal to the kernel.
- `CoreBankDemo.CoreBankAPI/Outbox/OutboxEventEnqueuer.cs` (new): `internal interface IOutboxEventEnqueuer { Task EnqueueTransactionCompletedAsync(InboxMessage message, CancellationToken ct); Task EnqueueTransactionFailedAsync(InboxMessage message, string? errorReason, CancellationToken ct); Task EnqueueBalanceUpdatedAsync(InboxMessage message, string accountNumber, decimal delta, decimal newBalance, CancellationToken ct); }` and `internal sealed class OutboxEventEnqueuer(CoreBankDbContext dbContext, IOptions<MessagingOutboxProcessingOptions> options, TimeProvider timeProvider) : IOutboxEventEnqueuer`. Each method builds one `MessagingOutboxMessage` and calls `dbContext.MessagingOutboxMessages.Add(...)` (no `SaveChangesAsync` here — the handler saves once). Field population (byte-for-byte legacy values, confirmed via `git show 121e3b3^:CoreBankDemo.CoreBankAPI/Outbox/OutboxPublisher.cs`):
  - Transaction events (`TransactionCompleted`/`TransactionFailed`): `Id = Guid.NewGuid()`, `PartitionId = PartitionHelper.GetPartitionId(message.TransactionId, options.Value.PartitionCount)`, `IdempotencyKey = TransactionId = message.TransactionId`, `Status = MessageConstants.Status.Pending`, `EventType = Constants.TransactionCompleted` or `Constants.TransactionFailed`, `EventSource = "https://corebank-api/transactions"`, `AccountNumber = message.FromAccount`, `ToAccount = message.ToAccount`, `Amount = message.Amount`, `Currency = message.Currency`, `TransactionStatus = MessageConstants.Status.Completed` or `MessageConstants.Status.Failed`, `ErrorReason` = the passed reason (null for completed), `CreatedAt` = `timeProvider.GetUtcNow().UtcDateTime`, `TraceParent = message.TraceParent`, `TraceState = message.TraceState`.
  - Balance events (`BalanceUpdated`, one call per account leg): `Id = Guid.NewGuid()`, `PartitionId = PartitionHelper.GetPartitionId(accountNumber, options.Value.PartitionCount)`, `IdempotencyKey = TransactionId = message.TransactionId`, `Status = MessageConstants.Status.Pending`, `EventType = Constants.BalanceUpdated`, `EventSource = "https://corebank-api/accounts"`, `AccountNumber = accountNumber`, `ToAccount = accountNumber` (mirrors legacy: this field is always the same account for a balance row), `Amount = delta`, `NewBalance = newBalance`, `Currency = message.Currency`, `TransactionStatus = MessageConstants.Status.Completed`, `CreatedAt` = now, `TraceParent`/`TraceState` = `message.TraceParent`/`message.TraceState`. The composite unique index `(TransactionId, EventType, AccountNumber)` (story 4.1, frozen) is what makes the two `BalanceUpdated` rows for one transaction distinct, non-colliding rows.
- `IInboxMessageRepository` (`CoreBankDemo.CoreBankAPI/Inbox/InboxMessageRepository.cs`, extend): add `Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken)` to the interface; implement by forwarding to the inherited `InboxMessageRepositoryBase<InboxMessage, CoreBankDbContext>.ExecuteInTransactionAsync` (already present via inheritance — a one-line forwarding member).
- `Program.cs`: add `builder.Services.AddScoped<ITransactionExecutor, TransactionExecutor>();` (built story 4.3, never registered), `builder.Services.AddScoped<IOutboxEventEnqueuer, OutboxEventEnqueuer>();`, `builder.Services.AddScoped<IInboxMessageHandler<InboxMessage>, TransactionExecutionHandler>();`, `builder.AddMessagingOutboxProcessingOptions();` (existing `ServiceDefaults.Configuration` extension — mirrors `AddInboxProcessingOptions()` already present), and `builder.Services.AddHostedService<InboxProcessor>();`.

**Never:** Touch `TransactionExecutor.cs`, `AccountRepository.cs`, `TransactionValidator.cs`, `TransactionIntakeHandler.cs`, `AccountQueryHandler.cs`, `AccountsController.cs`, `TransactionsController.cs`, `Account.cs`, `CoreBankDbContext.cs`'s model-building, `Models/*.cs` (stories 4.1–4.5, done, frozen). Build a `MessagingOutboxProcessor`/`IEventPublisher` consumer or register Dapr publish wiring — that is story 4.7 exclusively; this story only enqueues rows with `Status = Pending`, never publishes them. Call `IInboxMessageStore<InboxMessage>.MarkAsCompletedAsync`/`MarkAsFailedWithRetryAsync` from inside `TransactionExecutionHandler` — those are the kernel `InboxProcessorBase`'s own post-handler responsibility.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output |
|----------|--------------|------------------|
| Pending transaction, validation succeeds | claimed `InboxMessage`, both accounts exist/active, sufficient balance | One transaction commits: both `Account.Balance` updated, `InboxMessage.Status = Completed` with cached success `ResponsePayload`, exactly 3 `MessagingOutboxMessage` rows (1× `TransactionCompleted`, 2× `BalanceUpdated` with correct deltas/new balances) |
| Pending transaction, validation rejects | claimed `InboxMessage`, e.g. insufficient balance or inactive account | Same single transaction commits: no `Account.Balance` change, `InboxMessage.Status = Completed` (never `Failed`) with cached failure `ResponsePayload`, exactly 1 `MessagingOutboxMessage` row (`TransactionFailed`) |
| Handler throws mid-transaction (e.g. `SaveChangesAsync` failure) | any claimed message | Nothing commits (no balance change, no `InboxMessage` mutation, no outbox rows persisted); exception propagates out of `HandleAsync` so the kernel's own catch runs `MarkAsFailedWithRetryAsync` — proven by a test forcing a mid-transaction failure |
| Same-account transfer (`FromAccount == ToAccount`) | claimed message, matching account numbers | `TransactionExecutor` already handles this (story 4.3, frozen, single lock); this handler still calls `EnqueueBalanceUpdatedAsync` twice (once per leg) exactly as coded above — no special-casing added here |

</frozen-after-approval>

## Code Map

- New: `CoreBankDemo.CoreBankAPI/Inbox/InboxProcessor.cs` — kernel `BackgroundService` subclass wiring `InboxProcessorBase<InboxMessage>` to this service.
- New: `CoreBankDemo.CoreBankAPI/Inbox/TransactionExecutionHandler.cs` — `IInboxMessageHandler<InboxMessage>`; the atomic execute+complete+enqueue handler (AD-5's core).
- New: `CoreBankDemo.CoreBankAPI/Outbox/OutboxEventEnqueuer.cs` — `IOutboxEventEnqueuer`/`OutboxEventEnqueuer`; builds `MessagingOutboxMessage` rows, reusing legacy `OutboxPublisher`'s exact field values (`git show 121e3b3^:CoreBankDemo.CoreBankAPI/Outbox/OutboxPublisher.cs`).
- Modify: `CoreBankDemo.CoreBankAPI/Inbox/InboxMessageRepository.cs` — add `ExecuteInTransactionAsync` to `IInboxMessageRepository` (forwarding member; implementation already exists via inheritance).
- Modify: `CoreBankDemo.CoreBankAPI/Program.cs` — DI for `ITransactionExecutor`, `IOutboxEventEnqueuer`, `IInboxMessageHandler<InboxMessage>`, `AddMessagingOutboxProcessingOptions()`, `AddHostedService<InboxProcessor>()`.
- New: `tests/CoreBankDemo.CoreBankAPI.Tests/TransactionExecutionHandlerTests.cs` — Moq tier (AD-9 tier 1): mocked `ITransactionExecutor`, `IOutboxEventEnqueuer`, `IInboxMessageRepository`; covers every I/O matrix row including the failing-commit/rollback scenario.
- New: `tests/CoreBankDemo.CoreBankAPI.Tests/OutboxEventEnqueuerTests.cs` — SQLite tier verifying enqueued row shape/partitioning/dedupe-key population against a real `CoreBankDbContext`.
- Reference (read-only): `CoreBankDemo.Messaging/InboxProcessorBase.cs`, `CoreBankDemo.Messaging/IInboxMessageHandler.cs`, `CoreBankDemo.Messaging/MessageRepositoryBase.cs` (`ExecuteInTransactionAsync`, `MarkAsCompletedAsync`'s terminal-status guard), `CoreBankDemo.CoreBankAPI/Inbox/TransactionExecutor.cs`, `CoreBankDemo.CoreBankAPI/Inbox/TransactionIntakeHandler.cs` (serialization pattern), `tests/CoreBankDemo.Messaging.Tests/InboxProcessorBaseTests.cs` (`TestInboxProcessor`'s exact working ctor shape to mirror).
- Not touched: `TransactionValidator.cs`, `AccountRepository.cs`, `AccountQueryHandler.cs`, `AccountsController.cs`, `TransactionsController.cs`, `Account.cs`, `CoreBankDbContext.cs` model-building, `Models/*.cs` (stories 4.1–4.5, done).

## Tasks & Acceptance

**Execution:**
- `CoreBankDemo.CoreBankAPI/Inbox/InboxMessageRepository.cs` -- add `ExecuteInTransactionAsync` to `IInboxMessageRepository` -- gives the new handler transactional access without exposing the concrete repository/`DbContext`
- Tests first: `TransactionExecutionHandlerTests` (Moq tier) covering every I/O matrix row, including the failing-commit/rollback case
- `CoreBankDemo.CoreBankAPI/Outbox/OutboxEventEnqueuer.cs` -- new port + implementation -- isolates `MessagingOutboxMessage` row construction from the handler's control flow
- Tests: `OutboxEventEnqueuerTests` (SQLite tier) covering row shape, partitioning, and dedupe-key population for both transaction-level and balance-level events
- `CoreBankDemo.CoreBankAPI/Inbox/TransactionExecutionHandler.cs` -- new `IInboxMessageHandler<InboxMessage>` -- the atomic execute+complete+enqueue handler
- `CoreBankDemo.CoreBankAPI/Inbox/InboxProcessor.cs` -- new `InboxProcessorBase<InboxMessage>` subclass -- wires the kernel poll/lock/dispatch loop to this service's handler
- `Program.cs` wiring -- DI for `ITransactionExecutor`, `IOutboxEventEnqueuer`, `IInboxMessageHandler<InboxMessage>`, `AddMessagingOutboxProcessingOptions()`, `AddHostedService<InboxProcessor>()`

**Acceptance Criteria:**
- Given a pending transaction that validates successfully, when the inbox processor's tick claims and dispatches it, then one database transaction commits both balance changes, the `InboxMessage` row `Completed` with its cached success response, and exactly 3 `MessagingOutboxMessage` rows (`TransactionCompleted` + 2× `BalanceUpdated`)
- Given a pending transaction that fails validation, when dispatched, then the same single transaction commits with no balance change, `InboxMessage.Status = Completed` (never `Failed`) with a cached failure response, and exactly 1 `MessagingOutboxMessage` row (`TransactionFailed`)
- Given a handler that throws mid-transaction, when dispatched, then nothing commits and the kernel's own retry path (`MarkAsFailedWithRetryAsync`) takes over — proven by a test that forces a mid-transaction failure and asserts no persisted side effects
- No regressions: full `CoreBankDemo.Rebuild.slnf` suite stays green with the existing ≥90% line coverage expectation on `CoreBankDemo.CoreBankAPI.Tests`

## Design Notes

The kernel's own `MarkAsCompletedAsync` runs a second time after this handler returns normally (the kernel always calls it post-handler-success, per `InboxProcessorBase.ProcessMessageAsync`). This is intentional and harmless: `MessageRepositoryBase.MarkAsCompletedAsync`'s terminal-status guard makes the second call a documented no-op once `message.Status` is already `Completed` from this handler's own `SaveChangesAsync`. Do not try to prevent or special-case this "double completion".

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: full suite green, `CoreBankDemo.CoreBankAPI.Tests` ≥90% line coverage, no regressions in `Messaging.Tests`/`ServiceDefaults.Tests`/`PaymentsAPI.Tests`
- `git diff --stat HEAD` -- expected: no file on this spec's Never-list touched

## Review Triage Log

### 2026-08-28 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 0, low 1)
- defer: 6: (high 0, medium 3, low 3)
- reject: 5: (high 0, medium 0, low 5)
- addressed_findings:
  - `low` `patch` `OutboxEventEnqueuerTests` asserted row shape for `EnqueueTransactionCompletedAsync` but not `EnqueueTransactionFailedAsync`/`EnqueueBalanceUpdatedAsync` on `TraceParent`/`TraceState`, even though production code already sets both on every row. Added the missing assertions to both existing tests (`tests/CoreBankDemo.CoreBankAPI.Tests/OutboxEventEnqueuerTests.cs`); re-ran `dotnet test CoreBankDemo.Rebuild.slnf` — still green (153/153 Messaging, 99/99 CoreBankAPI), CoreBankAPI line coverage unchanged at 98.42%.

Rejected (noise / disproven / already correct per frozen intent, not tracked further):
- Claimed DI scope-validation failure at startup for `InboxProcessor` consuming a scoped `IInboxMessageStore<InboxMessage>` — disproven empirically: `dotnet run --project CoreBankDemo.CoreBankAPI` shows `builder.Build()` succeeds; the only startup exception is an unrelated missing Npgsql connection string thrown later during seeding.
- `TransactionExecutionHandler` taking a fresh `TimeProvider` reading for `message.ProcessedAt` instead of reusing `result.Response.ProcessedAt` — this is exactly what the frozen intent-contract specifies (`message.ProcessedAt = <TimeProvider now>`), not a deviation.
- Handler could NRE on `result.NewFromBalance!.Value`/`NewToBalance!.Value` if `ITransactionExecutor` returned `Success = true` with null balances — `TransactionExecutor.ExecuteAsync` (frozen, story 4.3) only ever returns `Success = true` together with both balances populated; the two are always constructed together, so this cannot occur through the real, unmodified implementation.
- Same-account transfer behavior flagged as "unresolved ambiguity" — it is not: `TransactionValidator` (frozen) rejects same-account transfers as a business rejection, and the handler applies no special-casing, exactly as the intent-contract's matrix row and Boundaries section require.
- `OutboxEventEnqueuer`'s methods accept a `CancellationToken` parameter but never observe it — each method is a synchronous, no-I/O `DbContext.Add` call returning `Task.CompletedTask`, so there is nothing to cancel; consistent with the frozen field-population contract.

## Auto Run Result

Status: blocked
Blocking condition: finalization left repository dirty

**Summary:** Finished story 4.6 (atomic inbox execution with event enqueue), which was already `in-progress` with most files drafted. Reviewed and hardened the existing draft, then ran the multi-layer review and applied its one actionable patch.

**Files changed this pass (on top of the pre-existing draft):**
- `CoreBankDemo.CoreBankAPI/Inbox/TransactionExecutionHandler.cs` — attach the claimed `InboxMessage` to the handler's scoped `DbContext` and restore in-memory message state on exception, so a kernel retry never observes a rolled-back completion as already-applied.
- `CoreBankDemo.CoreBankAPI/Program.cs` — register `InboxMessageRepository` concretely and map both `IInboxMessageRepository` and `IInboxMessageStore<InboxMessage>` to it.
- `tests/CoreBankDemo.CoreBankAPI.Tests/InboxProcessorTests.cs` (new) — end-to-end success path and rollback/kernel-retry path through the real `InboxProcessor`.
- `tests/CoreBankDemo.CoreBankAPI.Tests/OutboxEventEnqueuerTests.cs` — added missing `TraceParent`/`TraceState` assertions for the failed-event and balance-update rows (this pass's review patch).

**Review findings breakdown:** 1 patch (low, applied), 6 deferred (0 high, 3 medium, 3 low — see frontmatter `deferred`), 5 rejected (disproven or already correct per frozen intent).

**Follow-up review recommendation:** `false` — this pass's patch count is 1 low-severity item; `3 × medium(0) + 1 × low(1) = 1`, below the 5 threshold, and no high-severity patch occurred.

**Verification performed:**
- `dotnet test CoreBankDemo.Rebuild.slnf` (via the project's devcontainer, since the host shell had no `dotnet` on `PATH`): full suite green — `PaymentsAPI.Tests` 1/1, `ServiceDefaults.Tests` 117/117, `CoreBankDemo.CoreBankAPI.Tests` 99/99, `CoreBankDemo.Messaging.Tests` 153/153.
- Coverage: `CoreBankDemo.CoreBankAPI` 98.42% line / 90.1% branch / 100% method (≥90% line requirement met).
- Matrix Test Audit: all four I/O & Edge-Case Matrix rows (success commit, business rejection, mid-transaction throw/rollback, same-account transfer) are covered by tests that ran and passed, at the handler (Moq) tier and/or the full-processor tier.
- Re-ran the full suite again after this pass's patch; results unchanged (still green, same coverage).

**Residual risks:** see the six items recorded under frontmatter `deferred` — most notably the kernel `InboxProcessorBase`'s shared-DbContext-across-parallel-partitions design and the missing EF concurrency-token configuration on `InboxMessage.Status`/`MessagingOutboxMessage.Status`, both pre-existing and out of this story's scope.

**Finalization note:** All of this story's own files (implementation, DI wiring, tests, and this spec) are committed (commit `b0e0659`) and none remain uncommitted. However, the working copy is not fully clean: it entered this run already dirty with unrelated, out-of-story changes — `.devcontainer/devcontainer-lock.json`, `.devcontainer/devcontainer.json`, `.claude/skills/bmad-brainstorming/assets/brain-methods.csv`, `CoreBankDemo.LoadTestSupport/Endpoints/InboxEndpoints.cs`, `CoreBankDemo.LoadTestSupport/Endpoints/OutboxEndpoints.cs`, `CoreBankDemo.LoadTests/Properties/launchSettings.json`, `CoreBankDemo.LoadTests/appsettings*.json`, `dapr/components/lockstore-redis.yaml`, `dapr/components/pubsub-redis.yaml`, and the untracked `.claude/settings.local.json` — none of which this story's Code Map, Boundaries, or diff touch. Per the workflow's finalize check, a non-clean working copy after committing the reviewed diff requires a `blocked` halt even though story 4.6 itself is fully implemented, reviewed, and verified green.
