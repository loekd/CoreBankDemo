# Payment Status GET Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `GET /api/payments/{transactionId}` to PaymentsAPI, answering `200` with CoreBank's committed **business** outcome read from the local outbox row, or `404`.

**Architecture:** A new `PaymentStatusHandler` loads the outbox row through the existing `IOutboxRepository.FindByIdempotencyKeyAsync` (`TransactionId == IdempotencyKey`) and maps it to the frozen `PaymentResponse` using a fixed status matrix. The cached-outcome parsing that `PaymentsController` and `OutboxRepository` each do today moves into one helper, `CachedTransactionOutcome`, which the new handler also uses. The controller gains a thin `GET` action. No call to CoreBank.

**Tech Stack:** .NET 10, ASP.NET Core MVC controllers, EF Core + Npgsql, xUnit v3 + AwesomeAssertions + Moq, Testcontainers PostgreSQL (`postgres:18.3`), `WebApplicationFactory`.

**Spec:** `docs/superpowers/specs/2026-10-07-payment-status-get-design.md`

## Global Constraints

- Branch: `feature/payment-status-get` (already checked out, cut from `origin/main`). Never commit to `main`; do not push.
- Before any build: `dotnet tool restore` (the build runs Kiota through a local tool; see the `build` skill).
- `PaymentResponse` is frozen: `PaymentId`, `TransactionId`, `Status`, `Amount`, `Currency`, `ProcessedAt` — no field added, removed, renamed or reordered.
- The `GET` answers only `200` or `404`. Never `202`, `504`, or anything else.
- The `GET` reads only the PaymentsAPI database and writes nothing. No call to CoreBank, Dapr, or any other service.
- The `GET` reports the **business** status. It never reports `Completed` or `Failed` from the outbox row's technical `Status` column alone (technical `Completed` = delivered to CoreBank's inbox, not executed).
- `POST /api/payments` responses, including the duplicate-key replay on both rails, stay byte-identical. Every existing `PaymentsControllerTests` duplicate test must pass with only the constructor change in `CreateController`.
- Controllers stay thin (conventions skill): bind, call a handler, map to an `IActionResult`.
- No new database index or column (`EnsureCreated()` only).
- Test bar: xUnit + AwesomeAssertions + Moq; ≥90% line coverage (coverlet-enforced); never SQLite or EF InMemory as a PostgreSQL substitute.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

- **An id containing `/`.** `POST` answers `Location: /api/payments/tenant%2Fpayment-1`; ASP.NET Core keeps `%2F` encoded in route values, so a naive lookup misses and the caller following its own `Location` gets `404`. Expected: `200`. Pinned in Task 4 (end-to-end) and Task 3 (controller unescape).
- **An id longer than 100 characters.** The column is `varchar(100)`; the handler must answer not-found without relying on how Npgsql sizes the parameter. Expected: `404`. Pinned in Task 2 (no repository call) and Task 4 (end-to-end).
- **A standard payment delivered to CoreBank but not executed** (technical `Completed`, cached `Pending`, or no payload at all). Expected: `Pending`, never `Completed`. Pinned in Task 2.
- **A cached outcome with a blank or corrupt `Status`, or unparsable JSON.** Expected: treated as no outcome → falls through to the row's technical state → `Pending` (or `Cancelled`/`Failed` per matrix rows 2–3). Pinned in Tasks 1 and 2.
- **The duplicate-`POST` path after the helper move.** A missing or corrupt payload must still fall back to the row's raw status and `CreatedAt`, exactly as today. Pinned by the existing `PaymentsControllerTests` (Task 1 runs them unchanged).

## File Structure

| File | Action | Responsibility |
|---|---|---|
| `CoreBankDemo.PaymentsAPI/Outbox/CachedTransactionOutcome.cs` | Create | Parse `ResponsePayload` into a `TransactionSubmission?`; say whether a status is a committed (terminal) business outcome |
| `CoreBankDemo.PaymentsAPI/Outbox/OutboxRepository.cs` | Modify | `HasCommittedOutcome` delegates to the helper |
| `CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs` | Modify | `ResolveDeliveredResponse` delegates to the helper; new `GET` action; constructor gains `IPaymentStatusHandler` |
| `CoreBankDemo.PaymentsAPI/Handlers/PaymentStatusHandler.cs` | Create | `IPaymentStatusHandler` + implementation of the status matrix |
| `CoreBankDemo.PaymentsAPI/PaymentStorageServiceCollectionExtensions.cs` | Modify | Register `IPaymentStatusHandler` |
| `CoreBankDemo.PaymentsAPI/CoreBankDemo.http` | Modify | `GET` example |
| `tests/CoreBankDemo.PaymentsAPI.Tests/CachedTransactionOutcomeTests.cs` | Create | Helper unit tests |
| `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStatusHandlerTests.cs` | Create | Matrix unit tests |
| `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStorageRegistrationTests.cs` | Modify | Assert the handler is registered |
| `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsControllerTests.cs` | Modify | Constructor change; `GET` action tests |
| `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/PaymentIntakeWiringTests.cs` | Modify | End-to-end `GET` through the real entry point and PostgreSQL |
| `docs/adr/ADR-027-payment-status-local-projection.md` | Create | Decision record |
| `docs/constraints.md`, `ARCHITECTURE.md` | Modify | External contract line; endpoint box |
| `docs/superpowers/specs/2026-10-07-payment-status-get-design.md` | Modify | Status `Draft` → `Implemented`; ADR-027 link |

---

### Task 1: Extract `CachedTransactionOutcome` (pure refactor)

**Files:**
- Create: `CoreBankDemo.PaymentsAPI/Outbox/CachedTransactionOutcome.cs`
- Modify: `CoreBankDemo.PaymentsAPI/Outbox/OutboxRepository.cs` (`HasCommittedOutcome`, near the end of the file)
- Modify: `CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs` (`ResolveDeliveredResponse`, around line 236)
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/CachedTransactionOutcomeTests.cs`

**Interfaces:**
- Consumes: `TransactionSubmission(string TransactionId, string Status, DateTimeOffset ProcessedAt)` (internal record in `Outbox/CoreBankApiContracts.cs`); `MessageConstants.Status.{Completed,Failed,Cancelled}`.
- Produces:
  - `internal static TransactionSubmission? CachedTransactionOutcome.TryRead(string? responsePayload)` — `null` when the payload is null/empty, is not valid JSON, deserializes to `null`, or has a null/whitespace `Status`.
  - `internal static bool CachedTransactionOutcome.IsCommitted(string status)` — `true` for `Completed`, `Failed`, `Cancelled`.

- [ ] **Step 1: Write the failing tests**

Create `tests/CoreBankDemo.PaymentsAPI.Tests/CachedTransactionOutcomeTests.cs`:

```csharp
using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Outbox;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

public class CachedTransactionOutcomeTests
{
    [Fact]
    public void TryRead_returns_the_cached_submission()
    {
        var cached = CachedTransactionOutcome.TryRead(
            """{"TransactionId":"txn-1","Status":"Completed","ProcessedAt":"2026-10-07T12:00:05+00:00"}""");

        cached.Should().NotBeNull();
        cached!.TransactionId.Should().Be("txn-1");
        cached.Status.Should().Be(MessageConstants.Status.Completed);
        cached.ProcessedAt.Should().Be(new DateTimeOffset(2026, 10, 7, 12, 0, 5, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"TransactionId":"txn-1","ProcessedAt":"2026-10-07T12:00:05+00:00"}""")]
    [InlineData("""{"TransactionId":"txn-1","Status":"  ","ProcessedAt":"2026-10-07T12:00:05+00:00"}""")]
    public void TryRead_returns_null_for_a_missing_corrupt_or_status_less_payload(string? payload)
    {
        CachedTransactionOutcome.TryRead(payload).Should().BeNull();
    }

    [Theory]
    [InlineData(MessageConstants.Status.Completed, true)]
    [InlineData(MessageConstants.Status.Failed, true)]
    [InlineData(MessageConstants.Status.Cancelled, true)]
    [InlineData(MessageConstants.Status.Pending, false)]
    [InlineData(MessageConstants.Status.Processing, false)]
    public void IsCommitted_is_true_only_for_terminal_business_outcomes(string status, bool expected)
    {
        CachedTransactionOutcome.IsCommitted(status).Should().Be(expected);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet tool restore && dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~CachedTransactionOutcomeTests"`
Expected: build FAIL — `The name 'CachedTransactionOutcome' does not exist in the current context`.

- [ ] **Step 3: Create the helper**

Create `CoreBankDemo.PaymentsAPI/Outbox/CachedTransactionOutcome.cs`:

```csharp
using System.Text.Json;
using CoreBankDemo.Messaging;

namespace CoreBankDemo.PaymentsAPI.Outbox;

/// <summary>
/// Reads the business outcome cached on an outbox row
/// (<see cref="OutboxMessage.ResponsePayload"/>, a serialized
/// <see cref="TransactionSubmission"/>). Written by
/// <see cref="HttpForwardOutboxDeliveryStrategy"/> on delivery and upgraded by
/// <see cref="IOutboxRepository.RecordCommittedOutcomeAsync"/> from a
/// transaction event. This is the payment's business status; the row's
/// <see cref="OutboxMessage.Status"/> is transport state only (AD-11) and its
/// <c>Completed</c> means "delivered to CoreBank", not "executed".
/// </summary>
internal static class CachedTransactionOutcome
{
    /// <summary>
    /// Returns the cached submission, or <see langword="null"/> when there is
    /// none, it is corrupt, or it carries no status.
    /// </summary>
    public static TransactionSubmission? TryRead(string? responsePayload)
    {
        if (string.IsNullOrEmpty(responsePayload))
        {
            return null;
        }

        try
        {
            var submission = JsonSerializer.Deserialize<TransactionSubmission>(responsePayload);
            return string.IsNullOrWhiteSpace(submission?.Status) ? null : submission;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="status"/> is an outcome CoreBank committed:
    /// <c>Completed</c>, <c>Failed</c> or <c>Cancelled</c>. CoreBank's
    /// <c>Pending</c> acknowledgement is not.
    /// </summary>
    public static bool IsCommitted(string status) =>
        status is MessageConstants.Status.Completed
            or MessageConstants.Status.Failed
            or MessageConstants.Status.Cancelled;
}
```

- [ ] **Step 4: Run the helper tests to verify they pass**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~CachedTransactionOutcomeTests"`
Expected: PASS (12 tests).

- [ ] **Step 5: Point `OutboxRepository` at the helper**

In `CoreBankDemo.PaymentsAPI/Outbox/OutboxRepository.cs`, replace the whole `HasCommittedOutcome` method:

```csharp
    private static bool HasCommittedOutcome(string? responsePayload)
    {
        if (string.IsNullOrEmpty(responsePayload))
        {
            return false;
        }

        try
        {
            var cached = JsonSerializer.Deserialize<TransactionSubmission>(responsePayload);
            // Cancelled is terminal too (spec: instant-rail-timeout-cancel):
            // a cached cancellation is never overwritten by a later event.
            return cached?.Status is MessageConstants.Status.Completed
                or MessageConstants.Status.Failed
                or MessageConstants.Status.Cancelled;
        }
        catch (JsonException)
        {
            // A corrupt payload is never a committed outcome; overwrite it.
            return false;
        }
    }
```

with:

```csharp
    // Cancelled is terminal too (spec: instant-rail-timeout-cancel): a cached
    // cancellation is never overwritten by a later event. A corrupt payload is
    // never a committed outcome; it is overwritten.
    private static bool HasCommittedOutcome(string? responsePayload) =>
        CachedTransactionOutcome.TryRead(responsePayload) is { } cached
        && CachedTransactionOutcome.IsCommitted(cached.Status);
```

`JsonSerializer` is still used by `RecordCommittedOutcomeAsync`; keep `using System.Text.Json;`.

- [ ] **Step 6: Point `PaymentsController.ResolveDeliveredResponse` at the helper**

In `CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs`, replace the body of `ResolveDeliveredResponse` (keep its XML doc comment):

```csharp
    private static (string Status, DateTimeOffset ProcessedAt) ResolveDeliveredResponse(PaymentSnapshot snapshot)
    {
        var cached = CachedTransactionOutcome.TryRead(snapshot.ResponsePayload);
        return cached is null
            ? (snapshot.Status, new DateTimeOffset(DateTime.SpecifyKind(snapshot.CreatedAt, DateTimeKind.Utc)))
            : (cached.Status, cached.ProcessedAt);
    }
```

`ResolveDeliveredResponse` was the controller's only use of `JsonSerializer`/`JsonException`, so remove `using System.Text.Json;` from the controller.

- [ ] **Step 7: Run the whole PaymentsAPI unit-test project**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests`
Expected: PASS, including every existing `PaymentsControllerTests` duplicate-replay test and `TransactionEventHandlerTests`, unmodified.

- [ ] **Step 8: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Outbox/CachedTransactionOutcome.cs CoreBankDemo.PaymentsAPI/Outbox/OutboxRepository.cs CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs tests/CoreBankDemo.PaymentsAPI.Tests/CachedTransactionOutcomeTests.cs
git commit -m "refactor(payments): read the cached CoreBank outcome in one place

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `PaymentStatusHandler` and its registration

**Files:**
- Create: `CoreBankDemo.PaymentsAPI/Handlers/PaymentStatusHandler.cs`
- Modify: `CoreBankDemo.PaymentsAPI/PaymentStorageServiceCollectionExtensions.cs`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStatusHandlerTests.cs` (create)
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStorageRegistrationTests.cs` (modify `Valid_configuration_registers_storage_services`)

**Interfaces:**
- Consumes: `CachedTransactionOutcome.TryRead` / `IsCommitted` (Task 1); `IOutboxRepository.FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken)` returning `Task<OutboxMessage?>` (untracked row).
- Produces:
  - `public interface IPaymentStatusHandler { Task<PaymentResponse?> GetAsync(string transactionId, CancellationToken cancellationToken); }` — `null` means not found.
  - `internal sealed class PaymentStatusHandler(IOutboxRepository repository) : IPaymentStatusHandler`
  - `internal const int PaymentStatusHandler.MaxTransactionIdLength = 100`
  - Registered scoped in `AddPaymentStorage`.

- [ ] **Step 1: Write the failing handler tests**

Create `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStatusHandlerTests.cs`:

```csharp
using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using Moq;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// The spec's status matrix: the reported Status is CoreBank's committed
/// business outcome, never the outbox row's transport Status on its own.
/// </summary>
public class PaymentStatusHandlerTests
{
    private const string Key = "payment-key";
    private static readonly DateTime CreatedAt = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset CreatedAtOffset = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private const string SettledAtJson = "2026-10-07T12:00:05+00:00";
    private static readonly DateTimeOffset SettledAt = new(2026, 10, 7, 12, 0, 5, TimeSpan.Zero);

    private readonly Mock<IOutboxRepository> _repository = new(MockBehavior.Strict);

    private static OutboxMessage Row(string status, string? payload = null, DateTime? processedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = Key,
        TransactionId = Key,
        FromAccount = "NL91ABNA0417164300",
        ToAccount = "NL20INGB0001234567",
        Amount = 12.34m,
        Currency = "EUR",
        PartitionId = 1,
        Status = status,
        CreatedAt = CreatedAt,
        ProcessedAt = processedAt,
        ResponsePayload = payload
    };

    private static string Payload(string status) =>
        $$"""{"TransactionId":"{{Key}}","Status":"{{status}}","ProcessedAt":"{{SettledAtJson}}"}""";

    private async Task<PaymentResponse?> GetAsync(OutboxMessage? row)
    {
        _repository
            .Setup(repository => repository.FindByIdempotencyKeyAsync(Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);
        return await new PaymentStatusHandler(_repository.Object)
            .GetAsync(Key, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(MessageConstants.Status.Completed)]
    [InlineData(MessageConstants.Status.Failed)]
    [InlineData(MessageConstants.Status.Cancelled)]
    public async Task Row_1_a_committed_cached_outcome_is_reported_with_its_time(string outcome)
    {
        var response = await GetAsync(Row(MessageConstants.Status.Completed, Payload(outcome)));

        response.Should().NotBeNull();
        response!.Status.Should().Be(outcome);
        response.ProcessedAt.Should().Be(SettledAt);
    }

    [Theory]
    [InlineData(MessageConstants.Status.Pending)]
    [InlineData(MessageConstants.Status.Processing)]
    public async Task Row_1_a_cached_cancellation_wins_over_a_not_yet_delivered_row(string rowStatus)
    {
        // spec: instant-rail-cancelled-event -- CoreBank's cancellation can be
        // cached while the row is still waiting for the background rail.
        var response = await GetAsync(Row(rowStatus, Payload(MessageConstants.Status.Cancelled)));

        response!.Status.Should().Be(MessageConstants.Status.Cancelled);
        response.ProcessedAt.Should().Be(SettledAt);
    }

    [Fact]
    public async Task Row_2_a_cancelled_row_without_a_cached_outcome_uses_its_processed_time()
    {
        var cancelledAt = new DateTime(2026, 10, 7, 12, 0, 9, DateTimeKind.Utc);

        var response = await GetAsync(Row(MessageConstants.Status.Cancelled, processedAt: cancelledAt));

        response!.Status.Should().Be(MessageConstants.Status.Cancelled);
        response.ProcessedAt.Should().Be(new DateTimeOffset(cancelledAt));
    }

    [Fact]
    public async Task Row_2_a_cancelled_row_without_any_time_falls_back_to_creation()
    {
        var response = await GetAsync(Row(MessageConstants.Status.Cancelled));

        response!.Status.Should().Be(MessageConstants.Status.Cancelled);
        response.ProcessedAt.Should().Be(CreatedAtOffset);
    }

    [Fact]
    public async Task Row_3_a_legacy_failed_row_reports_failed()
    {
        var response = await GetAsync(Row(MessageConstants.Status.Failed));

        response!.Status.Should().Be(MessageConstants.Status.Failed);
        response.ProcessedAt.Should().Be(CreatedAtOffset);
    }

    [Theory]
    [InlineData(MessageConstants.Status.Pending, null)]
    [InlineData(MessageConstants.Status.Processing, null)]
    // Technical Completed = delivered to CoreBank's inbox, not executed.
    [InlineData(MessageConstants.Status.Completed, null)]
    [InlineData(MessageConstants.Status.Completed, """{"TransactionId":"payment-key","Status":"Pending","ProcessedAt":"2026-10-07T12:00:05+00:00"}""")]
    [InlineData(MessageConstants.Status.Completed, "not json")]
    [InlineData(MessageConstants.Status.Completed, """{"TransactionId":"payment-key","Status":"","ProcessedAt":"2026-10-07T12:00:05+00:00"}""")]
    public async Task Row_4_anything_without_a_committed_outcome_is_pending(string rowStatus, string? payload)
    {
        var response = await GetAsync(Row(rowStatus, payload));

        response!.Status.Should().Be(MessageConstants.Status.Pending);
        response.ProcessedAt.Should().Be(CreatedAtOffset);
    }

    [Fact]
    public async Task The_response_carries_the_row_identity_and_amount()
    {
        var response = await GetAsync(Row(MessageConstants.Status.Pending));

        response.Should().Be(new PaymentResponse(
            Key, Key, MessageConstants.Status.Pending, 12.34m, "EUR", CreatedAtOffset));
    }

    [Fact]
    public async Task An_unknown_id_is_not_found()
    {
        (await GetAsync(null)).Should().BeNull();
    }

    [Fact]
    public async Task An_id_longer_than_the_column_is_not_found_without_a_query()
    {
        var tooLong = new string('a', PaymentStatusHandler.MaxTransactionIdLength + 1);

        var response = await new PaymentStatusHandler(_repository.Object)
            .GetAsync(tooLong, TestContext.Current.CancellationToken);

        response.Should().BeNull();
        _repository.VerifyNoOtherCalls();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~PaymentStatusHandlerTests"`
Expected: build FAIL — `The type or namespace name 'PaymentStatusHandler' could not be found`.

- [ ] **Step 3: Implement the handler**

Create `CoreBankDemo.PaymentsAPI/Handlers/PaymentStatusHandler.cs`:

```csharp
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;

namespace CoreBankDemo.PaymentsAPI.Handlers;

/// <summary>
/// Reads a payment's status for <c>GET /api/payments/{transactionId}</c>
/// (spec: payment-status-get, ADR-027).
/// </summary>
public interface IPaymentStatusHandler
{
    /// <summary>
    /// Returns the payment's business status, or <see langword="null"/> when
    /// no payment has this id.
    /// </summary>
    Task<PaymentResponse?> GetAsync(string transactionId, CancellationToken cancellationToken);
}

/// <summary>
/// Projects the local outbox row onto <see cref="PaymentResponse"/>. Reports
/// the outcome CoreBank committed (<see cref="CachedTransactionOutcome"/>),
/// never the row's transport <see cref="OutboxMessage.Status"/> on its own:
/// that column turns <c>Completed</c> once CoreBank has the command in its
/// inbox, before it executes it. Never calls CoreBank (ADR-027).
/// </summary>
internal sealed class PaymentStatusHandler(IOutboxRepository repository) : IPaymentStatusHandler
{
    /// <summary>Length of the <c>IdempotencyKey</c>/<c>TransactionId</c> columns.</summary>
    internal const int MaxTransactionIdLength = 100;

    public async Task<PaymentResponse?> GetAsync(string transactionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transactionId);

        // No stored id can be longer than its column; answer without
        // depending on how the provider sizes an over-long parameter.
        if (transactionId.Length > MaxTransactionIdLength)
        {
            return null;
        }

        // PaymentStorageHandler stores TransactionId = IdempotencyKey, so the
        // indexed dedupe key finds the row (TransactionId has no index).
        var row = await repository.FindByIdempotencyKeyAsync(transactionId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var (status, processedAt) = ResolveBusinessStatus(row);
        return new PaymentResponse(
            row.IdempotencyKey,
            row.TransactionId,
            status,
            row.Amount,
            row.Currency,
            processedAt);
    }

    // The spec's status matrix, first match wins.
    private static (string Status, DateTimeOffset ProcessedAt) ResolveBusinessStatus(OutboxMessage row)
    {
        var createdAt = AsUtc(row.CreatedAt);

        if (CachedTransactionOutcome.TryRead(row.ResponsePayload) is { } cached
            && CachedTransactionOutcome.IsCommitted(cached.Status))
        {
            return (cached.Status, cached.ProcessedAt);
        }

        if (row.Status == MessageConstants.Status.Cancelled)
        {
            return (MessageConstants.Status.Cancelled, row.ProcessedAt is { } cancelledAt ? AsUtc(cancelledAt) : createdAt);
        }

        // Only rows from before ADR-023 can be Failed.
        if (row.Status == MessageConstants.Status.Failed)
        {
            return (MessageConstants.Status.Failed, createdAt);
        }

        return (MessageConstants.Status.Pending, createdAt);
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
```

- [ ] **Step 4: Run the handler tests to verify they pass**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~PaymentStatusHandlerTests"`
Expected: PASS (17 tests).

- [ ] **Step 5: Write the failing registration assertion**

In `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStorageRegistrationTests.cs`, `Valid_configuration_registers_storage_services`, add after the `IPaymentStorageHandler` line:

```csharp
        scope.ServiceProvider.GetRequiredService<IPaymentStatusHandler>().Should().BeOfType<PaymentStatusHandler>();
```

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~PaymentStorageRegistrationTests"`
Expected: FAIL — `No service for type 'CoreBankDemo.PaymentsAPI.Handlers.IPaymentStatusHandler' has been registered`.

- [ ] **Step 6: Register the handler**

In `CoreBankDemo.PaymentsAPI/PaymentStorageServiceCollectionExtensions.cs`, after `services.AddScoped<IPaymentStorageHandler, PaymentStorageHandler>();` add:

```csharp
        services.AddScoped<IPaymentStatusHandler, PaymentStatusHandler>();
```

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~PaymentStorageRegistrationTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Handlers/PaymentStatusHandler.cs CoreBankDemo.PaymentsAPI/PaymentStorageServiceCollectionExtensions.cs tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStatusHandlerTests.cs tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStorageRegistrationTests.cs
git commit -m "feat(payments): resolve a payment's business status from its outbox row

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `GET /api/payments/{transactionId}` on the controller

**Files:**
- Modify: `CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsControllerTests.cs`

**Interfaces:**
- Consumes: `IPaymentStatusHandler.GetAsync(string, CancellationToken)` → `Task<PaymentResponse?>` (Task 2).
- Produces: `public Task<IActionResult> PaymentsController.GetPayment(string transactionId, CancellationToken cancellationToken)` routed at `[HttpGet("{transactionId}")]`; constructor `PaymentsController(IPaymentStorageHandler handler, IInstantPaymentForwardingHandler instantHandler, IPaymentStatusHandler statusHandler, BusinessMetrics businessMetrics)`.

- [ ] **Step 1: Update the test fixture and write the failing tests**

In `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsControllerTests.cs`:

Add a field next to `_instantHandler`:

```csharp
    private readonly Mock<IPaymentStatusHandler> _statusHandler = new(MockBehavior.Strict);
```

Change the construction in `CreateController`:

```csharp
        return new PaymentsController(_handler.Object, _instantHandler.Object, _statusHandler.Object, _businessMetrics)
```

Add these tests at the end of the class:

```csharp
    [Fact]
    public async Task GetPayment_returns_200_with_the_handlers_response()
    {
        var payment = new PaymentResponse(
            IdempotencyKey, TransactionId, "Pending", 50m, "EUR",
            new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var cancellationToken = TestContext.Current.CancellationToken;
        _statusHandler.Setup(handler => handler.GetAsync(TransactionId, cancellationToken)).ReturnsAsync(payment);

        var result = await CreateController().GetPayment(TransactionId, cancellationToken);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(payment);
    }

    [Fact]
    public async Task GetPayment_returns_404_when_the_payment_is_unknown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _statusHandler.Setup(handler => handler.GetAsync("unknown", cancellationToken)).ReturnsAsync((PaymentResponse?)null);

        var result = await CreateController().GetPayment("unknown", cancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Theory]
    // ASP.NET Core keeps %2F encoded in route values; the Location header
    // escapes '/' that way, so the action must turn it back.
    [InlineData("tenant%2Fpayment-1", "tenant/payment-1")]
    [InlineData("tenant%2fpayment-1", "tenant/payment-1")]
    [InlineData("plain-key", "plain-key")]
    public async Task GetPayment_restores_an_escaped_slash_before_the_lookup(string routeValue, string expectedId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _statusHandler.Setup(handler => handler.GetAsync(expectedId, cancellationToken)).ReturnsAsync((PaymentResponse?)null);

        await CreateController().GetPayment(routeValue, cancellationToken);

        _statusHandler.Verify(handler => handler.GetAsync(expectedId, cancellationToken), Times.Once);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~PaymentsControllerTests"`
Expected: build FAIL — `'PaymentsController' does not contain a constructor that takes 4 arguments` / `does not contain a definition for 'GetPayment'`.

- [ ] **Step 3: Implement the action**

In `CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs`:

Change the primary constructor:

```csharp
public class PaymentsController(
    IPaymentStorageHandler handler,
    IInstantPaymentForwardingHandler instantHandler,
    IPaymentStatusHandler statusHandler,
    BusinessMetrics businessMetrics) : ControllerBase
```

Add the action after `ProcessPayment`:

```csharp
    /// <summary>
    /// The payment's business status, read locally from CoreBank's recorded
    /// outcome -- never by calling CoreBank (spec: payment-status-get,
    /// ADR-027). Always <c>200</c> for a known payment, the outcome in the
    /// body's <c>Status</c>; <c>404</c> otherwise. This is the resource the
    /// <c>202</c>'s <c>Location</c> header points at.
    /// </summary>
    [HttpGet("{transactionId}")]
    public async Task<IActionResult> GetPayment(string transactionId, CancellationToken cancellationToken)
    {
        // Location escapes '/' as %2F, which routing leaves encoded.
        var id = transactionId.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
        var payment = await statusHandler.GetAsync(id, cancellationToken);
        return payment is null ? NotFound() : Ok(payment);
    }
```

Also update the class `<summary>` sentence that lists what the controller calls so it mentions `IPaymentStatusHandler` for the `GET`.

- [ ] **Step 4: Run the controller tests to verify they pass**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests`
Expected: PASS — the new tests and every pre-existing test in the project.

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsControllerTests.cs
git commit -m "feat(payments): add GET /api/payments/{transactionId}

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: End-to-end through the real entry point and PostgreSQL

**Files:**
- Modify: `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/PaymentIntakeWiringTests.cs` (reuses its private `PaymentsApiFactory`, which removes `PaymentsOutboxProcessor` and uses a non-acquiring lock, so no background delivery races the test)

**Interfaces:**
- Consumes: the `GET` route (Task 3); `OutboxRepository.RecordCommittedOutcomeAsync(string transactionId, string status, DateTimeOffset processedAt, CancellationToken)`; base-class `CreateStore()` / `store.CreateContext()` and `ConnectionString`; `PaymentsEntryPointEnvironment.Apply(string)`; `TestBusinessMetrics.Instance`.
- Produces: nothing for later tasks.

- [ ] **Step 1: Write the tests**

Add to `PaymentIntakeWiringTests` (above the private nested types):

```csharp
    [Theory]
    [InlineData("payment-status-wiring")]
    [InlineData("tenant/payment-status-wiring")]
    public async Task Get_follows_the_202_location_and_reports_the_business_status(string idempotencyKey)
    {
        await using var environment = PaymentsEntryPointEnvironment.Apply(ConnectionString);
        await using var factory = new PaymentsApiFactory();
        using var client = factory.CreateClient();
        using var post = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = JsonContent.Create(new PaymentRequest(
                "NL91ABNA0417164300", "NL20INGB0001234567", 12.34m, "EUR"))
        };
        post.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey).Should().BeTrue();
        var accepted = await client.SendAsync(post, TestContext.Current.CancellationToken);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var location = accepted.Headers.Location!;

        // Stored, nothing delivered yet: Pending.
        var pending = await client.GetAsync(location, TestContext.Current.CancellationToken);
        pending.StatusCode.Should().Be(HttpStatusCode.OK);
        var pendingBody = await pending.Content.ReadFromJsonAsync<PaymentResponse>(TestContext.Current.CancellationToken);
        pendingBody!.TransactionId.Should().Be(idempotencyKey);
        pendingBody.Status.Should().Be(MessageConstants.Status.Pending);

        // Delivered to CoreBank's inbox (technical Completed) is still Pending.
        await using (var store = CreateStore())
        await using (var context = store.CreateContext())
        {
            var row = context.OutboxMessages.Single(message => message.IdempotencyKey == idempotencyKey);
            row.Status = MessageConstants.Status.Completed;
            row.ResponsePayload = $$"""{"TransactionId":"{{idempotencyKey}}","Status":"Pending","ProcessedAt":"2026-10-07T12:00:01+00:00"}""";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var delivered = await client.GetFromJsonAsync<PaymentResponse>(location, TestContext.Current.CancellationToken);
        delivered!.Status.Should().Be(MessageConstants.Status.Pending);

        // CoreBank's transaction.completed event recorded: Completed.
        var settledAt = new DateTimeOffset(2026, 10, 7, 12, 0, 5, TimeSpan.Zero);
        await using (var store = CreateStore())
        await using (var context = store.CreateContext())
        {
            var repository = new OutboxRepository(context, System.TimeProvider.System, TestBusinessMetrics.Instance);
            (await repository.RecordCommittedOutcomeAsync(
                idempotencyKey, MessageConstants.Status.Completed, settledAt, TestContext.Current.CancellationToken))
                .Should().BeTrue();
        }

        var completed = await client.GetFromJsonAsync<PaymentResponse>(location, TestContext.Current.CancellationToken);
        completed!.Status.Should().Be(MessageConstants.Status.Completed);
        completed.ProcessedAt.Should().Be(settledAt);
    }

    [Theory]
    [InlineData("/api/payments/never-stored")]
    [InlineData("/api/payments/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Get_answers_404_for_an_unknown_or_over_long_id(string path)
    {
        await using var environment = PaymentsEntryPointEnvironment.Apply(ConnectionString);
        await using var factory = new PaymentsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
```

The second `InlineData` is 101 `a` characters. `CreateStore()` comes from `PaymentsPostgresTestBase`; every `using` these tests need is already at the top of `PaymentIntakeWiringTests.cs`.

- [ ] **Step 2: Run the integration tests**

Run: `dotnet test tests/CoreBankDemo.Persistence.IntegrationTests --filter "FullyQualifiedName~PaymentIntakeWiringTests"` (needs Docker for the Testcontainer)
Expected: PASS for all cases, including `tenant/payment-status-wiring`. If the slash case answers `404`, the `%2F` restore in Task 3 is not reaching the route value as assumed: inspect `transactionId` in the action under the test (it may arrive as `tenant%2Fpayment-status-wiring` or already decoded), fix the action, and add the observed form to the Task 3 theory. Do not drop the slash case.

- [ ] **Step 3: Commit**

```bash
git add tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/PaymentIntakeWiringTests.cs
git commit -m "test(payments): GET follows the 202 location end to end

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: ADR-027, contract docs, `.http` sample

**Files:**
- Create: `docs/adr/ADR-027-payment-status-local-projection.md`
- Modify: `docs/constraints.md` (§2, PaymentsAPI list)
- Modify: `ARCHITECTURE.md` (PaymentsAPI endpoints box, around line 85)
- Modify: `CoreBankDemo.PaymentsAPI/CoreBankDemo.http`
- Modify: `docs/superpowers/specs/2026-10-07-payment-status-get-design.md` (header)

**Interfaces:** none.

- [ ] **Step 1: Write ADR-027**

Create `docs/adr/ADR-027-payment-status-local-projection.md`:

```markdown
# ADR-027: Payment status is read from the local projection of CoreBank's outcome

**Date:** 2026-10-07
**Status:** Accepted
**Deciders:** Architecture team

Implementation spec: [`2026-10-07-payment-status-get-design.md`](../superpowers/specs/2026-10-07-payment-status-get-design.md).

## Context

`POST /api/payments` answers `202` with `Location: /api/payments/{transactionId}`, but PaymentsAPI had
no `GET` there. On the instant rail the `202` is the honest answer to an ambiguous timeout (ADR-020),
so a caller must be able to learn the outcome later. Resending the `POST` with the same key was the
only way, and on the standard rail it echoes the outbox row's transport status.

## Decision

1. **`GET /api/payments/{transactionId}` reads the PaymentsAPI outbox row and never calls CoreBank.**
   The row already holds CoreBank's committed outcome: the delivery path caches CoreBank's answer and
   `TransactionEventHandler` records `transaction.completed`/`.failed`/`.cancelled` on it. Reading it
   is a projection of CoreBank's decision, so ADR-023's single source of outcomes holds. An inline call
   would fail in exactly the conditions that produce a `202`.
2. **Always `200` for a known payment, `404` otherwise.** The outcome travels in the frozen
   `PaymentResponse.Status`: `Pending`, `Completed`, `Failed` or `Cancelled`.
3. **The business status, never the transport status.** The outbox row's `Status` is transport state
   (AD-11): its `Completed` means CoreBank has the command in its inbox, not that it executed it. The
   `GET` reports `Completed`/`Failed`/`Cancelled` only from CoreBank's cached outcome, a `Cancelled`
   or legacy `Failed` row as such, and everything else as `Pending`.
4. **The route takes `TransactionId`,** the value the `Location` header carries. PaymentsAPI stores
   `TransactionId = IdempotencyKey`, so the lookup uses the indexed idempotency key.

## Consequences

- The `Location` header of every `202` now resolves.
- The answer lags CoreBank by the event pipeline: a payment CoreBank has decided reads `Pending` until
  its event is processed, and stays `Pending` while the pipeline is down. Late, never wrong.
- `Location` escapes `/` as `%2F`, which ASP.NET Core routing leaves encoded; the action restores it.
  A key that literally contains the text `%2F` therefore cannot be looked up. Accepted: no client in
  this repository generates such keys.
- The standard rail's duplicate `POST` still reports the transport status; `docs/backlog.md` tracks it.
```

- [ ] **Step 2: Update `docs/constraints.md`**

In §2, PaymentsAPI list, after the `POST /api/payments` bullet, add:

```markdown
  - `GET /api/payments/{transactionId}` → `200` with `PaymentResponse` whose `Status` is CoreBank's committed outcome (`Completed`/`Failed`/`Cancelled`) or `Pending`, read from the local outbox row without calling CoreBank; unknown id → `404` (ADR-027).
```

- [ ] **Step 3: Update `ARCHITECTURE.md`**

In the PaymentsAPI box, after `│  │  • POST /api/payments                                      │  │` add a line of the same width:

```
│  │  • GET  /api/payments/{transactionId}                      │  │
```

Check that the right-hand borders line up with the neighbouring lines.

- [ ] **Step 4: Add the `.http` sample**

In `CoreBankDemo.PaymentsAPI/CoreBankDemo.http`, after the first `###` (following the demo `POST`), add:

```http
# Read the demo payment's status (CoreBank's committed outcome, or Pending)
GET {{CoreBankDemo_HostAddress}}/api/payments/regular-apphost-demo-payment-v1
Accept: application/json

###
```

- [ ] **Step 5: Mark the spec implemented**

In `docs/superpowers/specs/2026-10-07-payment-status-get-design.md`, change `> **Status:** Draft` to `> **Status:** Implemented` and, in the `Related` line, replace `ADR-027 (written by this change)` with `[ADR-027](../../adr/ADR-027-payment-status-local-projection.md)`.

- [ ] **Step 6: Commit**

```bash
git add docs/adr/ADR-027-payment-status-local-projection.md docs/constraints.md ARCHITECTURE.md CoreBankDemo.PaymentsAPI/CoreBankDemo.http docs/superpowers/specs/2026-10-07-payment-status-get-design.md
git commit -m "docs(adr): ADR-027 payment status from the local outcome projection

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Full gate

**Files:** none.

- [ ] **Step 1: Unit tier**

Run: `dotnet test CoreBankDemo.UnitTests.slnf`
Expected: PASS, coverage gate met.

- [ ] **Step 2: Integration tier**

Run: `dotnet test CoreBankDemo.IntegrationTests.slnf` (Docker required)
Expected: PASS, coverage gate met.

- [ ] **Step 3: Report**

Report the pass/fail counts and coverage from both runs. Do not push or open a PR unless asked.
