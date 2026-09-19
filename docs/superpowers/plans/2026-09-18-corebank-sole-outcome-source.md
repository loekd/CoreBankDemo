# CoreBank Is The Only Source Of Payment Outcomes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An accepted payment can never end without an outcome: infrastructure failures retry without limit in strict partition order, PaymentsAPI stops pre-validating accounts, and a `400` from CoreBank is a recorded, published verdict.

**Architecture:** The messaging kernel stops writing `Failed` and stops a batch at its first failure, releasing the rows behind it. PaymentsAPI submits directly and maps `400` on submission to a `Failed` business outcome (row `Completed`). CoreBank records a rejection row plus a `transaction.failed` outbox row in one save before answering `400`, and answers `503` for its own internal failures.

**Tech Stack:** .NET 10, EF Core on PostgreSQL 18.3 (`EnsureCreated`, no migrations), xUnit v3 + AwesomeAssertions + Moq, Testcontainers, Kiota-generated CoreBank client, Dev Proxy 3.2.0, Terminal.Gui DemoRunner.

**Spec:** `docs/superpowers/specs/2026-09-18-corebank-sole-outcome-source-design.md` — read it first. Decision record: `docs/adr/ADR-023-corebank-sole-outcome-source.md`.

## Global Constraints

- Branch: `feature/corebank-sole-outcome-source` (already cut from `origin/main`). Never commit to `main`. Push and open the PR only when asked.
- Build order (the `build` skill): `dotnet tool restore` once, then `dotnet test CoreBankDemo.UnitTests.slnf` (Docker-free) and `dotnet test CoreBankDemo.IntegrationTests.slnf` (needs Docker; pinned `postgres:18.3`).
- TDD: write the failing test, watch it fail, then implement. Coverage ≥90 % per logic project (coverlet-enforced).
- Never use SQLite or EF InMemory as a PostgreSQL substitute.
- No new column on any message table, no new event type or topic, no new metric instrument, no change to instant-rail settings (spec "Ask First").
- Never publish a transaction outcome from PaymentsAPI. Never write `Failed` as a kernel row status. `MessageConstants.Status.Failed` stays (wire word for a business rejection). Rows already `Failed` in a database stay terminal and untouched.
- Treat `400` as a verdict for `ProcessTransactionAsync` only. `429`, `408`, `401`, `403`, `404`, `5xx`, timeouts and transport exceptions stay `Retry`.
- CoreBankAPI keeps `POST /api/accounts/validate` (external contract).
- Follow the `conventions`, `messaging-patterns` and `observability` skills. `TimeProvider` for every clock read.
- Commit messages end with: `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`

## File Structure

| File | Responsibility after this plan |
|---|---|
| `CoreBankDemo.Messaging/MessageRepositoryBase.cs` | Failure transition always returns to `Pending`; new `ReleaseClaimsAsync` |
| `CoreBankDemo.Messaging/OutboxMessageRepositoryBase.cs`, `InboxMessageRepositoryBase.cs` | Claim queries without a retry-count filter |
| `CoreBankDemo.Messaging/IOutboxMessageStore.cs`, `IInboxMessageStore.cs` | Declare `ReleaseClaimsAsync` |
| `CoreBankDemo.Messaging/OutboxProcessorBase.cs`, `InboxProcessorBase.cs` | Batch stops at first unsettled row and releases the rest |
| `CoreBankDemo.Messaging/MessageConstants.cs` | No `MaxRetryCount` |
| `CoreBankDemo.ServiceDefaults/BusinessMetrics.cs` | No `ItemOutcome.TerminalFailed` |
| `CoreBankDemo.PaymentsAPI/Outbox/*` | No validation call; `CoreBankClientOutcome.Rejected` for submission `400` |
| `CoreBankDemo.CoreBankAPI/Inbox/TransactionRejectionHandler.cs` (new) | Records a door rejection + its event in one save |
| `CoreBankDemo.CoreBankAPI/Controllers/TransactionsController.cs` | `400` only after recording; `503` for internal failures |
| `CoreBankDemo.DemoRunner/Application/FaultLevels.cs` | Jitter preset `5, 1200, 3000, 0` |

---

### Task 1: The kernel never gives a row up

**Files:**
- Modify: `CoreBankDemo.Messaging/MessageRepositoryBase.cs` (`ApplyFailureTransition`, ~line 390; doc comments ~185, ~316-321, ~345, ~378)
- Modify: `CoreBankDemo.Messaging/OutboxMessageRepositoryBase.cs:41`, `CoreBankDemo.Messaging/InboxMessageRepositoryBase.cs:41`
- Modify: `CoreBankDemo.Messaging/MessageConstants.cs:20,52`, `IMessage.cs:20`, `IOutboxMessageStore.cs:35`, `IInboxMessageStore.cs:37`
- Modify: `CoreBankDemo.Messaging/OutboxProcessorBase.cs:375-388`, `CoreBankDemo.Messaging/InboxProcessorBase.cs:375-388`
- Modify: `CoreBankDemo.ServiceDefaults/BusinessMetrics.cs:139,389`
- Modify: `observability/grafana/dashboards/corebank.json:654`
- Test: `tests/CoreBankDemo.Persistence.IntegrationTests/Messaging/MarkAsFailedWithRetryAsyncTests.cs`, `ClaimBatchForPartitionAsyncTests.cs`, `ClaimBatchForPartitionAsyncOutboxTests.cs`, `MarkAsCompletedAsyncTests.cs`, `PaymentsApi/InboxProcessorTests.cs`, `LoadTestSupport/AssertEndpointsIntegrationTests.cs`
- Test: `tests/CoreBankDemo.Messaging.Tests/MessageConstantsTests.cs`, `OutboxProcessorBaseTests.cs`, `InboxProcessorBaseTests.cs`, `tests/CoreBankDemo.ServiceDefaults.Tests/BusinessMetricsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `MarkAsFailedWithRetryAsync` always leaves `Status == Pending` (unless the row was already terminal). `MessageConstants.Defaults.MaxRetryCount` and `BusinessMetrics.ItemOutcome.TerminalFailed` no longer exist.

- [ ] **Step 1: Rewrite the retry-limit integration tests to assert "retried again"**

In `tests/CoreBankDemo.Persistence.IntegrationTests/Messaging/MarkAsFailedWithRetryAsyncTests.cs` replace the whole `Retry_at_limit_becomes_terminal_failed` test with:

```csharp
    [Fact]
    public async Task Retry_far_past_five_attempts_still_returns_to_pending()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var message = new TestInboxMessage { IdempotencyKey = "never-given-up", RetryCount = 41 };
        context.InboxMessages.Add(message);
        await context.SaveChangesAsync(ct);

        await repository.MarkAsFailedWithRetryAsync(message, "still failing", ct);

        message.Status.Should().Be(MessageConstants.Status.Pending);
        message.RetryCount.Should().Be(42);
        message.LastError.Should().Be("still failing");

        var reloaded = await context.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, ct);
        reloaded.Status.Should().Be(MessageConstants.Status.Pending, "the kernel never writes Failed (ADR-023)");
    }
```

In the same file, in `Repeat_call_on_an_already_failed_message_is_a_no_op` (a legacy `Failed` row must stay untouched), replace every `MessageConstants.Defaults.MaxRetryCount` with the literal `5`.

In `ClaimBatchForPartitionAsyncTests.cs` replace the test `Excludes_poisoned_rows_at_max_retry_count` with:

```csharp
    [Fact]
    public async Task Claims_a_pending_row_however_often_it_has_been_retried()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var retriedOften = new TestInboxMessage
        {
            IdempotencyKey = "retried-often",
            PartitionId = 0,
            RetryCount = 500,
        };
        context.InboxMessages.Add(retriedOften);
        await context.SaveChangesAsync(ct);

        var claimed = await repository.ClaimBatchForPartitionAsync(0, 10, ct);

        claimed.Should().ContainSingle().Which.Id.Should().Be(retriedOften.Id);
    }
```

In `ClaimBatchForPartitionAsyncOutboxTests.cs` find the test that seeds the row with `IdempotencyKey = "poisoned"` (line ~94). Set that row's `RetryCount = 500`, rename the test to `Claims_a_pending_outbox_row_however_often_it_has_been_retried`, and change its assertion so the claimed batch **contains** the `"poisoned"` row's id instead of excluding it.

In `MarkAsCompletedAsyncTests.cs:139` and `LoadTestSupport/AssertEndpointsIntegrationTests.cs:240` replace `MessageConstants.Defaults.MaxRetryCount` with the literal `5` (both seed a legacy `Failed` row; the literal keeps the fixture meaningful).

In `PaymentsApi/InboxProcessorTests.cs` replace the test `StartAsync_at_the_retry_limit_marks_an_unsupported_event_terminally_failed` — keep its body, but:

```csharp
    [Fact]
    public async Task StartAsync_keeps_retrying_an_unsupported_event_past_five_attempts()
    {
        // ... unchanged arrange, except:
        message.RetryCount = 41;
        // ... unchanged act ...
        persisted.Status.Should().Be(MessageConstants.Status.Pending);
        persisted.RetryCount.Should().Be(42);
        persisted.ProcessedAt.Should().BeNull();
    }
```

- [ ] **Step 2: Rewrite the unit tests that expect `terminal_failed`**

`tests/CoreBankDemo.Messaging.Tests/MessageConstantsTests.cs:24` — delete the line `MessageConstants.Defaults.MaxRetryCount.Should().Be(5);`.

`OutboxProcessorBaseTests.cs` — delete the test `Delivery_failure_at_max_retry_records_a_terminal_failed_item_metric_exactly_once`. `InboxProcessorBaseTests.cs` — delete `Handler_failure_at_max_retry_records_a_terminal_failed_item_metric_exactly_once` and `Concurrent_terminal_failure_records_no_second_terminal_failed_metric`. Rename `Delivery_failure_below_max_retry_records_a_retry_scheduled_item_metric` (outbox) and its inbox twin to `..._failure_records_a_retry_scheduled_item_metric`.

`tests/CoreBankDemo.ServiceDefaults.Tests/BusinessMetricsTests.cs` — delete the test at ~line 205-222 that records `ItemOutcome.TerminalFailed`, and delete the `[InlineData(BusinessMetrics.ItemOutcome.TerminalFailed, "terminal_failed")]` row at line 228.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet tool restore && dotnet test CoreBankDemo.IntegrationTests.slnf --filter "FullyQualifiedName~MarkAsFailedWithRetryAsyncTests|FullyQualifiedName~ClaimBatchForPartitionAsync|FullyQualifiedName~PaymentsApi.InboxProcessorTests"`
Expected: FAIL — `Retry_far_past_five_attempts_still_returns_to_pending` sees `Failed`; the two claim tests get an empty batch.

- [ ] **Step 4: Implement**

`MessageRepositoryBase.cs` — replace `ApplyFailureTransition`:

```csharp
    private static void ApplyFailureTransition(TMessage message, string errorMessage)
    {
        // ADR-023: an infrastructure failure is never given up on. RetryCount
        // keeps counting for diagnostics only; the row always goes back to
        // Pending and is first in line again on the next poll tick.
        message.RetryCount += 1;
        message.LastError = errorMessage;
        message.Status = MessageConstants.Status.Pending;
    }
```

`OutboxMessageRepositoryBase.cs` and `InboxMessageRepositoryBase.cs` — delete the line
`m.RetryCount < MessageConstants.Defaults.MaxRetryCount &&` from `GetClaimableMessagesQuery`.

`MessageConstants.cs` — delete `public const int MaxRetryCount = 5;` and its doc comment; change the `Failed` summary at line 20 to:

```csharp
        /// <summary>Wire word for a business rejection inside a response payload. No longer written as a row status (ADR-023); legacy rows that carry it stay terminal.</summary>
```

`IMessage.cs:20` → `/// <summary>Transport delivery attempts so far. Diagnostic only: a row is retried without limit (ADR-023).</summary>`

`IOutboxMessageStore.cs:35-37`, `IInboxMessageStore.cs:37-39` and the `MarkAsFailedWithRetryAsync` doc comment in `MessageRepositoryBase.cs` (~316-321): replace the sentences about `MaxRetryCount`/terminal `Failed` with "Always returns the row to `Pending` and increments `RetryCount`; never writes `Failed` (ADR-023)." Fix the remaining comment mentions at `MessageRepositoryBase.cs` ~185, ~345, ~378 to say "legacy `Failed` rows" instead of "hit `MaxRetryCount`".

`OutboxProcessorBase.cs` and `InboxProcessorBase.cs` — replace the block

```csharp
            _businessMetrics.RecordItemProcessed(
                StoreName,
                BusinessMetrics.StoreKind.Outbox,
                message.Status == MessageConstants.Status.Failed
                    ? BusinessMetrics.ItemOutcome.TerminalFailed
                    : BusinessMetrics.ItemOutcome.RetryScheduled);
```

(and the comment above it) with

```csharp
            // ADR-023: a failed row always goes back to Pending.
            _businessMetrics.RecordItemProcessed(
                StoreName, BusinessMetrics.StoreKind.Outbox, BusinessMetrics.ItemOutcome.RetryScheduled);
```

(use `StoreKind.Inbox` in the inbox processor).

`BusinessMetrics.cs` — delete the `TerminalFailed` enum member (line 139) and its `"terminal_failed"` switch arm (line 389).

`observability/grafana/dashboards/corebank.json:654` — remove `terminal_failed|` from the `outcome=~"…"` regex.

- [ ] **Step 5: Build everything and fix stragglers**

Run: `dotnet build CoreBankDemo.sln --no-restore 2>&1 | grep -E "error|Build succeeded"`
Expected: `Build succeeded`. If an error names `MaxRetryCount` or `TerminalFailed`, it is a reference this task missed — apply the same rule (literal `5` in a fixture, delete in an assertion about the limit).

Run: `grep -rn "MaxRetryCount\|TerminalFailed\|terminal_failed" --include=*.cs --include=*.json CoreBankDemo.* observability tests | grep -v "/bin/\|/obj/"`
Expected: only comment mentions in `CoreBankDemo.CoreBankAPI/Inbox/TransactionIntakeHandler.cs`, `CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs` and `CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs` (Tasks 4 and 6 rewrite those).

- [ ] **Step 6: Run both tiers**

Run: `dotnet test CoreBankDemo.UnitTests.slnf` then `dotnet test CoreBankDemo.IntegrationTests.slnf`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(messaging): infrastructure failures retry without limit (ADR-023)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: `ReleaseClaimsAsync` — give claimed rows back untouched

**Files:**
- Modify: `CoreBankDemo.Messaging/MessageRepositoryBase.cs` (add after `MarkAsCancelledAsync`, ~line 580)
- Modify: `CoreBankDemo.Messaging/IOutboxMessageStore.cs`, `CoreBankDemo.Messaging/IInboxMessageStore.cs`
- Modify: `tests/CoreBankDemo.Messaging.Tests/OutboxProcessorBaseTests.cs` (`ScopedStore`, ~line 112 — the only hand-written store fake)
- Create: `tests/CoreBankDemo.Persistence.IntegrationTests/Messaging/ReleaseClaimsAsyncTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces, on both store interfaces:
  `Task ReleaseClaimsAsync(IReadOnlyList<TMessage> messages, CancellationToken cancellationToken = default);`
  `Processing` → `Pending`, `RetryCount`/`LastError` untouched; rows not `Processing` are skipped; a row another writer changed is skipped, never forced.

- [ ] **Step 1: Write the failing tests**

Create `tests/CoreBankDemo.Persistence.IntegrationTests/Messaging/ReleaseClaimsAsyncTests.cs`:

```csharp
using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Messaging;

/// <summary>
/// <c>ReleaseClaimsAsync</c> (ADR-023): when a batch stops at its first failed
/// row, the rows claimed behind it go back to <c>Pending</c> exactly as they
/// were -- no retry counted, no error recorded -- so the failed row is first
/// in line again on the next tick and nothing overtakes it.
/// </summary>
public class ReleaseClaimsAsyncTests(PostgresContainerFixture fixture) : MessagingPostgresTestBase(fixture)
{
    [Fact]
    public async Task Processing_rows_return_to_pending_with_retry_count_and_error_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var first = new TestInboxMessage { IdempotencyKey = "r-1", Status = MessageConstants.Status.Processing, RetryCount = 3, LastError = "earlier" };
        var second = new TestInboxMessage { IdempotencyKey = "r-2", Status = MessageConstants.Status.Processing };
        context.InboxMessages.AddRange(first, second);
        await context.SaveChangesAsync(ct);

        await repository.ReleaseClaimsAsync([first, second], ct);

        var reloaded = await context.InboxMessages.AsNoTracking().OrderBy(m => m.IdempotencyKey).ToListAsync(ct);
        reloaded.Should().AllSatisfy(m => m.Status.Should().Be(MessageConstants.Status.Pending));
        reloaded[0].RetryCount.Should().Be(3);
        reloaded[0].LastError.Should().Be("earlier");
        reloaded[1].RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task Rows_that_are_not_processing_are_left_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var completed = new TestInboxMessage { IdempotencyKey = "done", Status = MessageConstants.Status.Completed };
        var cancelled = new TestInboxMessage { IdempotencyKey = "gone", Status = MessageConstants.Status.Cancelled };
        context.InboxMessages.AddRange(completed, cancelled);
        await context.SaveChangesAsync(ct);

        await repository.ReleaseClaimsAsync([completed, cancelled], ct);

        var statuses = await context.InboxMessages.AsNoTracking().Select(m => m.Status).ToListAsync(ct);
        statuses.Should().BeEquivalentTo([MessageConstants.Status.Completed, MessageConstants.Status.Cancelled]);
    }

    [Fact]
    public async Task A_row_another_writer_already_moved_is_skipped_and_the_rest_still_release()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var raced = new TestInboxMessage { IdempotencyKey = "raced", Status = MessageConstants.Status.Processing };
        var plain = new TestInboxMessage { IdempotencyKey = "plain", Status = MessageConstants.Status.Processing };
        context.InboxMessages.AddRange(raced, plain);
        await context.SaveChangesAsync(ct);

        // Another writer cancels the first row behind this context's back.
        await using (var other = CreateContext())
        {
            var theirs = await other.InboxMessages.SingleAsync(m => m.Id == raced.Id, ct);
            theirs.Status = MessageConstants.Status.Cancelled;
            await other.SaveChangesAsync(ct);
        }

        var act = () => repository.ReleaseClaimsAsync([raced, plain], ct);

        await act.Should().NotThrowAsync();
        await using var verify = CreateContext();
        (await verify.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == raced.Id, ct))
            .Status.Should().Be(MessageConstants.Status.Cancelled, "a conflicting row is never forced");
        (await verify.InboxMessages.AsNoTracking().SingleAsync(m => m.Id == plain.Id, ct))
            .Status.Should().Be(MessageConstants.Status.Pending);
    }

    [Fact]
    public async Task Rejects_a_null_list()
    {
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var act = () => repository.ReleaseClaimsAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test CoreBankDemo.IntegrationTests.slnf --filter "FullyQualifiedName~ReleaseClaimsAsyncTests"`
Expected: build FAIL — `ReleaseClaimsAsync` does not exist.

- [ ] **Step 3: Implement**

Add to both `IOutboxMessageStore<TMessage>` and `IInboxMessageStore<TMessage>`:

```csharp
    /// <summary>
    /// Returns rows this caller claimed but never attempted to <c>Pending</c>,
    /// exactly as they were: <c>RetryCount</c> and <c>LastError</c> untouched
    /// (ADR-023 -- a batch stops at its first failed row). Rows that are not
    /// <c>Processing</c>, and rows another writer has changed since they were
    /// claimed, are skipped; nothing is ever forced.
    /// </summary>
    Task ReleaseClaimsAsync(IReadOnlyList<TMessage> messages, CancellationToken cancellationToken = default);
```

Add to `MessageRepositoryBase<TMessage, TDbContext>`:

```csharp
    /// <inheritdoc cref="IOutboxMessageStore{TMessage}.ReleaseClaimsAsync"/>
    public virtual async Task ReleaseClaimsAsync(
        IReadOnlyList<TMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        foreach (var message in messages)
        {
            if (message is null || message.Status != MessageConstants.Status.Processing)
            {
                continue;
            }

            if (DbContext.Entry(message).State == EntityState.Detached)
            {
                DbContext.Attach(message);
            }

            message.Status = MessageConstants.Status.Pending;

            try
            {
                // One save per row: Status is the concurrency token, so a row
                // another writer moved must not roll the others back.
                await DbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone else owns this row now. Take their values and leave it.
                await DbContext.Entry(message).ReloadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
```

Add to `ScopedStore` in `tests/CoreBankDemo.Messaging.Tests/OutboxProcessorBaseTests.cs`:

```csharp
        public Task ReleaseClaimsAsync(
            IReadOnlyList<TestOutboxEventMessage> messages,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test CoreBankDemo.IntegrationTests.slnf --filter "FullyQualifiedName~ReleaseClaimsAsyncTests"` — Expected: 4 passed.
Run: `dotnet build CoreBankDemo.sln --no-restore 2>&1 | grep -E "error|Build succeeded"` — Expected: `Build succeeded`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(messaging): ReleaseClaimsAsync returns unattempted claims to Pending

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: A batch stops at its first unsettled row

**Files:**
- Modify: `CoreBankDemo.Messaging/OutboxProcessorBase.cs` (`ProcessPartitionAsync` ~256-281, `ProcessMessageAsync` ~303-430)
- Modify: `CoreBankDemo.Messaging/InboxProcessorBase.cs` (`ProcessPartitionAsync` ~243-277, `ProcessMessageAsync` ~305-429)
- Test: `tests/CoreBankDemo.Messaging.Tests/OutboxProcessorBaseTests.cs`, `InboxProcessorBaseTests.cs`

**Interfaces:**
- Consumes: `ReleaseClaimsAsync` (Task 2).
- Produces: `ProcessMessageAsync` returns `Task<bool>` — `true` only when the row reached a terminal state (completed by this call, or `AlreadyTerminal`). On `false` the processor releases every later row of the batch and ends the partition's tick.

- [ ] **Step 1: Rewrite the four "continues to the next message" tests**

`OutboxProcessorBaseTests.cs` — replace `Delivery_failure_on_one_message_still_lets_the_tick_continue_to_the_next_message` with:

```csharp
    [Fact]
    public async Task Delivery_failure_stops_the_batch_and_releases_the_rows_behind_it()
    {
        var first = NewMessage("first");
        var second = NewMessage("second");
        var third = NewMessage("third");
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { first, second, third });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)Array.Empty<TestOutboxEventMessage>());
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(first, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        strategy.Setup(s => s.DeliverAsync(second, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        await processor.RunTickAsync(CancellationToken.None);

        store.Verify(s => s.MarkAsCompletedAsync(first, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.MarkAsFailedWithRetryAsync(second, "boom", It.IsAny<CancellationToken>()), Times.Once);
        strategy.Verify(s => s.DeliverAsync(third, It.IsAny<CancellationToken>()), Times.Never,
            "a later row must never overtake a failed one (ADR-023)");
        store.Verify(s => s.ReleaseClaimsAsync(
            It.Is<IReadOnlyList<TestOutboxEventMessage>>(rows => rows.Count == 1 && rows[0] == third),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_batch_that_fully_succeeds_releases_nothing()
    {
        var first = NewMessage("first");
        var second = NewMessage("second");
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { first, second });
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        await processor.RunTickAsync(CancellationToken.None);

        store.Verify(s => s.MarkAsCompletedAsync(second, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.ReleaseClaimsAsync(It.IsAny<IReadOnlyList<TestOutboxEventMessage>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failure_while_releasing_the_remainder_never_escapes_the_tick()
    {
        var first = NewMessage("first");
        var second = NewMessage("second");
        var store = new Mock<IOutboxMessageStore<TestOutboxEventMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestOutboxEventMessage>)new[] { first, second });
        store.Setup(s => s.ReleaseClaimsAsync(It.IsAny<IReadOnlyList<TestOutboxEventMessage>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var strategy = new Mock<IOutboxDeliveryStrategy<TestOutboxEventMessage>>();
        strategy.Setup(s => s.DeliverAsync(first, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
        var processor = new TestOutboxProcessor(
            store.Object, new AlwaysAcquiringLockService(), strategy.Object, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new OutboxProcessorOptions { PartitionCount = 1 });

        var act = async () => await processor.RunTickAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("unreleased rows are reclaimed once their claim goes stale");
    }
```

In `Delivery_failure_followed_by_a_MarkAsFailedWithRetryAsync_failure_does_not_escape_the_tick_and_the_next_message_still_dispatches`: rename the suffix to `..._and_the_batch_stops`; replace the two verifications about `second` with

```csharp
        strategy.Verify(s => s.DeliverAsync(second, It.IsAny<CancellationToken>()), Times.Never,
            "the failed row is stuck Processing until its claim goes stale; nothing may overtake it");
        store.Verify(s => s.ReleaseClaimsAsync(
            It.Is<IReadOnlyList<TestOutboxEventMessage>>(rows => rows.Count == 1 && rows[0] == second),
            It.IsAny<CancellationToken>()), Times.Once);
```

and update the leading comment to say the batch stops rather than continues.

`InboxProcessorBaseTests.cs` — make the same three changes with the inbox types. The replacement for `Handler_failure_on_one_message_still_lets_the_tick_continue_to_the_next_message`:

```csharp
    [Fact]
    public async Task Handler_failure_stops_the_batch_and_releases_the_rows_behind_it()
    {
        var first = NewMessage("first");
        var second = NewMessage("second");
        var third = NewMessage("third");
        var store = new Mock<IInboxMessageStore<TestInboxMessage>>();
        store.Setup(s => s.ClaimBatchForPartitionAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestInboxMessage>)new[] { first, second, third });
        store.Setup(s => s.ClaimBatchForPartitionAsync(It.Is<int>(p => p != 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TestInboxMessage>)Array.Empty<TestInboxMessage>());
        var handler = new Mock<IInboxMessageHandler<TestInboxMessage>>();
        handler.Setup(h => h.HandleAsync(first, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        handler.Setup(h => h.HandleAsync(second, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
        var scopeFactory = new FakeServiceScopeFactory(() => store.Object, () => handler.Object);
        var processor = new TestInboxProcessor(
            new AlwaysAcquiringLockService(), scopeFactory, ActivitySource, TimeProvider.System,
            NullLoggerLike(), TestBusinessMetrics, new InboxProcessorOptions { PartitionCount = 1 });

        await processor.RunTickAsync(CancellationToken.None);

        store.Verify(s => s.MarkAsCompletedAsync(first, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.MarkAsFailedWithRetryAsync(second, "boom", It.IsAny<CancellationToken>()), Times.Once);
        handler.Verify(h => h.HandleAsync(third, It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.ReleaseClaimsAsync(
            It.Is<IReadOnlyList<TestInboxMessage>>(rows => rows.Count == 1 && rows[0] == third),
            It.IsAny<CancellationToken>()), Times.Once);
    }
```

Add the inbox twins of `A_batch_that_fully_succeeds_releases_nothing` and `A_failure_while_releasing_the_remainder_never_escapes_the_tick` (same bodies; `IInboxMessageStore<TestInboxMessage>`, a `Mock<IInboxMessageHandler<TestInboxMessage>>` whose `HandleAsync(first, …)` throws in the second one, `FakeServiceScopeFactory`, `TestInboxProcessor`, `InboxProcessorOptions`). Apply the same rename/verification swap to `Handler_failure_followed_by_a_MarkAsFailedWithRetryAsync_failure_…`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/CoreBankDemo.Messaging.Tests --filter "FullyQualifiedName~stops_the_batch|FullyQualifiedName~releases_nothing|FullyQualifiedName~releasing_the_remainder|FullyQualifiedName~batch_stops"`
Expected: FAIL — the third row is delivered and `ReleaseClaimsAsync` is never called.

- [ ] **Step 3: Implement in `OutboxProcessorBase.cs`**

Replace the `foreach` in `ProcessPartitionAsync` with:

```csharp
        // Sequential, oldest-first (AD-4). ADR-023: the batch stops at the
        // first row that did not reach a terminal state -- a later row must
        // never overtake it -- and the rows claimed behind it go back to
        // Pending untouched, so the unsettled row is first in line next tick.
        for (var index = 0; index < claimed.Count; index++)
        {
            var settled = await ProcessMessageAsync(claimed[index], store, deliveryStrategy, cancellationToken)
                .ConfigureAwait(false);
            if (settled)
            {
                continue;
            }

            await ReleaseRemainderAsync(store, claimed, index + 1, partitionId, cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    private async Task ReleaseRemainderAsync(
        IOutboxMessageStore<TMessage> store,
        IReadOnlyList<TMessage> claimed,
        int firstUnattempted,
        int partitionId,
        CancellationToken cancellationToken)
    {
        var remainder = claimed.Skip(firstUnattempted).Where(m => m is not null).ToList();
        if (remainder.Count == 0)
        {
            return;
        }

        try
        {
            await store.ReleaseClaimsAsync(remainder, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Bookkeeping only: the rows stay Processing and are reclaimed
            // once their claim goes stale. Must never escape the tick.
            _logger.LogWarning(
                ex,
                "Failed to release {Count} unattempted outbox claims in partition {PartitionId}; they will be reclaimed once stale",
                remainder.Count, partitionId);
        }
```

(The closing brace of `ReleaseRemainderAsync` is the one that previously closed `ProcessPartitionAsync`; make sure braces balance.)

Change `ProcessMessageAsync`'s signature to `private async Task<bool> ProcessMessageAsync(...)` and its exits:

| Exit today | Returns |
|---|---|
| failure path, `MarkAsFailedWithRetryAsync` → `AlreadyTerminal` (`return;`) | `return true;` |
| failure path, retry persistence failed (`return;` after `RetryPersistenceFailed`) | `return false;` |
| failure path, retry scheduled (`return;` after `RetryScheduled`) | `return false;` |
| completion path, `MarkAsCompletedAsync` → `AlreadyTerminal` (`return;`) | `return true;` |
| completion persistence failed (`return;` after `CompletionPersistenceFailed`) | `return false;` |
| end of method (after `ItemOutcome.Completed`) | `return true;` |

Add to the method's doc comment: "Returns `true` only when the row reached a terminal state; `false` stops the batch (ADR-023)."

- [ ] **Step 4: Implement the same in `InboxProcessorBase.cs`**

Replace the `foreach` (keep the null-element guard):

```csharp
        for (var index = 0; index < claimed.Count; index++)
        {
            var message = claimed[index];
            // Defensive: a null element from a misbehaving store is skipped.
            if (message is null)
            {
                continue;
            }

            var settled = await ProcessMessageAsync(store, message, cancellationToken).ConfigureAwait(false);
            if (settled)
            {
                continue;
            }

            await ReleaseRemainderAsync(store, claimed, index + 1, partitionId, cancellationToken).ConfigureAwait(false);
            return;
        }
```

Add `ReleaseRemainderAsync` exactly as in Step 3 but with `IInboxMessageStore<TMessage>` and the log text "unattempted inbox claims". Change `ProcessMessageAsync` to `Task<bool>` with the same exit table.

- [ ] **Step 5: Run to verify**

Run: `dotnet test CoreBankDemo.UnitTests.slnf` then `dotnet test CoreBankDemo.IntegrationTests.slnf`
Expected: PASS. An integration test that relied on a second row being processed in the same tick as a failing first row must be changed to expect the second row `Pending` after that tick and processed on a later one — that is the behaviour ADR-023 specifies, not a regression.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(messaging): a batch stops at its first unsettled row and releases the rest

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: PaymentsAPI submits without pre-validation

**Files:**
- Modify: `CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs` (`ForwardAsync` ~202-226; class doc ~60-90)
- Modify: `CoreBankDemo.PaymentsAPI/Outbox/ICoreBankApiClient.cs`, `KiotaCoreBankApiClient.cs:45-74`, `CoreBankApiContracts.cs` (`AccountValidation` record)
- Modify: `CoreBankDemo.AppHost/devproxy/config/devproxy-errors.json:6`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/HttpForwardOutboxDeliveryStrategyTests.cs`, `CoreBankApiClientTests.cs`, `CoreBankClientRegistrationTests.cs`, `PaymentRequestSchemeTests.cs`, `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/PaymentsOutboxProcessorTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ICoreBankApiClient` without `ValidateAccountAsync`; `AccountValidation` record deleted. `ICoreBankTransactionForwarder.ForwardAsync` signature unchanged.

- [ ] **Step 1: Write the failing test**

In `HttpForwardOutboxDeliveryStrategyTests.cs` replace `DeliverAsync_completes_when_account_is_valid_and_submission_succeeds` with:

```csharp
    [Fact]
    public async Task DeliverAsync_submits_directly_without_validating_the_destination_account()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new FakeCoreBankApiClient
        {
            SubmitResult = CoreBankResult<TransactionSubmission>.Success(
                new TransactionSubmission("forward-key", "Pending", DateTimeOffset.UtcNow))
        };
        var strategy = new HttpForwardOutboxDeliveryStrategy(client, _store.Object, BusinessMetrics, NullLogger<HttpForwardOutboxDeliveryStrategy>.Instance);
        var message = Message();

        var act = () => strategy.DeliverAsync(message, cancellation.Token);

        await act.Should().NotThrowAsync();
        client.SubmitCalls.Should().ContainSingle();
        client.SubmitCancellationTokens.Should().Equal(cancellation.Token);
        client.SubmitCalls[0].FromAccount.Should().Be("NL91ABNA0417164300");
        client.SubmitCalls[0].ToAccount.Should().Be(ToAccount);
        client.SubmitCalls[0].Amount.Should().Be(message.Amount);
        client.SubmitCalls[0].Currency.Should().Be(message.Currency);
        client.SubmitCalls[0].TransactionId.Should().Be("forward-key");
    }
```

In the fake at the bottom of the file delete `ValidateResult`, `ValidateThrows`, `ValidateCalls`, `ValidateCancellationTokens` and the `ValidateAccountAsync` method. Delete the test `DeliverAsync_throws_and_never_submits_when_destination_account_is_invalid` and every test (or `[Theory]` row) whose failure is injected through `ValidateResult`/`ValidateThrows`; in every remaining test delete the `ValidateResult = …` initializer lines. Keep all submission-failure tests.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build tests/CoreBankDemo.PaymentsAPI.Tests 2>&1 | grep -E "error CS|Build succeeded" | head`
Expected: FAIL — `FakeCoreBankApiClient` does not implement `ICoreBankApiClient.ValidateAccountAsync`.

- [ ] **Step 3: Implement**

`HttpForwardOutboxDeliveryStrategy.ForwardAsync` — delete everything from `var validation = await client.ValidateAccountAsync(` through the closing brace of the `if (!validation.Value!.IsValid)` block, so the method starts with `var submission = await client.ProcessTransactionAsync(`. In the class doc comment replace the description of the validate-then-submit sequence with:

```csharp
/// Submits the transaction directly. There is no destination-account
/// pre-validation (ADR-023): CoreBank checks both accounts when it executes
/// the transaction, and that check is the only authoritative one -- an
/// unknown or inactive account comes back as a committed business rejection
/// (<c>transaction.failed</c>), never as a delivery failure.
```

and delete the sentence about a `Success` validation with `IsValid == false`.

`ICoreBankApiClient.cs` — delete `ValidateAccountAsync`. `KiotaCoreBankApiClient.cs` — delete the `ValidateAccountAsync` method (lines 45-74) and any private helper only it used. `CoreBankApiContracts.cs` — delete the `AccountValidation` record. Leave `GetAccountDetailsAsync`/`AccountDetails`.

`CoreBankApiClientTests.cs` — delete every `ValidateAccountAsync_*` test. `CoreBankClientRegistrationTests.cs` and `PaymentRequestSchemeTests.cs` — each has one reference; if it is a fake's `ValidateAccountAsync` member delete the member, if it is a call replace it with `GetAccountDetailsAsync` using the same arguments. `PaymentsOutboxProcessorTests.cs` (integration) — delete the four validation stub setups/assertions; a test whose whole subject is a failed validation is deleted.

`CoreBankDemo.AppHost/devproxy/config/devproxy-errors.json:6` — change
`"url": "http://127.0.0.1:5032/api/accounts/validate"` to `"url": "http://127.0.0.1:5032/api/transactions/process"`.

- [ ] **Step 4: Run to verify**

Run: `grep -rn "ValidateAccountAsync\|AccountValidation\b" --include=*.cs CoreBankDemo.PaymentsAPI tests | grep -v "/bin/\|/obj/"`
Expected: no output.
Run: `dotnet test CoreBankDemo.UnitTests.slnf` then `dotnet test CoreBankDemo.IntegrationTests.slnf`
Expected: PASS. If `tests/CoreBankDemo.DemoRunner.Tests/Infrastructure/DevProxySessionConfigWriterTests.cs` asserts the old errors-file URL, update the expected URL to `/api/transactions/process`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(payments): submit without destination-account pre-validation (ADR-023)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: `400` on submission is CoreBank's verdict

**Files:**
- Modify: `CoreBankDemo.PaymentsAPI/Outbox/CoreBankApiContracts.cs` (`CoreBankClientOutcome`, `CoreBankResult<T>`)
- Modify: `CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs` (`ProcessTransactionAsync` ~113, `ExecuteAsync` ~266-335)
- Modify: `CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs` (constructor, `ForwardAsync`)
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankApiClientTests.cs`, `HttpForwardOutboxDeliveryStrategyTests.cs`

**Interfaces:**
- Consumes: Task 4's validation-free `ForwardAsync`.
- Produces:
  - `CoreBankClientOutcome.Rejected`
  - `CoreBankResult<T>.Rejected(int statusCode)` — `Value == null`, `RetryReason == null`, `StatusCode == statusCode`
  - `HttpForwardOutboxDeliveryStrategy(ICoreBankApiClient client, IOutboxMessageStore<OutboxMessage> store, BusinessMetrics businessMetrics, TimeProvider timeProvider, ILogger<HttpForwardOutboxDeliveryStrategy> logger)`
  - On `Rejected`, `ForwardAsync` returns `new TransactionSubmission(message.TransactionId, MessageConstants.Status.Failed, timeProvider.GetUtcNow())` and caches it as `ResponsePayload`.

- [ ] **Step 1: Write the failing tests**

`CoreBankApiClientTests.cs` — replace `ProcessTransactionAsync_treats_400_transport_failure_as_retry_without_throwing` with:

```csharp
    [Fact]
    public async Task ProcessTransactionAsync_maps_400_to_rejected_never_a_retry()
    {
        using var handler = new FakeHttpMessageHandler((_, _) =>
            JsonResponse(HttpStatusCode.BadRequest, new { errors = new[] { "Amount must be between 0.01 and 1,000,000" } }));
        var client = CreateClient(handler);
        var request = new TransactionSubmissionRequest(
            AccountNumber, "NL20INGB0001234567", 100m, "EUR", "txn-1");

        var result = await client.ProcessTransactionAsync(request, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Rejected);
        result.StatusCode.Should().Be(400);
        result.Value.Should().BeNull();
        result.RetryReason.Should().BeNull();
    }

    [Fact]
    public async Task ProcessTransactionAsync_maps_a_400_with_an_unreadable_body_to_rejected_too()
    {
        using var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("not json", System.Text.Encoding.UTF8, "application/json") });
        var client = CreateClient(handler);
        var request = new TransactionSubmissionRequest(
            AccountNumber, "NL20INGB0001234567", 100m, "EUR", "txn-1");

        var result = await client.ProcessTransactionAsync(request, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Rejected);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ProcessTransactionAsync_keeps_every_other_failure_status_a_retry(HttpStatusCode status)
    {
        using var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(status, new { errors = new[] { "x" } }));
        var client = CreateClient(handler);
        var request = new TransactionSubmissionRequest(
            AccountNumber, "NL20INGB0001234567", 100m, "EUR", "txn-1");

        var result = await client.ProcessTransactionAsync(request, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.StatusCode.Should().Be((int)status);
    }
```

Leave `CancelTransactionAsync_treats_400_as_retry_without_throwing` exactly as it is — it now also proves the mapping is submission-only.

`HttpForwardOutboxDeliveryStrategyTests.cs` — first run
`sed -i 's/, NullLogger<HttpForwardOutboxDeliveryStrategy>.Instance)/, TimeProvider.System, NullLogger<HttpForwardOutboxDeliveryStrategy>.Instance)/' tests/CoreBankDemo.PaymentsAPI.Tests/HttpForwardOutboxDeliveryStrategyTests.cs`
then add:

```csharp
    [Fact]
    public async Task ForwardAsync_returns_a_failed_submission_for_a_rejected_outcome_and_never_throws()
    {
        var now = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);
        var clock = new Mock<TimeProvider>();
        clock.Setup(c => c.GetUtcNow()).Returns(now);
        var client = new FakeCoreBankApiClient
        {
            SubmitResult = CoreBankResult<TransactionSubmission>.Rejected(400)
        };
        var strategy = new HttpForwardOutboxDeliveryStrategy(
            client, _store.Object, BusinessMetrics, clock.Object, NullLogger<HttpForwardOutboxDeliveryStrategy>.Instance);
        var message = Message();

        var submission = await strategy.ForwardAsync(message, executeInline: false, TestContext.Current.CancellationToken);

        submission.Should().Be(new TransactionSubmission(message.TransactionId, MessageConstants.Status.Failed, now));
        message.ResponsePayload.Should().Be(System.Text.Json.JsonSerializer.Serialize(submission),
            "a duplicate replay must recover the rejection from the cached payload");
    }

    [Fact]
    public async Task DeliverAsync_completes_normally_for_a_rejected_outcome_so_the_kernel_never_retries_it()
    {
        var client = new FakeCoreBankApiClient { SubmitResult = CoreBankResult<TransactionSubmission>.Rejected(400) };
        var strategy = new HttpForwardOutboxDeliveryStrategy(
            client, _store.Object, BusinessMetrics, TimeProvider.System, NullLogger<HttpForwardOutboxDeliveryStrategy>.Instance);

        var act = () => strategy.DeliverAsync(Message(), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        _store.Verify(s => s.MarkAsCancelledAsync(It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
```

The instant rail needs no new test and no production change: `InstantPaymentForwardingHandlerTests.ForwardAsync_reports_a_committed_business_rejection_but_still_completes_the_row` already proves that a forwarder answering `Status == "Failed"` ends `Rejected` (`200 Failed`) with the row completed — which is exactly what the strategy now returns for a `400`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build tests/CoreBankDemo.PaymentsAPI.Tests 2>&1 | grep -E "error CS|Build succeeded" | head`
Expected: FAIL — `CoreBankClientOutcome.Rejected`, `CoreBankResult<T>.Rejected` and the five-argument constructor do not exist.

- [ ] **Step 3: Implement the contract**

`CoreBankApiContracts.cs` — add to `CoreBankClientOutcome`:

```csharp
    /// <summary>
    /// CoreBankAPI answered <c>400</c> to a transaction submission (ADR-023):
    /// its verdict on the request, recorded and published by CoreBank as
    /// <c>transaction.failed</c> before it answered. Retrying can never
    /// succeed. Carries no value -- the client contract never exposes response
    /// bodies; the reason travels with CoreBank's event.
    /// </summary>
    Rejected
```

and to `CoreBankResult<T>`:

```csharp
    public static CoreBankResult<T> Rejected(int statusCode) =>
        new(CoreBankClientOutcome.Rejected, value: default, retryReason: null, statusCode);
```

- [ ] **Step 4: Implement the client mapping**

`KiotaCoreBankApiClient.ExecuteAsync` — add a parameter `bool badRequestIsVerdict = false` after `classifyApiException`, add the helper

```csharp
    private static CoreBankResult<T> RejectionOrRetry<T>(int? statusCode, bool badRequestIsVerdict)
        where T : class =>
        badRequestIsVerdict && statusCode == StatusCodes.Status400BadRequest
            ? CoreBankResult<T>.Rejected(StatusCodes.Status400BadRequest)
            : CoreBankResult<T>.Retry(CoreBankRetryReason.TransportRejection, statusCode);
```

(`using Microsoft.AspNetCore.Http;` — or use the literal `400` if that namespace is not already imported in the file), and use it in the three status-bearing branches:

```csharp
        catch (JsonException)
        {
            var statusCode = statusCapture.StatusCode;
            return statusCode is >= 400 and <= 599
                ? RejectionOrRetry<T>(statusCode, badRequestIsVerdict)
                : CoreBankResult<T>.Retry(CoreBankRetryReason.MalformedResponse);
        }
        catch (ApiException ex)
        {
            return classifyApiException?.Invoke(ex)
                ?? RejectionOrRetry<T>(ex.ResponseStatusCode, badRequestIsVerdict);
        }
```

and in the final `catch (Exception)`:

```csharp
            return statusCode is >= 400 and <= 599
                ? RejectionOrRetry<T>(statusCode, badRequestIsVerdict)
                : CoreBankResult<T>.Retry(CoreBankRetryReason.TransportException);
```

In `ProcessTransactionAsync` pass `badRequestIsVerdict: true` as the last argument of its `ExecuteAsync(...)` call. No other operation passes it.

- [ ] **Step 5: Implement the strategy**

Add `TimeProvider timeProvider` to `HttpForwardOutboxDeliveryStrategy`'s primary constructor between `businessMetrics` and `logger`. In `ForwardAsync`, immediately after the `businessMetrics.RecordDelivery(...)` call and before the `if (submission.Outcome != CoreBankClientOutcome.Success)` throw, insert:

```csharp
        // ADR-023: a 400 is CoreBank's verdict, recorded and published by
        // CoreBank before it answered. It is a business rejection, not a
        // delivery failure (AD-11): the row completes with a Failed payload,
        // exactly like a rejection at execution, and is never retried.
        if (submission.Outcome == CoreBankClientOutcome.Rejected)
        {
            logger.LogInformation(
                "CoreBank rejected transaction {TransactionId} with status {StatusCode}; completing the row with a Failed outcome",
                message.TransactionId, submission.StatusCode);
            var rejected = new TransactionSubmission(
                message.TransactionId, MessageConstants.Status.Failed, timeProvider.GetUtcNow());
            message.ResponsePayload = JsonSerializer.Serialize(rejected);
            return rejected;
        }
```

`TimeProvider` is already registered (`PaymentStorageServiceCollectionExtensions.cs:33`), so DI needs no change. Fix the stale comment in `InstantPaymentForwardingHandler.cs` ~389: replace "(MaxRetryCount is still enforced by the shared kernel path)" with "(the kernel never gives a row up, ADR-023)".

- [ ] **Step 6: Run to verify**

Run: `dotnet test CoreBankDemo.UnitTests.slnf` then `dotnet test CoreBankDemo.IntegrationTests.slnf`
Expected: PASS (fix any remaining four-argument `new HttpForwardOutboxDeliveryStrategy(` in the integration tests by inserting `TimeProvider.System` before the logger).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(payments): a 400 on submission is CoreBank's verdict, never retried (ADR-023)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: CoreBank records a rejection before it answers `400`; internal failures answer `503`

**Files:**
- Create: `CoreBankDemo.CoreBankAPI/Inbox/TransactionRejectionHandler.cs`
- Modify: `CoreBankDemo.CoreBankAPI/Program.cs:56` (register), `Controllers/TransactionsController.cs` (ctor, `ProcessTransaction`, `CancelTransaction`), `Inbox/TransactionIntakeHandler.cs:446-468` (dead branch), `OpenApi/corebank-api.json`
- Create: `tests/CoreBankDemo.Persistence.IntegrationTests/CoreBankApi/TransactionRejectionHandlerTests.cs`
- Test: `tests/CoreBankDemo.CoreBankAPI.Tests/TransactionsControllerTests.cs`, `TransactionIntakeHandlerTests.cs`

**Interfaces:**
- Consumes: `IInboxMessageRepository.FindByIdempotencyKeyAsync` / `StoreIfNewAsync`, `IOutboxEventEnqueuer.EnqueueTransactionFailedAsync(InboxMessage, string?, CancellationToken)`, `CoreBankDbContext`.
- Produces:

```csharp
public enum TransactionRejectionOutcome { Recorded, AlreadyKnown, NotRecordable, StoreFailed }

public interface ITransactionRejectionHandler
{
    Task<TransactionRejectionOutcome> RejectAsync(
        TransactionRequest? request, IReadOnlyList<string> errors, CancellationToken cancellationToken);
}
```

  Controller mapping on invalid model state: `Recorded`/`AlreadyKnown`/`NotRecordable` → `400 { Errors }`; `StoreFailed` → `503 { Errors }`.

- [ ] **Step 1: Write the failing integration tests**

Create `tests/CoreBankDemo.Persistence.IntegrationTests/CoreBankApi/TransactionRejectionHandlerTests.cs`. Copy the class scaffolding (fixture base class, `_clock`, `CreateContext()`, constants `FromAccount`/`ToAccount`/`TransactionId`) from the sibling `TransactionCancellationHandlerTests.cs`, then:

```csharp
    private static readonly string[] Errors = ["Amount must be between 0.01 and 1,000,000"];

    private TransactionRejectionHandler CreateHandler(CoreBankDbContext context, IInboxMessageRepository repository) =>
        new(repository,
            new OutboxEventEnqueuer(
                context,
                Options.Create(new MessagingOutboxProcessingOptions { PartitionCount = 4, LockExpirySeconds = 30, PollingIntervalMs = 5000 }),
                _clock),
            context,
            Options.Create(new InboxProcessingOptions { PartitionCount = 4, LockExpirySeconds = 30 }),
            _clock,
            NullLogger<TransactionRejectionHandler>.Instance);

    [Fact]
    public async Task A_rejection_and_its_failed_event_commit_in_one_save()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, _clock, TestBusinessMetrics.Instance);
        var handler = CreateHandler(context, repository);
        var request = new TransactionRequest(FromAccount, ToAccount, 0m, "EUR", TransactionId);

        var outcome = await handler.RejectAsync(request, Errors, ct);

        outcome.Should().Be(TransactionRejectionOutcome.Recorded);
        await using var verification = CreateContext();
        var inbox = await verification.InboxMessages.SingleAsync(ct);
        inbox.TransactionId.Should().Be(TransactionId);
        inbox.Status.Should().Be(MessageConstants.Status.Completed);
        inbox.ProcessedAt.Should().NotBeNull();
        inbox.LastError.Should().Be(Errors[0]);
        var cached = JsonSerializer.Deserialize<TransactionResponse>(inbox.ResponsePayload!)!;
        cached.Status.Should().Be(MessageConstants.Status.Failed);
        cached.ProcessedAt.UtcDateTime.Should().Be(inbox.ProcessedAt!.Value);
        var evt = await verification.MessagingOutboxMessages.SingleAsync(ct);
        evt.EventType.Should().Be(Constants.TransactionFailed);
        evt.TransactionId.Should().Be(TransactionId);
        evt.TransactionStatus.Should().Be(MessageConstants.Status.Failed);
        evt.ErrorReason.Should().Be(Errors[0]);
        evt.Status.Should().Be(MessageConstants.Status.Pending);
    }

    [Fact]
    public async Task An_id_corebank_already_knows_is_never_overwritten_and_gets_no_event()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var seed = CreateContext())
        {
            seed.InboxMessages.Add(new InboxMessage
            {
                Id = Guid.NewGuid(), IdempotencyKey = TransactionId, TransactionId = TransactionId,
                FromAccount = FromAccount, ToAccount = ToAccount, Amount = 50m, Currency = "EUR",
                PartitionId = 0, Status = MessageConstants.Status.Pending, ReceivedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync(ct);
        }

        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, _clock, TestBusinessMetrics.Instance);
        var outcome = await CreateHandler(context, repository)
            .RejectAsync(new TransactionRequest(FromAccount, ToAccount, 0m, "EUR", TransactionId), Errors, ct);

        outcome.Should().Be(TransactionRejectionOutcome.AlreadyKnown);
        await using var verification = CreateContext();
        (await verification.InboxMessages.SingleAsync(ct)).Status.Should().Be(MessageConstants.Status.Pending);
        (await verification.MessagingOutboxMessages.CountAsync(ct)).Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_request_without_a_usable_transaction_id_is_not_recordable(string? transactionId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, _clock, TestBusinessMetrics.Instance);

        var outcome = await CreateHandler(context, repository)
            .RejectAsync(new TransactionRequest(FromAccount, ToAccount, 10m, "EUR", transactionId!), Errors, ct);

        outcome.Should().Be(TransactionRejectionOutcome.NotRecordable);
        (await context.InboxMessages.CountAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task A_too_long_transaction_id_and_a_null_request_are_not_recordable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, _clock, TestBusinessMetrics.Instance);
        var handler = CreateHandler(context, repository);

        (await handler.RejectAsync(new TransactionRequest(FromAccount, ToAccount, 10m, "EUR", new string('x', 101)), Errors, ct))
            .Should().Be(TransactionRejectionOutcome.NotRecordable);
        (await handler.RejectAsync(null, Errors, ct)).Should().Be(TransactionRejectionOutcome.NotRecordable);
    }

    [Fact]
    public async Task Values_that_do_not_fit_the_table_are_clamped_and_the_reason_keeps_the_truth()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new InboxMessageRepository(context, _clock, TestBusinessMetrics.Instance);
        var request = new TransactionRequest(new string('A', 80), null!, 99_999_999_999_999_999m, "EURO", TransactionId);

        var outcome = await CreateHandler(context, repository).RejectAsync(request, ["FromAccount too long", "ToAccount required"], ct);

        outcome.Should().Be(TransactionRejectionOutcome.Recorded);
        await using var verification = CreateContext();
        var inbox = await verification.InboxMessages.SingleAsync(ct);
        inbox.FromAccount.Should().HaveLength(50);
        inbox.ToAccount.Should().BeEmpty();
        inbox.Amount.Should().Be(0m);
        inbox.Currency.Should().Be("EUR");
        inbox.LastError.Should().Be("FromAccount too long; ToAccount required");
    }

    [Fact]
    public async Task A_failed_save_reports_store_failed_and_leaves_nothing_behind()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new Mock<IInboxMessageRepository>(MockBehavior.Strict);
        repository.Setup(r => r.FindByIdempotencyKeyAsync(TransactionId, It.IsAny<CancellationToken>())).ReturnsAsync((InboxMessage?)null);
        repository.Setup(r => r.StoreIfNewAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var outcome = await CreateHandler(context, repository.Object)
            .RejectAsync(new TransactionRequest(FromAccount, ToAccount, 0m, "EUR", TransactionId), Errors, ct);

        outcome.Should().Be(TransactionRejectionOutcome.StoreFailed);
        context.ChangeTracker.Entries<MessagingOutboxMessage>().Should().BeEmpty("the event row must not ride along on a later save");
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build tests/CoreBankDemo.Persistence.IntegrationTests 2>&1 | grep -E "error CS|Build succeeded" | head -3`
Expected: FAIL — `TransactionRejectionHandler` does not exist.

- [ ] **Step 3: Implement the handler**

Create `CoreBankDemo.CoreBankAPI/Inbox/TransactionRejectionHandler.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.CoreBankAPI.Inbox;

/// <summary>Outcome of <see cref="ITransactionRejectionHandler.RejectAsync"/> (ADR-023).</summary>
public enum TransactionRejectionOutcome
{
    /// <summary>The rejection row and its <c>transaction.failed</c> event were committed in one save.</summary>
    Recorded,

    /// <summary>CoreBank already holds a row for this id; nothing was written and that row stays authoritative.</summary>
    AlreadyKnown,

    /// <summary>The request carries no usable <c>TransactionId</c>, so there is nothing to record the rejection under.</summary>
    NotRecordable,

    /// <summary>The save failed. The caller must not answer <c>400</c>: nothing stands behind it.</summary>
    StoreFailed
}

public interface ITransactionRejectionHandler
{
    Task<TransactionRejectionOutcome> RejectAsync(
        TransactionRequest? request, IReadOnlyList<string> errors, CancellationToken cancellationToken);
}

/// <summary>
/// Records a request CoreBank refused at the door (ADR-023), modelled on
/// <see cref="TransactionCancellationHandler"/>'s tombstone: a terminal inbox
/// row carrying a <c>Failed</c> response, and its <c>transaction.failed</c>
/// event, committed by <c>StoreIfNewAsync</c>'s single <c>SaveChanges</c>.
/// A <c>400</c> therefore always has a published outcome behind it -- the
/// caller was told "no" by the only service that announces outcomes.
/// </summary>
internal sealed class TransactionRejectionHandler(
    IInboxMessageRepository repository,
    IOutboxEventEnqueuer enqueuer,
    CoreBankDbContext dbContext,
    IOptions<InboxProcessingOptions> inboxOptions,
    TimeProvider timeProvider,
    ILogger<TransactionRejectionHandler> logger) : ITransactionRejectionHandler
{
    private const int MaxTransactionIdLength = 100;
    private const int MaxAccountLength = 50;
    private const decimal MaxStorableAmount = 9_999_999_999_999_999.99m; // numeric(18,2)
    private const string FallbackCurrency = "EUR";

    public async Task<TransactionRejectionOutcome> RejectAsync(
        TransactionRequest? request, IReadOnlyList<string> errors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var transactionId = request?.TransactionId;
        if (string.IsNullOrWhiteSpace(transactionId) || transactionId.Length > MaxTransactionIdLength)
        {
            return TransactionRejectionOutcome.NotRecordable;
        }

        Activity.Current?.SetTag("transaction.id", transactionId);
        var reason = string.Join("; ", errors);

        try
        {
            var existing = await repository.FindByIdempotencyKeyAsync(transactionId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return TransactionRejectionOutcome.AlreadyKnown;
            }

            var now = timeProvider.GetUtcNow();
            var rejection = new InboxMessage
            {
                Id = Guid.NewGuid(),
                IdempotencyKey = transactionId,
                TransactionId = transactionId,
                FromAccount = Clamp(request!.FromAccount),
                ToAccount = Clamp(request.ToAccount),
                Amount = request.Amount is >= 0m and <= MaxStorableAmount ? request.Amount : 0m,
                Currency = request.Currency is { Length: 3 } currency ? currency : FallbackCurrency,
                PartitionId = PartitionHelper.GetPartitionId(transactionId, inboxOptions.Value.PartitionCount),
                Status = MessageConstants.Status.Completed,
                ReceivedAt = now.UtcDateTime,
                ProcessedAt = now.UtcDateTime,
                LastError = reason,
                ResponsePayload = JsonSerializer.Serialize(
                    new TransactionResponse(transactionId, MessageConstants.Status.Failed, now)),
                TraceParent = Activity.Current?.Id,
                TraceState = Activity.Current?.TraceStateString
            };

            // Enqueued before the store so StoreIfNewAsync's single SaveChanges
            // commits the rejection and its event together (AD-5).
            await enqueuer.EnqueueTransactionFailedAsync(rejection, reason, cancellationToken).ConfigureAwait(false);

            var stored = await repository.StoreIfNewAsync(rejection, cancellationToken).ConfigureAwait(false);
            if (!stored)
            {
                // Lost the unique-key race: the winner's row is authoritative.
                DetachPendingEvents(transactionId);
                return TransactionRejectionOutcome.AlreadyKnown;
            }

            logger.LogInformation(
                "Recorded the rejection of transaction {TransactionId} with its transaction.failed event: {Reason}",
                transactionId, reason);
            Activity.Current?.SetTag("outcome", "rejected_at_intake");
            return TransactionRejectionOutcome.Recorded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DetachPendingEvents(transactionId);
            logger.LogWarning(ex, "Could not record the rejection of transaction {TransactionId}", transactionId);
            return TransactionRejectionOutcome.StoreFailed;
        }
    }

    private static string Clamp(string? value) =>
        value is null ? string.Empty : value.Length <= MaxAccountLength ? value : value[..MaxAccountLength];

    /// <summary>An event row for a rejection that never committed must not ride along on a later save.</summary>
    private void DetachPendingEvents(string transactionId)
    {
        var pending = dbContext.ChangeTracker.Entries<MessagingOutboxMessage>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.TransactionId == transactionId)
            .ToList();
        foreach (var entry in pending)
        {
            entry.State = EntityState.Detached;
        }
    }
}
```

Register it in `Program.cs` after line 56:

```csharp
builder.Services.AddScoped<ITransactionRejectionHandler, TransactionRejectionHandler>();
```

- [ ] **Step 4: Run the handler tests**

Run: `dotnet test CoreBankDemo.IntegrationTests.slnf --filter "FullyQualifiedName~TransactionRejectionHandlerTests"`
Expected: PASS (8 cases).

- [ ] **Step 5: Write the failing controller tests**

`TransactionsControllerTests.cs` — add a field `private readonly Mock<ITransactionRejectionHandler> _rejectionHandler = new();`, pass `_rejectionHandler.Object` as the third constructor argument in `CreateController()` (before `_businessMetrics`), and add:

```csharp
    [Theory]
    [InlineData(TransactionRejectionOutcome.Recorded)]
    [InlineData(TransactionRejectionOutcome.AlreadyKnown)]
    [InlineData(TransactionRejectionOutcome.NotRecordable)]
    public async Task ProcessTransaction_with_invalid_model_state_answers_400_once_the_rejection_is_settled(
        TransactionRejectionOutcome outcome)
    {
        var request = new TransactionRequest("NL91ABNA0417164300", "NL20INGB0001234567", 0m, "EUR", "txn-bad");
        _rejectionHandler
            .Setup(h => h.RejectAsync(request, It.Is<IReadOnlyList<string>>(e => e.Contains("Amount out of range")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);
        var controller = CreateController();
        controller.ModelState.AddModelError("Amount", "Amount out of range");

        var result = await controller.ProcessTransaction(request, TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestObjectResult>();
        _handler.Verify(h => h.ProcessAsync(It.IsAny<TransactionRequest>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ProcessTransaction_answers_503_when_the_rejection_could_not_be_recorded()
    {
        var request = new TransactionRequest("NL91ABNA0417164300", "NL20INGB0001234567", 0m, "EUR", "txn-bad");
        _rejectionHandler
            .Setup(h => h.RejectAsync(request, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TransactionRejectionOutcome.StoreFailed);
        var controller = CreateController();
        controller.ModelState.AddModelError("Amount", "Amount out of range");

        var result = await controller.ProcessTransaction(request, TestContext.Current.CancellationToken);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }
```

Find the existing tests that assert `BadRequest` for `TransactionIntakeOutcome.TransportFailed` (process) and for `TransactionCancellationOutcome.StoreFailed` (cancel); change each expectation to

```csharp
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
```

and rename them `..._answers_503_...`.

- [ ] **Step 6: Run to verify they fail**

Run: `dotnet build tests/CoreBankDemo.CoreBankAPI.Tests 2>&1 | grep -E "error CS|Build succeeded" | head -3`
Expected: FAIL — the controller has no third handler parameter.

- [ ] **Step 7: Implement the controller changes**

Constructor:

```csharp
public class TransactionsController(
    ITransactionIntakeHandler handler,
    ITransactionCancellationHandler cancellationHandler,
    ITransactionRejectionHandler rejectionHandler,
    BusinessMetrics businessMetrics) : ControllerBase
```

`ProcessTransaction` — replace the `if (!ModelState.IsValid)` block with:

```csharp
        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToArray();

            // ADR-023: a 400 is a verdict, so it is recorded and published
            // (transaction.failed, one save) before it is given. If that
            // cannot be done the honest answer is 503 -- the caller retries.
            var rejection = await rejectionHandler.RejectAsync(request, errors, cancellationToken);
            return rejection == TransactionRejectionOutcome.StoreFailed
                ? ServiceUnavailable(["The rejection could not be recorded; retry"])
                : BadRequest(new { Errors = errors });
        }
```

Replace `TransactionIntakeOutcome.TransportFailed => BadRequest(new { Errors = result.Errors }),` with
`TransactionIntakeOutcome.TransportFailed => ServiceUnavailable(result.Errors),` and in `CancelTransaction` replace `TransactionCancellationOutcome.StoreFailed => BadRequest(new { Errors = result.Errors }),` with `TransactionCancellationOutcome.StoreFailed => ServiceUnavailable(result.Errors),`. Add:

```csharp
    /// <summary>An internal failure is not a bad request (ADR-023): the caller is asked to retry.</summary>
    private ObjectResult ServiceUnavailable(IEnumerable<string>? errors) =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new { Errors = errors ?? [] });
```

Update the `CancelTransaction` doc comment: "`409` … (in flight)" — drop "or terminally failed"; add "`503` when the tombstone could not be stored."

- [ ] **Step 8: Remove the dead terminal-failure branch in the intake handler**

`TransactionIntakeHandler.cs` ~446-468 — delete the comment block starting "MarkAsFailedWithRetryAsync mutates claimed.Status in place" and the whole `if (claimed.Status == MessageConstants.Status.Failed) { … }` block, leaving:

```csharp
            await inboxStore.MarkAsFailedWithRetryAsync(
                claimed,
                ex.Message,
                cancellationToken).ConfigureAwait(false);

            // ADR-023: the row is back at Pending; the inbox processor retries it.
            return new InlineAttempt(null, NotFirstYet: false);
```

In `TransactionIntakeHandlerTests.cs` delete the test that arranges `MarkAsFailedWithRetryAsync` to flip the row to `Failed` during inline execution and expects `TransportFailed`. Keep `BuildIntakeResultForExisting`'s `Failed` branch and its test (legacy rows).

- [ ] **Step 9: Document `503` in the OpenAPI file**

In `CoreBankDemo.CoreBankAPI/OpenApi/corebank-api.json`, under `paths./api/transactions/process.post.responses` and `paths./api/transactions/cancel.post.responses`, add after the `"400"` entry:

```json
          "503": {
            "description": "An internal failure prevented CoreBank from recording its answer; retry",
            "content": {
              "application/json": {
                "schema": { "$ref": "#/components/schemas/ErrorResponse" }
              }
            }
          },
```

and change the `"400"` description on `process` to "The request was rejected. The rejection is recorded and published as transaction.failed before this answer is given (ADR-023)".

- [ ] **Step 10: Run to verify**

Run: `dotnet test CoreBankDemo.UnitTests.slnf` then `dotnet test CoreBankDemo.IntegrationTests.slnf`
Expected: PASS. (PaymentsAPI regenerates its Kiota client from the JSON at build; the `503` maps to the same `ApiException` path and stays a `Retry`.)

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat(corebank): record and publish a rejection before answering 400; 503 for internal failures (ADR-023)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Re-tune the jitter preset for one proxied call per attempt

**Files:**
- Modify: `CoreBankDemo.DemoRunner/Application/FaultLevels.cs:98-102`
- Test: `tests/CoreBankDemo.DemoRunner.Tests/Application/FaultLevelsTests.cs`

**Interfaces:** none.

> `main` currently carries `new FaultLevels(5, 800, 3000, 0)` (set by the repository owner in PR #29). This task replaces it, as the spec states.

- [ ] **Step 1: Change the test first**

In `PresetsForRegular_OfferAnInstantRailJitterBandStraddlingTheAttemptTimeout` change the expectation to:

```csharp
        jitter.Levels.Should().Be(new FaultLevels(5, 1200, 3000, 0));
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/CoreBankDemo.DemoRunner.Tests --filter "FullyQualifiedName~FaultLevelsTests"`
Expected: FAIL — actual `(5, 800, 3000, 0)`.

- [ ] **Step 3: Implement**

Replace the comment and the preset line:

```csharp
            // An instant attempt makes one proxied call (ADR-023 removed the pre-validation),
            // so the band itself has to straddle the rail's 2.5 s attempt timeout: about one
            // attempt in four overruns, a few payments in a burst of fifty overrun both, and
            // roughly half of those end cancelled.
            new FaultPreset("Instant-rail jitter", new FaultLevels(5, 1200, 3000, 0)),
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/CoreBankDemo.DemoRunner.Tests`
Expected: PASS (all).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "fix(demorunner): re-tune the Instant-rail jitter preset to 1200-3000 ms

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Documents

**Files:**
- Modify: `docs/constraints.md`, `README.md`, `ARCHITECTURE.md`, `docs/backlog.md:195`, `docs/adr/ADR-023-corebank-sole-outcome-source.md`, `docs/adr/ADR-006-retry-exponential-backoff.md`, `docs/superpowers/specs/2026-08-29-story-5-4-forwarding-processor-design.md`, `docs/superpowers/specs/2026-08-21-story-2-3-claiming-retry-and-poison-state-machine-design.md`, `.claude/skills/messaging-patterns/SKILL.md`

**Interfaces:** none.

- [ ] **Step 1: `docs/constraints.md`**

Under §1 invariant 5 append: "A batch stops at the first row that does not reach a terminal state and releases the rows claimed behind it, so ordering holds with faults on (ADR-023)." Under §2 CoreBankAPI's `POST /api/transactions/process` bullet append: "`400` is answered only after the rejection is recorded and its `transaction.failed` event enqueued in the same save; an internal failure answers `503` (ADR-023)." Under PaymentsAPI add: "Forwards with a single `POST /api/transactions/process`; there is no destination-account pre-validation (ADR-023)."

- [ ] **Step 2: ADR cross-references**

`ADR-023`: set `**Status:** Accepted`; add to "Supersedes in part": "ADR-006's second retry tier 'up to `MaxRetryCount` = 5' — the tier stays, the limit is removed." and "Story 2.3's poison state (terminal `Failed` at the retry limit)."
`ADR-006`: add under the header `**Superseded in part by:** ADR-023 removes the outbox/inbox tier's MaxRetryCount limit; rows are retried without limit.` and change "(up to `MaxRetryCount` = 5)" to "(without limit, ADR-023)".
Story 5.4 spec and story 2.3 spec: add below each title block `> **Superseded in part by:** [ADR-023](../../adr/ADR-023-corebank-sole-outcome-source.md) — ` followed by "the destination-account validation call is removed." (5.4) / "the poison state is gone: a row is never given up on." (2.3).

- [ ] **Step 3: `README.md`, `ARCHITECTURE.md`, skill**

Run: `grep -n -i "validat.*account\|accounts/validate\|MaxRetryCount\|terminal.*Failed\|poison" README.md ARCHITECTURE.md .claude/skills/messaging-patterns/SKILL.md`
For each hit:
- a description of the forward sequence ("Validates account with CoreBankAPI", "a. Validate toAccount via CoreBankAPI") → delete the step and renumber; the sequence is a single submission.
- the CoreBankAPI endpoint lists (`POST /api/accounts/validate`) → keep (the endpoint still exists).
- a statement that a message becomes `Failed` after five retries → replace with "is retried on every poll tick without limit; a batch stops at its first failed row so nothing overtakes it (ADR-023)".
- status lists `Pending|Processing|Completed|Failed|Cancelled` → keep, and append "(`Failed` is no longer written as a row status, ADR-023)".

- [ ] **Step 4: `docs/backlog.md`**

Delete the item at line ~195 that begins "A destination-account validation rejection (`HttpForwardOutboxDeliveryStrategy.ForwardAsync` throws for `IsValid=false`)…" — ADR-023 resolves it (`200`/`Failed`).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "docs: ADR-023 accepted; constraints, architecture and backlog follow

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: Acceptance — gate, load test, live bursts

**Files:** none (verification only). Use the `build`, `load-test`, `aspire-launch` and `devproxy-install` skills.

- [ ] **Step 1: Full gate**

Run: `dotnet tool restore && dotnet build CoreBankDemo.sln && dotnet test CoreBankDemo.UnitTests.slnf && dotnet test CoreBankDemo.IntegrationTests.slnf`
Expected: build succeeds, both tiers PASS, no coverage threshold failure.

- [ ] **Step 2: Load test**

Follow the `load-test` skill end to end (reset, run, drain, assert).
Expected: every invariant passes, `Failed` counts are zero, no `PerKeyOrdering` violation.

- [ ] **Step 3: Throttle burst (the original bug)**

Start the Regular AppHost with fault arming on (`aspire-launch`; `devproxy-install` if `devproxy --version` is not 3.2.0). In the DemoRunner Faults workspace stage throttling `10/60s`, error rate `5%`, latency `200–200 ms`, Apply. Send a burst of 11 instant payments.
Expected: no row ends `Failed`; within about two minutes `still moving` reaches 0. Verify in the database:

```bash
docker exec -e PGPASSWORD=postgres-dev-load-test $(docker ps --format '{{.Names}}' | grep '^postgres-') \
  psql -U postgres -d paymentsdb -c "select \"Status\", count(*) from \"OutboxMessages\" where \"CreatedAt\" > now() - interval '10 minutes' group by 1"
```

Expected: only `Completed` and `Cancelled`.

- [ ] **Step 4: Jitter burst**

Panic-off, select the "Instant-rail jitter" preset, Apply, send a burst of 50 instant payments.
Expected: `still moving` drains to 0; roughly two payments end cancelled. Record the actual figure in the PR description. If it is far off (0, or more than 8), report it rather than re-tuning silently — the band is an estimate and the repository owner decides the final value.

- [ ] **Step 5: Report**

State plainly what ran and what the numbers were. Do not push or open a PR unless asked.
