# PaymentsAPI Account Projection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** PaymentsAPI keeps a `ProjectedAccounts` table built from accepted payments and CoreBank's `transaction-events`, writes it atomically with the outbox insert (transactional outbox) and with inbox completion (transactional inbox), and answers `422` to a debit it can see is unfunded.

**Architecture:** Bottom-up. A metric vocabulary value (ServiceDefaults); the entity, its EF model and the load-test reset (schema); a small raw-SQL row helper plus the `IAccountProjectionStore` port the inbox handler uses; `OutboxRepository.AcceptAsync`, the one-transaction accept; the storage handler and controller mapping; the inbox handler's transaction; DI wiring and the end-to-end gate; then ADR-028 and the doc amendments. Every projection write happens inside a transaction the kernel's own helpers open (`ExecuteInTransactionAsync`), and inbox completion reuses the kernel's `MarkAsCompletedAsync` so the kernel's later call sees `AlreadyTerminal`, as CoreBank's `TransactionExecutionHandler` already relies on.

**Tech Stack:** .NET 10, EF Core + Npgsql (`EnsureCreated` only), xUnit v3 + AwesomeAssertions + Moq, Testcontainers PostgreSQL 18.3 for tier 2.

**Spec:** `docs/superpowers/specs/2026-10-08-payments-account-projection-design.md`

## Global Constraints

- Branch: `feature/payments-account-projection` (already checked out, cut from `origin/main` with `--no-track`). Never commit to `main`; do not push unless asked.
- Before any build: `dotnet tool restore` (see the `build` skill). Build with `dotnet build CoreBankDemo.Rebuild.slnf`.
- Tier 1 (`dotnet test CoreBankDemo.UnitTests.slnf`) must run without Docker. Tier 2 (`dotnet test CoreBankDemo.IntegrationTests.slnf`) needs Docker; it is never skipped silently. Run tests with a dead OTLP endpoint: `export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1`.
- A filtered `dotnet test --filter` run exits 1 on the coverlet 90% gate even when every test passes; judge filtered runs by the `Passed!`/`Failed!` summary line, and the gate by the unfiltered project run.
- No SQLite, no EF InMemory. No EF migrations. New raw SQL lives in a repository/store class listed in `PersistenceTierFilters` (`tests/Directory.Build.props`), never in a handler.
- `TimeProvider` for every timestamp. `MessageConstants` / CloudEvent `Constants` for every status and event type. No new `ActivitySource`; tag `Activity.Current`.
- Refuse only when `SettledBalance` is not `NULL` and `SettledBalance − Reserved < Amount`. A `422` body is `{ "Errors": ["Insufficient funds"] }` — never the balance.
- Insert-first dedupe stays: the outbox insert runs before the balance is consulted; a duplicate key replays as today.
- The outbox row's `Status` is never written by the inbox path.
- Table name `ProjectedAccounts`; entity `ProjectedAccount`; key `AccountNumber` (`varchar(50)`); `SettledBalance numeric(18,2) NULL`; `Reserved numeric(18,2) NOT NULL DEFAULT 0`; `Currency varchar(3) NULL`; `UpdatedAt timestamp NOT NULL`.
- Commit messages end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.

## Review Focus

- **Two concurrent first-ever debits from one unseen account.** Expected: both are accepted (no settled balance to refuse on), one `ProjectedAccounts` row exists, `Reserved` equals the sum. Pinned in Task 4 (`AcceptAsync_two_concurrent_first_debits_create_one_row_and_sum_reservations`).
- **`balance.updated` arriving for the debtor before the handler has ever seen a payment row** (recreated database). Expected: `SettledBalance` set, `Reserved` stays 0, warning logged, row `Completed`, partition not blocked. Pinned in Task 7 (`Release_below_zero_clamps_and_warns`).
- **An event whose projection write succeeds but whose completion save fails.** Expected: neither the projection change nor `Completed` persists; the row returns to the kernel as unhandled. Pinned in Task 7 persistence test (`HandleAsync_rolls_back_projection_when_completion_fails`).
- **A `422` followed by the same key resent after funds arrive.** Expected: accepted as a fresh payment (key not consumed). Pinned in Task 4 (`AcceptAsync_refused_key_is_not_consumed`).
- **A load-test reset between runs.** Expected: `ProjectedAccounts` empty afterwards, otherwise reservations accumulate into spurious `422`s. Pinned in Task 2 (resetter test asserts `ProjectedAccounts` count 0).

## File Structure

| File | Action | Responsibility |
|---|---|---|
| `CoreBankDemo.ServiceDefaults/BusinessMetrics.cs` | Modify | `PaymentOutcome.InsufficientFunds` → tag `insufficient_funds` |
| `tests/CoreBankDemo.ServiceDefaults.Tests/BusinessMetricsTests.cs` | Modify | vocabulary row |
| `CoreBankDemo.PaymentsAPI/Accounts/ProjectedAccount.cs` | Create | entity |
| `CoreBankDemo.PaymentsAPI/PaymentsDbContext.cs` | Modify | `ProjectedAccounts` DbSet + model |
| `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/PaymentsDbContextTests.cs` | Modify | schema assertions |
| `CoreBankDemo.LoadTestSupport/DatabaseResetCoordinator.cs` | Modify | truncate `ProjectedAccounts` |
| `tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/LoadTestDatabaseResetterTests.cs` | Modify | assert truncation |
| `CoreBankDemo.PaymentsAPI/Accounts/ProjectedAccountRows.cs` | Create | upsert-if-missing + `FOR UPDATE` SQL (static, shared by outbox repo and store) |
| `CoreBankDemo.PaymentsAPI/Accounts/IAccountProjectionStore.cs` | Create | port: `SettleAsync`, `ReleaseAsync` |
| `CoreBankDemo.PaymentsAPI/Accounts/AccountProjectionStore.cs` | Create | implementation on the scoped `PaymentsDbContext` |
| `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/AccountProjectionStoreTests.cs` | Create | store on PostgreSQL |
| `tests/Directory.Build.props` | Modify | add `AccountProjectionStore*` and `ProjectedAccountRows*` to `PersistenceTierFilters` |
| `CoreBankDemo.PaymentsAPI/Outbox/OutboxRepository.cs` | Modify | `PaymentAcceptance`, `IOutboxRepository.AcceptAsync`, implementation |
| `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/OutboxRepositoryAcceptTests.cs` | Create | accept-path on PostgreSQL |
| `CoreBankDemo.PaymentsAPI/Handlers/PaymentStorageHandler.cs` | Modify | call `AcceptAsync`; `InsufficientFunds` outcome, metric, tags, log |
| `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStorageHandlerTests.cs` | Modify | mocks move to `AcceptAsync`; new cases |
| `CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs` | Modify | `422` arm |
| `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsControllerTests.cs` | Modify | `422` cases |
| `CoreBankDemo.PaymentsAPI/Inbox/InboxMessageRepository.cs` | Modify | port exposes `ExecuteInTransactionAsync`, `MarkAsCompletedAsync` |
| `CoreBankDemo.PaymentsAPI/Handlers/TransactionEventHandler.cs` | Modify | transaction, projection effects, completion |
| `tests/CoreBankDemo.PaymentsAPI.Tests/TransactionEventHandlerTests.cs` | Modify | `CreateHandler` helper; projection cases |
| `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/TransactionEventHandlerTests.cs` | Create | atomicity on PostgreSQL |
| `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/InboxProcessorTests.cs` | Modify | handler construction |
| `CoreBankDemo.PaymentsAPI/PaymentStorageServiceCollectionExtensions.cs` | Modify | register `IAccountProjectionStore` |
| `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStorageRegistrationTests.cs` | Modify | resolves |
| `demo-requests.http` | Modify | drain-and-refuse example |
| `docs/adr/ADR-028-local-account-projection-door-check.md` | Create | decision |
| `docs/adr/ADR-023-corebank-sole-outcome-source.md`, `docs/constraints.md`, `.claude/skills/messaging-patterns/SKILL.md`, `ARCHITECTURE.md`, the spec | Modify | amendments |

---

### Task 1: `PaymentOutcome.InsufficientFunds`

**Files:**
- Modify: `CoreBankDemo.ServiceDefaults/BusinessMetrics.cs:42-47` (enum) and `:320-325` (`ToTag`)
- Test: `tests/CoreBankDemo.ServiceDefaults.Tests/BusinessMetricsTests.cs:70-74`

**Interfaces:**
- Produces: `BusinessMetrics.PaymentOutcome.InsufficientFunds`, tag value `"insufficient_funds"` on instrument `corebankdemo.payment.intake`.

- [ ] **Step 1: Add the failing theory row**

In `BusinessMetricsTests.cs`, after the `ValidationFailed` `InlineData`:

```csharp
    [InlineData(BusinessMetrics.PaymentOutcome.InsufficientFunds, "insufficient_funds")]
```

- [ ] **Step 2: Run it to see it fail to compile**

Run: `dotnet test tests/CoreBankDemo.ServiceDefaults.Tests --filter "FullyQualifiedName~RecordPaymentIntake_emits_exactly_one_measurement"`
Expected: build error `'PaymentOutcome' does not contain a definition for 'InsufficientFunds'`.

- [ ] **Step 3: Add the value and its tag**

```csharp
    public enum PaymentOutcome
    {
        Stored,
        Duplicate,
        ValidationFailed,
        /// <summary>Refused at the door by the local account projection (ADR-028); nothing stored.</summary>
        InsufficientFunds
    }
```

```csharp
    private static string ToTag(PaymentOutcome outcome) => outcome switch
    {
        PaymentOutcome.Stored => "stored",
        PaymentOutcome.Duplicate => "duplicate",
        PaymentOutcome.ValidationFailed => "validation_failed",
        PaymentOutcome.InsufficientFunds => "insufficient_funds",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };
```

- [ ] **Step 4: Run the test**

Run the same command. Expected: `Passed!` with 4 rows of that theory.

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.ServiceDefaults/BusinessMetrics.cs tests/CoreBankDemo.ServiceDefaults.Tests/BusinessMetricsTests.cs
git commit -m "feat(metrics): insufficient_funds payment intake outcome

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: `ProjectedAccount` entity, EF model, load-test reset

**Files:**
- Create: `CoreBankDemo.PaymentsAPI/Accounts/ProjectedAccount.cs`
- Modify: `CoreBankDemo.PaymentsAPI/PaymentsDbContext.cs`
- Modify: `CoreBankDemo.LoadTestSupport/DatabaseResetCoordinator.cs:33-36`
- Test: `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/PaymentsDbContextTests.cs`
- Test: `tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/LoadTestDatabaseResetterTests.cs:95-115`

**Interfaces:**
- Produces: `CoreBankDemo.PaymentsAPI.Accounts.ProjectedAccount { string AccountNumber; decimal? SettledBalance; decimal Reserved; string? Currency; DateTime UpdatedAt }`, `PaymentsDbContext.ProjectedAccounts`.

- [ ] **Step 1: Write the failing schema test**

Add to `PaymentsDbContextTests`:

```csharp
    [Fact]
    public async Task Model_includes_the_projected_accounts_table()
    {
        await using var store = CreateStore();
        await using var context = store.CreateContext();

        var account = context.Model.FindEntityType(typeof(CoreBankDemo.PaymentsAPI.Accounts.ProjectedAccount))!;
        account.GetTableName().Should().Be("ProjectedAccounts");
        account.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal(nameof(CoreBankDemo.PaymentsAPI.Accounts.ProjectedAccount.AccountNumber));
        AssertMaxLength(account, nameof(CoreBankDemo.PaymentsAPI.Accounts.ProjectedAccount.AccountNumber), 50);
        AssertMaxLength(account, nameof(CoreBankDemo.PaymentsAPI.Accounts.ProjectedAccount.Currency), 3);
        foreach (var name in new[] { "SettledBalance", "Reserved" })
        {
            account.FindProperty(name)!.GetPrecision().Should().Be(18);
            account.FindProperty(name)!.GetScale().Should().Be(2);
        }
        account.FindProperty("SettledBalance")!.IsNullable.Should().BeTrue();
        account.FindProperty("Currency")!.IsNullable.Should().BeTrue();
        AssertRequired(account, "Reserved", "UpdatedAt");

        // Round trip: a NULL settled balance survives the database.
        context.ProjectedAccounts.Add(new CoreBankDemo.PaymentsAPI.Accounts.ProjectedAccount
        {
            AccountNumber = "NL91ABNA0417164300",
            UpdatedAt = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc)
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using var verification = store.CreateContext();
        var row = await verification.ProjectedAccounts.SingleAsync(TestContext.Current.CancellationToken);
        row.SettledBalance.Should().BeNull();
        row.Reserved.Should().Be(0m);
        row.Currency.Should().BeNull();
    }
```

(`AssertMaxLength` and `AssertRequired` already exist in this class.)

- [ ] **Step 2: Write the failing reset assertion**

In `LoadTestDatabaseResetterTests.Reset_truncates_message_stores_and_resets_load_accounts` (the test around line 60–115 that seeds both databases), seed a projection row next to the Payments outbox row:

```csharp
        payments.ProjectedAccounts.Add(new CoreBankDemo.PaymentsAPI.Accounts.ProjectedAccount
        {
            AccountNumber = "NL01LOAD0000000001",
            SettledBalance = 1m,
            Reserved = 1m,
            UpdatedAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc)
        });
```

and assert after the reset:

```csharp
        (await payments.ProjectedAccounts.CountAsync(cancellationToken)).Should().Be(0);
```

- [ ] **Step 3: Run both tests to see them fail**

Run: `dotnet test tests/CoreBankDemo.Persistence.IntegrationTests --filter "FullyQualifiedName~PaymentsDbContextTests|FullyQualifiedName~LoadTestDatabaseResetterTests"`
Expected: build errors on `ProjectedAccounts`/`ProjectedAccount`.

- [ ] **Step 4: Create the entity**

`CoreBankDemo.PaymentsAPI/Accounts/ProjectedAccount.cs`:

```csharp
namespace CoreBankDemo.PaymentsAPI.Accounts;

/// <summary>
/// PaymentsAPI's local view of one account (spec: payments-account-projection,
/// ADR-028), built only from the payments it accepts and the
/// <c>transaction-events</c> CoreBank publishes -- never seeded, never read
/// from CoreBank. <see cref="SettledBalance"/> is the last <c>NewBalance</c>
/// CoreBank reported (<see langword="null"/> until the first
/// <c>balance.updated</c>); <see cref="Reserved"/> is the sum of accepted
/// debits whose outcome PaymentsAPI has not yet seen. Available funds are
/// <c>SettledBalance - Reserved</c>, defined only once the former is known.
/// </summary>
public class ProjectedAccount
{
    public required string AccountNumber { get; set; }

    public decimal? SettledBalance { get; set; }

    /// <summary>Never negative; a release that would overshoot clamps to zero.</summary>
    public decimal Reserved { get; set; }

    public string? Currency { get; set; }

    public DateTime UpdatedAt { get; set; }
}
```

- [ ] **Step 5: Add the DbSet and model**

In `PaymentsDbContext.cs` add `using CoreBankDemo.PaymentsAPI.Accounts;`, the DbSet

```csharp
    public DbSet<ProjectedAccount> ProjectedAccounts => Set<ProjectedAccount>();
```

and, at the end of `OnModelCreating`:

```csharp
        modelBuilder.Entity<ProjectedAccount>(entity =>
        {
            entity.ToTable("ProjectedAccounts");
            entity.HasKey(e => e.AccountNumber);
            entity.Property(e => e.AccountNumber).IsRequired().HasMaxLength(50);
            entity.Property(e => e.SettledBalance).HasPrecision(18, 2);
            entity.Property(e => e.Reserved).HasPrecision(18, 2).IsRequired().HasDefaultValue(0m);
            entity.Property(e => e.Currency).HasMaxLength(3);
            entity.Property(e => e.UpdatedAt).IsRequired();
        });
```

- [ ] **Step 6: Truncate it on reset**

In `DatabaseResetCoordinator.cs`, after the Payments `InboxMessages` truncate:

```csharp
        await paymentsDb.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE \"ProjectedAccounts\" RESTART IDENTITY CASCADE", cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 7: Run the two tests**

Same command as Step 3. Expected: `Passed!`.

- [ ] **Step 8: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Accounts/ProjectedAccount.cs CoreBankDemo.PaymentsAPI/PaymentsDbContext.cs CoreBankDemo.LoadTestSupport/DatabaseResetCoordinator.cs tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/PaymentsDbContextTests.cs tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/LoadTestDatabaseResetterTests.cs
git commit -m "feat(payments): ProjectedAccounts table and its load-test reset

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: `ProjectedAccountRows` and `IAccountProjectionStore`

**Files:**
- Create: `CoreBankDemo.PaymentsAPI/Accounts/ProjectedAccountRows.cs`
- Create: `CoreBankDemo.PaymentsAPI/Accounts/IAccountProjectionStore.cs`
- Create: `CoreBankDemo.PaymentsAPI/Accounts/AccountProjectionStore.cs`
- Modify: `tests/Directory.Build.props:27` (`PersistenceTierFilters`)
- Test: `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/AccountProjectionStoreTests.cs`

**Interfaces:**
- Produces:
  - `internal static class ProjectedAccountRows { static Task<ProjectedAccount> LockAsync(PaymentsDbContext db, string accountNumber, DateTime now, CancellationToken ct) }` — upsert-if-missing then `SELECT … FOR UPDATE`; returns the tracked row. Must be called inside an open transaction.
  - `internal interface IAccountProjectionStore { Task SettleAsync(string accountNumber, decimal newBalance, string currency, CancellationToken ct); Task<decimal> ReleaseAsync(string accountNumber, decimal amount, CancellationToken ct); }` — both lock, mutate and `SaveChanges`; `ReleaseAsync` returns the shortfall (the part of `amount` that could not be released because `Reserved` hit zero; `0` normally).

- [ ] **Step 1: Write the failing persistence tests**

`tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/AccountProjectionStoreTests.cs`:

```csharp
using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Accounts;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

/// <summary>
/// The inbox side of the account projection (spec: payments-account-projection)
/// against real PostgreSQL: upsert-if-missing, row locking, settle, release
/// and the clamp at zero.
/// </summary>
public class AccountProjectionStoreTests(PostgresContainerFixture fixture) : PaymentsPostgresTestBase(fixture)
{
    private const string Account = "NL91ABNA0417164300";

    [Fact]
    public async Task SettleAsync_creates_the_row_and_records_the_settled_balance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var store = new AccountProjectionStore(context, TimeProvider);

        await using (var transaction = await context.Database.BeginTransactionAsync(ct))
        {
            await store.SettleAsync(Account, 987.66m, "EUR", ct);
            await transaction.CommitAsync(ct);
        }

        await using var verification = CreateContext();
        var row = await verification.ProjectedAccounts.SingleAsync(ct);
        row.AccountNumber.Should().Be(Account);
        row.SettledBalance.Should().Be(987.66m);
        row.Reserved.Should().Be(0m);
        row.Currency.Should().Be("EUR");
        row.UpdatedAt.Should().Be(TimeProvider.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task SettleAsync_overwrites_an_existing_settled_balance_and_keeps_the_reservation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var seed = CreateContext())
        {
            seed.ProjectedAccounts.Add(new ProjectedAccount
            {
                AccountNumber = Account, SettledBalance = 100m, Reserved = 25m, Currency = "EUR",
                UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            await seed.SaveChangesAsync(ct);
        }
        await using var context = CreateContext();
        var store = new AccountProjectionStore(context, TimeProvider);

        await using (var transaction = await context.Database.BeginTransactionAsync(ct))
        {
            await store.SettleAsync(Account, 75m, "EUR", ct);
            await transaction.CommitAsync(ct);
        }

        await using var verification = CreateContext();
        var row = await verification.ProjectedAccounts.SingleAsync(ct);
        row.SettledBalance.Should().Be(75m);
        row.Reserved.Should().Be(25m, "settling never touches the reservation");
    }

    [Fact]
    public async Task ReleaseAsync_subtracts_the_amount_and_reports_no_shortfall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var seed = CreateContext())
        {
            seed.ProjectedAccounts.Add(new ProjectedAccount
            {
                AccountNumber = Account, SettledBalance = 100m, Reserved = 25m,
                UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            await seed.SaveChangesAsync(ct);
        }
        await using var context = CreateContext();
        var store = new AccountProjectionStore(context, TimeProvider);

        decimal shortfall;
        await using (var transaction = await context.Database.BeginTransactionAsync(ct))
        {
            shortfall = await store.ReleaseAsync(Account, 10m, ct);
            await transaction.CommitAsync(ct);
        }

        shortfall.Should().Be(0m);
        await using var verification = CreateContext();
        (await verification.ProjectedAccounts.SingleAsync(ct)).Reserved.Should().Be(15m);
    }

    [Fact]
    public async Task ReleaseAsync_on_an_unseen_account_creates_the_row_clamps_at_zero_and_reports_the_shortfall()
    {
        // A projection created after the payment was accepted (recreated
        // database) sees a release it never reserved for.
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var store = new AccountProjectionStore(context, TimeProvider);

        decimal shortfall;
        await using (var transaction = await context.Database.BeginTransactionAsync(ct))
        {
            shortfall = await store.ReleaseAsync(Account, 12.34m, ct);
            await transaction.CommitAsync(ct);
        }

        shortfall.Should().Be(12.34m);
        await using var verification = CreateContext();
        var row = await verification.ProjectedAccounts.SingleAsync(ct);
        row.Reserved.Should().Be(0m);
        row.SettledBalance.Should().BeNull();
    }

    [Fact]
    public async Task LockAsync_blocks_a_second_writer_until_the_first_commits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = CreateStore();
        var (first, second) = store.CreateCompetingContexts();
        await using var firstHandle = first;
        await using var secondHandle = second;
        var now = TimeProvider.GetUtcNow().UtcDateTime;

        await using var firstTransaction = await first.Database.BeginTransactionAsync(ct);
        await ProjectedAccountRows.LockAsync(first, Account, now, ct);

        await using var secondTransaction = await second.Database.BeginTransactionAsync(ct);
        var secondLock = ProjectedAccountRows.LockAsync(second, Account, now, ct);
        var completedEarly = await Task.WhenAny(secondLock, Task.Delay(TimeSpan.FromMilliseconds(500), ct));
        completedEarly.Should().NotBeSameAs(secondLock, "the row lock must hold the second writer");

        await firstTransaction.CommitAsync(ct);
        (await secondLock.WaitAsync(PostgresContainerFixture.LockWaitTimeout, ct)).AccountNumber.Should().Be(Account);
        await secondTransaction.CommitAsync(ct);

        await using var verification = CreateContext();
        (await verification.ProjectedAccounts.CountAsync(ct)).Should().Be(1, "ON CONFLICT DO NOTHING creates the row once");
    }
}
```

- [ ] **Step 2: Run to see the build fail**

Run: `dotnet test tests/CoreBankDemo.Persistence.IntegrationTests --filter "FullyQualifiedName~AccountProjectionStoreTests"`
Expected: build errors on `AccountProjectionStore`/`ProjectedAccountRows`.

- [ ] **Step 3: Write the row helper**

`CoreBankDemo.PaymentsAPI/Accounts/ProjectedAccountRows.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.PaymentsAPI.Accounts;

/// <summary>
/// The one place that knows how a <see cref="ProjectedAccount"/> row is
/// created and locked (ruling A6: raw SQL stays in persistence classes). Both
/// writers -- the accept path in <c>OutboxRepository.AcceptAsync</c> and the
/// inbox path in <see cref="AccountProjectionStore"/> -- lock through here, so
/// a debit and a settlement for one account serialise on the same row lock.
/// </summary>
internal static class ProjectedAccountRows
{
    /// <summary>
    /// Creates the row if it does not exist (<c>ON CONFLICT DO NOTHING</c>, so
    /// two first-time writers cannot both fail to create it) and then takes a
    /// <c>SELECT … FOR UPDATE</c> lock on it. Must run inside an open
    /// transaction: the lock is released when that transaction ends.
    /// </summary>
    public static async Task<ProjectedAccount> LockAsync(
        PaymentsDbContext dbContext,
        string accountNumber,
        DateTime now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrEmpty(accountNumber);

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "ProjectedAccounts" ("AccountNumber", "SettledBalance", "Reserved", "Currency", "UpdatedAt")
             VALUES ({accountNumber}, NULL, 0, NULL, {now})
             ON CONFLICT ("AccountNumber") DO NOTHING
             """,
            cancellationToken).ConfigureAwait(false);

        return await dbContext.ProjectedAccounts
            .FromSqlInterpolated($"SELECT * FROM \"ProjectedAccounts\" WHERE \"AccountNumber\" = {accountNumber} FOR UPDATE")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Write the port and the store**

`CoreBankDemo.PaymentsAPI/Accounts/IAccountProjectionStore.cs`:

```csharp
namespace CoreBankDemo.PaymentsAPI.Accounts;

/// <summary>
/// The inbox side of the account projection (spec: payments-account-projection).
/// Every member locks the account row, applies one change and saves it; the
/// caller owns the surrounding transaction (<c>ExecuteInTransactionAsync</c>),
/// so a save here is only durable once the inbox row's completion commits
/// with it. Narrow so <c>TransactionEventHandler</c> stays unit-testable
/// through a mock.
/// </summary>
internal interface IAccountProjectionStore
{
    /// <summary>Records CoreBank's reported balance for the account; never touches the reservation.</summary>
    Task SettleAsync(string accountNumber, decimal newBalance, string currency, CancellationToken cancellationToken);

    /// <summary>
    /// Releases <paramref name="amount"/> of the account's reservation, clamping
    /// at zero.
    /// </summary>
    /// <returns>
    /// The part of <paramref name="amount"/> that could not be released
    /// because the reservation was already smaller (<c>0</c> normally).
    /// </returns>
    Task<decimal> ReleaseAsync(string accountNumber, decimal amount, CancellationToken cancellationToken);
}
```

`CoreBankDemo.PaymentsAPI/Accounts/AccountProjectionStore.cs`:

```csharp
namespace CoreBankDemo.PaymentsAPI.Accounts;

internal sealed class AccountProjectionStore(PaymentsDbContext dbContext, TimeProvider timeProvider) : IAccountProjectionStore
{
    public async Task SettleAsync(string accountNumber, decimal newBalance, string currency, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var row = await ProjectedAccountRows.LockAsync(dbContext, accountNumber, now, cancellationToken).ConfigureAwait(false);
        row.SettledBalance = newBalance;
        row.Currency = currency;
        row.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<decimal> ReleaseAsync(string accountNumber, decimal amount, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var row = await ProjectedAccountRows.LockAsync(dbContext, accountNumber, now, cancellationToken).ConfigureAwait(false);
        var released = Math.Min(amount, row.Reserved);
        row.Reserved -= released;
        row.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return amount - released;
    }
}
```

- [ ] **Step 5: Put both classes in the persistence tier**

In `tests/Directory.Build.props`, append to `PersistenceTierFilters` (same line, comma-separated):

```
,[CoreBankDemo.PaymentsAPI]CoreBankDemo.PaymentsAPI.Accounts.AccountProjectionStore*,[CoreBankDemo.PaymentsAPI]CoreBankDemo.PaymentsAPI.Accounts.ProjectedAccountRows*
```

- [ ] **Step 6: Run the tests**

Same command as Step 2. Expected: `Passed!` (5 tests).

- [ ] **Step 7: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Accounts tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/AccountProjectionStoreTests.cs tests/Directory.Build.props
git commit -m "feat(payments): account projection store with row locking and clamped release

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: `OutboxRepository.AcceptAsync` — the transactional accept

**Files:**
- Modify: `CoreBankDemo.PaymentsAPI/Outbox/OutboxRepository.cs`
- Test: `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/OutboxRepositoryAcceptTests.cs`

**Interfaces:**
- Consumes: `ProjectedAccountRows.LockAsync` (Task 3).
- Produces: `internal enum PaymentAcceptance { Stored, Duplicate, InsufficientFunds }` and `IOutboxRepository.AcceptAsync(OutboxMessage message, CancellationToken ct) : Task<PaymentAcceptance>`. `StoreIfNewAsync` stays on the interface (the kernel and existing tests use it).

- [ ] **Step 1: Write the failing persistence tests**

`tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/OutboxRepositoryAcceptTests.cs`:

```csharp
using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI.Accounts;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

/// <summary>
/// The transactional-outbox accept (spec: payments-account-projection): the
/// outbox insert and the debtor's reservation commit together or not at all,
/// insert-first dedupe is kept, and a known-short account is refused without
/// leaving a row.
/// </summary>
public class OutboxRepositoryAcceptTests(PostgresContainerFixture fixture) : PaymentsPostgresTestBase(fixture)
{
    private const string Debtor = "NL91ABNA0417164300";

    [Fact]
    public async Task AcceptAsync_commits_the_outbox_row_and_the_reservation_together()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var outcome = await repository.AcceptAsync(PaymentsApiTestData.Outbox("accept-1"), ct);

        outcome.Should().Be(PaymentAcceptance.Stored);
        await using var verification = CreateContext();
        (await verification.OutboxMessages.CountAsync(m => m.IdempotencyKey == "accept-1", ct)).Should().Be(1);
        var account = await verification.ProjectedAccounts.SingleAsync(ct);
        account.AccountNumber.Should().Be(Debtor);
        account.SettledBalance.Should().BeNull("PaymentsAPI never seeds a balance");
        account.Reserved.Should().Be(12.34m);
    }

    [Fact]
    public async Task AcceptAsync_accumulates_reservations_for_a_known_account_with_enough_funds()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(settled: 100m, reserved: 50m, ct);
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var outcome = await repository.AcceptAsync(PaymentsApiTestData.Outbox("accept-2"), ct);

        outcome.Should().Be(PaymentAcceptance.Stored);
        await using var verification = CreateContext();
        (await verification.ProjectedAccounts.SingleAsync(ct)).Reserved.Should().Be(62.34m);
    }

    [Fact]
    public async Task AcceptAsync_refuses_a_known_short_account_and_leaves_nothing_behind()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(settled: 100m, reserved: 90m, ct); // available 10.00 < 12.34
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        var outcome = await repository.AcceptAsync(PaymentsApiTestData.Outbox("refused"), ct);

        outcome.Should().Be(PaymentAcceptance.InsufficientFunds);
        await using var verification = CreateContext();
        (await verification.OutboxMessages.CountAsync(ct)).Should().Be(0, "a refused payment leaves no row");
        (await verification.ProjectedAccounts.SingleAsync(ct)).Reserved.Should().Be(90m, "and no reservation");
    }

    [Fact]
    public async Task AcceptAsync_accepts_exactly_the_available_amount()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(settled: 100m, reserved: 87.66m, ct); // available 12.34 == amount
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        (await repository.AcceptAsync(PaymentsApiTestData.Outbox("exact"), ct)).Should().Be(PaymentAcceptance.Stored);
    }

    [Fact]
    public async Task AcceptAsync_never_refuses_while_the_settled_balance_is_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(settled: null, reserved: 1_000_000m, ct);
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);

        (await repository.AcceptAsync(PaymentsApiTestData.Outbox("unknown"), ct)).Should().Be(PaymentAcceptance.Stored);
    }

    [Fact]
    public async Task AcceptAsync_duplicate_key_rolls_back_before_the_balance_is_consulted_and_keeps_the_context_usable()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(settled: 0m, reserved: 0m, ct); // would be refused if the balance were checked
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        await using (var seed = CreateContext())
        {
            seed.OutboxMessages.Add(PaymentsApiTestData.Outbox("dup"));
            await seed.SaveChangesAsync(ct);
        }

        var outcome = await repository.AcceptAsync(PaymentsApiTestData.Outbox("dup"), ct);

        outcome.Should().Be(PaymentAcceptance.Duplicate);
        var winner = await repository.FindByIdempotencyKeyAsync("dup", ct);
        winner.Should().NotBeNull("the context must still work after the rolled-back insert");
        await using var verification = CreateContext();
        (await verification.OutboxMessages.CountAsync(ct)).Should().Be(1);
        (await verification.ProjectedAccounts.SingleAsync(ct)).Reserved.Should().Be(0m);
    }

    [Fact]
    public async Task AcceptAsync_refused_key_is_not_consumed()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(settled: 10m, reserved: 0m, ct);
        await using var context = CreateContext();
        var repository = new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        (await repository.AcceptAsync(PaymentsApiTestData.Outbox("later"), ct)).Should().Be(PaymentAcceptance.InsufficientFunds);

        await using (var funds = CreateContext())
        {
            var row = await funds.ProjectedAccounts.SingleAsync(ct);
            row.SettledBalance = 500m;
            await funds.SaveChangesAsync(ct);
        }

        (await repository.AcceptAsync(PaymentsApiTestData.Outbox("later"), ct)).Should().Be(PaymentAcceptance.Stored);
    }

    [Fact]
    public async Task AcceptAsync_two_concurrent_debits_cannot_both_pass_on_the_same_funds()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(settled: 20m, reserved: 0m, ct); // room for one 12.34, not two
        await using var store = CreateStore();
        var (first, second) = store.CreateCompetingContexts();
        await using var firstHandle = first;
        await using var secondHandle = second;
        var firstRepository = new OutboxRepository(first, TimeProvider, TestBusinessMetrics.Instance);
        var secondRepository = new OutboxRepository(second, TimeProvider, TestBusinessMetrics.Instance);

        var outcomes = await PaymentsApiTestData.RaceAsync(
            () => firstRepository.AcceptAsync(PaymentsApiTestData.Outbox("race-a"), ct),
            () => secondRepository.AcceptAsync(PaymentsApiTestData.Outbox("race-b"), ct));

        outcomes.Should().BeEquivalentTo([PaymentAcceptance.Stored, PaymentAcceptance.InsufficientFunds]);
        await using var verification = CreateContext();
        (await verification.OutboxMessages.CountAsync(ct)).Should().Be(1);
        (await verification.ProjectedAccounts.SingleAsync(ct)).Reserved.Should().Be(12.34m);
    }

    [Fact]
    public async Task AcceptAsync_two_concurrent_first_debits_create_one_row_and_sum_reservations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = CreateStore();
        var (first, second) = store.CreateCompetingContexts();
        await using var firstHandle = first;
        await using var secondHandle = second;
        var firstRepository = new OutboxRepository(first, TimeProvider, TestBusinessMetrics.Instance);
        var secondRepository = new OutboxRepository(second, TimeProvider, TestBusinessMetrics.Instance);

        var outcomes = await PaymentsApiTestData.RaceAsync(
            () => firstRepository.AcceptAsync(PaymentsApiTestData.Outbox("first-a"), ct),
            () => secondRepository.AcceptAsync(PaymentsApiTestData.Outbox("first-b"), ct));

        outcomes.Should().AllBeEquivalentTo(PaymentAcceptance.Stored);
        await using var verification = CreateContext();
        var account = await verification.ProjectedAccounts.SingleAsync(ct);
        account.Reserved.Should().Be(24.68m);
    }

    private async Task Seed(decimal? settled, decimal reserved, CancellationToken ct)
    {
        await using var seed = CreateContext();
        seed.ProjectedAccounts.Add(new ProjectedAccount
        {
            AccountNumber = Debtor, SettledBalance = settled, Reserved = reserved, Currency = "EUR",
            UpdatedAt = TimeProvider.GetUtcNow().UtcDateTime
        });
        await seed.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 2: Run to see the build fail**

Run: `dotnet test tests/CoreBankDemo.Persistence.IntegrationTests --filter "FullyQualifiedName~OutboxRepositoryAcceptTests"`
Expected: build errors on `PaymentAcceptance`/`AcceptAsync`.

- [ ] **Step 3: Add the enum and interface member**

In `OutboxRepository.cs`, above `IOutboxRepository`:

```csharp
/// <summary>Outcome of <see cref="IOutboxRepository.AcceptAsync"/>.</summary>
internal enum PaymentAcceptance
{
    /// <summary>Row inserted and the debtor's reservation raised, in one commit.</summary>
    Stored,
    /// <summary>The idempotency key already exists; nothing written. The balance was never consulted.</summary>
    Duplicate,
    /// <summary>The debtor's known available balance is short; nothing written, key not consumed.</summary>
    InsufficientFunds
}
```

and on the interface, after `StoreIfNewAsync`:

```csharp
    /// <summary>
    /// The transactional-outbox accept (spec: payments-account-projection,
    /// ADR-028): inserts <paramref name="message"/> and raises the debtor's
    /// <see cref="Accounts.ProjectedAccount.Reserved"/> in one database
    /// transaction. The insert runs first, so a duplicate key is detected by
    /// the unique index before any balance is read (AD-4), and a refusal rolls
    /// the insert back so no row and no reservation remain.
    /// </summary>
    Task<PaymentAcceptance> AcceptAsync(OutboxMessage message, CancellationToken cancellationToken);
```

- [ ] **Step 4: Implement `AcceptAsync`**

Add `using CoreBankDemo.PaymentsAPI.Accounts;` and these members to `OutboxRepository`:

```csharp
    public async Task<PaymentAcceptance> AcceptAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            await ExecuteInTransactionAsync(async () =>
            {
                // 1. Insert first (AD-4). A unique violation aborts the
                //    PostgreSQL transaction, so the only way out is a rollback.
                OutboxMessages.Add(message);
                try
                {
                    await DbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException ex) when (UniqueViolation.IsUniqueViolation(ex))
                {
                    throw new AcceptanceRollback(PaymentAcceptance.Duplicate);
                }

                // 2. Lock the debtor's projection row (created if unseen).
                var now = timeProvider.GetUtcNow().UtcDateTime;
                var account = await ProjectedAccountRows
                    .LockAsync(DbContext, message.FromAccount, now, cancellationToken).ConfigureAwait(false);

                // 3. Refuse only what the projection knows is short.
                if (account.SettledBalance is { } settled && settled - account.Reserved < message.Amount)
                {
                    throw new AcceptanceRollback(PaymentAcceptance.InsufficientFunds);
                }

                // 4. Reserve and commit with the row.
                account.Reserved += message.Amount;
                account.UpdatedAt = now;
                await DbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceRollback rollback)
        {
            DbContext.Entry(message).State = EntityState.Detached;
            if (rollback.Outcome == PaymentAcceptance.Duplicate)
            {
                // Only a dedupe hit is a store "duplicate"; a refusal is
                // counted by the payment-intake metric, not by the store.
                businessMetrics.RecordStoreOperation(
                    StoreName, BusinessMetrics.StoreKind.Outbox, BusinessMetrics.StoreOperationOutcome.Duplicate);
            }

            return rollback.Outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DbContext.Entry(message).State = EntityState.Detached;
            throw;
        }
        catch
        {
            DbContext.Entry(message).State = EntityState.Detached;
            businessMetrics.RecordStoreOperation(
                StoreName, BusinessMetrics.StoreKind.Outbox, BusinessMetrics.StoreOperationOutcome.Failed);
            throw;
        }

        businessMetrics.RecordStoreOperation(
            StoreName, BusinessMetrics.StoreKind.Outbox, BusinessMetrics.StoreOperationOutcome.Added);
        return PaymentAcceptance.Stored;
    }

    /// <summary>
    /// Carries a deliberate rollback out of <see cref="ExecuteInTransactionAsync"/>;
    /// never escapes <see cref="AcceptAsync"/>. Not transient, so the Npgsql
    /// execution strategy never retries it.
    /// </summary>
    private sealed class AcceptanceRollback(PaymentAcceptance outcome) : Exception
    {
        public PaymentAcceptance Outcome { get; } = outcome;
    }
```

Notes for the implementer: `timeProvider` and `businessMetrics` are the primary-constructor parameters already in scope; `StoreName`, `OutboxMessages`, `DbContext`, `ExecuteInTransactionAsync` come from the base classes. The `AcceptanceRollback` from inside the delegate makes `ExecuteInTransactionAsync` roll back and rethrow, which is exactly what both non-stored outcomes need.

- [ ] **Step 5: Run the tests**

Same command as Step 2. Expected: `Passed!` (9 tests). Also run `dotnet test tests/CoreBankDemo.Persistence.IntegrationTests --filter "FullyQualifiedName~OutboxRepositoryTests"` to confirm the existing repository tests still pass.

- [ ] **Step 6: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Outbox/OutboxRepository.cs tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/OutboxRepositoryAcceptTests.cs
git commit -m "feat(payments): AcceptAsync commits the outbox row with the debtor's reservation

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: `PaymentStorageHandler` refuses on `InsufficientFunds`

**Files:**
- Modify: `CoreBankDemo.PaymentsAPI/Handlers/PaymentStorageHandler.cs`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStorageHandlerTests.cs`

**Interfaces:**
- Consumes: `IOutboxRepository.AcceptAsync` → `PaymentAcceptance` (Task 4); `BusinessMetrics.PaymentOutcome.InsufficientFunds` (Task 1).
- Produces: `PaymentStorageOutcome.InsufficientFunds`; `PaymentStorageResult(InsufficientFunds, null, ["Insufficient funds"])`.

- [ ] **Step 1: Move the existing mocks to `AcceptAsync`**

In `PaymentStorageHandlerTests.cs`, every `Setup(store => store.StoreIfNewAsync(It.IsAny<OutboxMessage>(), …))…ReturnsAsync(true)` becomes

```csharp
            .Setup(store => store.AcceptAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((message, _) => captured = message)
            .ReturnsAsync(PaymentAcceptance.Stored);
```

(the `Request_fields_cancellation_and_structured_scope_are_forwarded` setup keeps its explicit `cancellation.Token`), and the duplicate tests' `ReturnsAsync(false)` becomes `ReturnsAsync(PaymentAcceptance.Duplicate)`. `Infrastructure_errors_propagate` throws from `AcceptAsync` instead. Nothing else in those tests changes.

- [ ] **Step 2: Add the failing new tests**

```csharp
    [Fact]
    public async Task Insufficient_funds_returns_the_outcome_with_a_single_bare_error_and_no_snapshot()
    {
        var repository = new Mock<IOutboxRepository>(MockBehavior.Strict);
        repository
            .Setup(store => store.AcceptAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentAcceptance.InsufficientFunds);
        var logger = new CapturingLogger();
        var handler = CreateHandler(repository.Object, logger);

        var result = await handler.StoreAsync(Request, "short-key", TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(PaymentStorageOutcome.InsufficientFunds);
        result.Payment.Should().BeNull();
        result.Errors.Should().Equal("Insufficient funds");
        logger.Messages.Should().ContainSingle(message => message.Contains("Refused payment short-key"));
        repository.Verify(store => store.FindByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(PaymentSchemes.Standard, "standard")]
    [InlineData(PaymentSchemes.Instant, "instant")]
    public async Task Insufficient_funds_records_the_intake_metric_for_the_scheme(string scheme, string expectedScheme)
    {
        var repository = new Mock<IOutboxRepository>();
        repository
            .Setup(store => store.AcceptAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentAcceptance.InsufficientFunds);
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var handler = CreateHandler(repository.Object, businessMetrics: businessMetrics);

        await handler.StoreAsync(Request with { Scheme = scheme }, "short-key", TestContext.Current.CancellationToken);

        var measurement = listener.Measurements.Should().ContainSingle(m => m.InstrumentName == "corebankdemo.payment.intake").Which;
        measurement.Tags["outcome"].Should().Be("insufficient_funds");
        measurement.Tags["payment.scheme"].Should().Be(expectedScheme);
    }

    [Fact]
    public async Task Insufficient_funds_tags_the_current_span_as_a_rejected_payment()
    {
        var repository = new Mock<IOutboxRepository>();
        repository
            .Setup(store => store.AcceptAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentAcceptance.InsufficientFunds);
        var handler = CreateHandler(repository.Object);
        using var activity = new Activity("accept-payment").SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();

        await handler.StoreAsync(Request, "short-key", TestContext.Current.CancellationToken);

        activity.TagObjects.Should().Contain(
            new KeyValuePair<string, object?>(FailedPaymentTags.Outcome, FailedPaymentTags.Rejected),
            new KeyValuePair<string, object?>(FailedPaymentTags.FailureReason, "insufficient_funds"),
            new KeyValuePair<string, object?>(FailedPaymentTags.TransactionId, "short-key"));
    }
```

- [ ] **Step 3: Run to see the failures**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~PaymentStorageHandlerTests"`
Expected: build error on `PaymentStorageOutcome.InsufficientFunds`.

- [ ] **Step 4: Implement**

In `PaymentStorageHandler.cs`:

```csharp
public enum PaymentStorageOutcome
{
    Stored,
    Duplicate,
    ValidationFailed,
    /// <summary>Refused at the door by the local account projection (ADR-028); nothing stored.</summary>
    InsufficientFunds
}
```

Replace the block from `if (await repository.StoreIfNewAsync(...))` to the end of `StoreAsync` with:

```csharp
        switch (await repository.AcceptAsync(message, cancellationToken).ConfigureAwait(false))
        {
            case PaymentAcceptance.Stored:
                logger.LogInformation(
                    "Stored payment {IdempotencyKey} in partition {PartitionId}",
                    key,
                    partitionId);
                businessMetrics.RecordPaymentIntake(BusinessMetrics.PaymentOutcome.Stored, scheme);
                return new PaymentStorageResult(PaymentStorageOutcome.Stored, ToSnapshot(message), []);

            case PaymentAcceptance.InsufficientFunds:
                // ADR-028: a door refusal. Nothing was accepted, so CoreBank
                // stays the only source of outcomes; the figures stay in the
                // log and on the span, never in the response.
                logger.LogWarning(
                    "Refused payment {IdempotencyKey} in partition {PartitionId}: account {FromAccount} is known to be short of {Amount} {Currency}",
                    key,
                    partitionId,
                    request.FromAccount,
                    normalizedAmount,
                    request.Currency);
                var activity = Activity.Current;
                activity?.SetTag(FailedPaymentTags.Outcome, FailedPaymentTags.Rejected);
                activity?.SetTag(FailedPaymentTags.FailureReason, InsufficientFundsReason);
                activity?.SetTag(FailedPaymentTags.TransactionId, key);
                businessMetrics.RecordPaymentIntake(BusinessMetrics.PaymentOutcome.InsufficientFunds, scheme);
                return new PaymentStorageResult(PaymentStorageOutcome.InsufficientFunds, null, [InsufficientFundsError]);

            case PaymentAcceptance.Duplicate:
            default:
                break;
        }

        logger.LogInformation(
            "Payment {IdempotencyKey} already exists in partition {PartitionId}; loading persisted winner",
            key,
            partitionId);
        var winner = await repository.FindByIdempotencyKeyAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Payment store reported duplicate idempotency key '{key}', but no persisted winner was found.");

        businessMetrics.RecordPaymentIntake(BusinessMetrics.PaymentOutcome.Duplicate, scheme);
        return new PaymentStorageResult(PaymentStorageOutcome.Duplicate, ToSnapshot(winner), []);
```

and two constants on the class:

```csharp
    /// <summary>The only text a refused caller sees; amounts never leave the log and the span.</summary>
    internal const string InsufficientFundsError = "Insufficient funds";

    /// <summary>Span tag value for the traces dashboard's failed-payments table.</summary>
    internal const string InsufficientFundsReason = "insufficient_funds";
```

- [ ] **Step 5: Run the handler tests**

Same command as Step 3. Expected: `Passed!`.

- [ ] **Step 6: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Handlers/PaymentStorageHandler.cs tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStorageHandlerTests.cs
git commit -m "feat(payments): refuse a debit the projection knows is unfunded

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: `PaymentsController` answers `422`

**Files:**
- Modify: `CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs:576-594`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsControllerTests.cs`

**Interfaces:**
- Consumes: `PaymentStorageOutcome.InsufficientFunds` (Task 5).

- [ ] **Step 1: Write the failing tests**

```csharp
    [Theory]
    [InlineData(PaymentSchemes.Standard)]
    [InlineData(PaymentSchemes.Instant)]
    public async Task ProcessPayment_returns_422_with_the_handler_errors_when_funds_are_insufficient_on_either_rail(string scheme)
    {
        var request = ValidRequest() with { Scheme = scheme };
        _handler
            .Setup(h => h.StoreAsync(request, IdempotencyKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.InsufficientFunds, null, ["Insufficient funds"]));
        var controller = CreateController(IdempotencyKey);

        var result = await controller.ProcessPayment(request, TestContext.Current.CancellationToken);

        var unprocessable = result.Should().BeOfType<ObjectResult>().Subject;
        unprocessable.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        GetErrors(unprocessable.Value).Should().Equal("Insufficient funds");
        _instantHandler.VerifyNoOtherCalls();
    }
```

(`GetErrors` already exists in this class; `_instantHandler` is strict, so the instant rail never starting is proved by the absence of a setup.)

- [ ] **Step 2: Run to see it fail**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~PaymentsControllerTests"`
Expected: `InvalidOperationException: Unhandled payment storage outcome: InsufficientFunds` on both rows.

- [ ] **Step 3: Add the arm**

In `ProcessPayment`'s switch, after the `ValidationFailed` arm:

```csharp
            // ADR-028: a door refusal from the local account projection. Both
            // rails answer the same; nothing was stored, so there is no
            // Location and the instant inline attempt never starts.
            PaymentStorageOutcome.InsufficientFunds =>
                UnprocessableEntity(new { Errors = result.Errors }),
```

- [ ] **Step 4: Run the controller tests**

Same command. Expected: `Passed!`.

- [ ] **Step 5: Update the controller's class summary**

In the `<summary>` of `PaymentsController`, after "map results to an `IActionResult`", add: "a `PaymentStorageOutcome.InsufficientFunds` maps to `422` (ADR-028)".

- [ ] **Step 6: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsControllerTests.cs
git commit -m "feat(payments): 422 Unprocessable Content for a refused debit

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: `TransactionEventHandler` updates the projection in the inbox transaction

**Files:**
- Modify: `CoreBankDemo.PaymentsAPI/Inbox/InboxMessageRepository.cs` (interface)
- Modify: `CoreBankDemo.PaymentsAPI/Handlers/TransactionEventHandler.cs`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/TransactionEventHandlerTests.cs`
- Test: `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/TransactionEventHandlerTests.cs` (create)
- Modify: `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/InboxProcessorTests.cs:116-118` (handler construction)

**Interfaces:**
- Consumes: `IAccountProjectionStore` (Task 3); `IOutboxRepository.FindByIdempotencyKeyAsync` (existing; PaymentsAPI stores `TransactionId = IdempotencyKey`, ADR-027).
- Produces: `IInboxMessageRepository` gains `Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken)` and `Task<MessageTransitionOutcome> MarkAsCompletedAsync(InboxMessage message, CancellationToken cancellationToken)` — both already implemented by the inherited base; the interface only exposes them. New handler constructor: `TransactionEventHandler(ILogger<TransactionEventHandler> logger, IOutboxRepository outboxRepository, IInboxMessageRepository inboxRepository, IAccountProjectionStore accounts)`.

- [ ] **Step 1: Expose the two kernel members on the port**

In `InboxMessageRepository.cs`:

```csharp
internal interface IInboxMessageRepository
{
    Task<bool> StoreIfNewAsync(InboxMessage message, CancellationToken cancellationToken);

    /// <summary>Kernel transaction helper, exposed so <c>TransactionEventHandler</c> can make its projection writes and the row's completion one commit (spec: payments-account-projection).</summary>
    Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken);

    /// <summary>The kernel's own completion transition; called by the handler inside its transaction so the processor's later call finds the row <see cref="MessageTransitionOutcome.AlreadyTerminal"/>.</summary>
    Task<MessageTransitionOutcome> MarkAsCompletedAsync(InboxMessage message, CancellationToken cancellationToken);
}
```

No implementation change: `InboxMessageRepositoryBase` already has both as public virtuals with these signatures.

- [ ] **Step 2: Rewrite the unit tests' handler construction**

In `tests/CoreBankDemo.PaymentsAPI.Tests/TransactionEventHandlerTests.cs` add a helper and replace every `new TransactionEventHandler(logger-or-new CapturingLogger(), <outbox mock>.Object)` with `CreateHandler(logger, <outbox mock>)` (default arguments where the old call used `new CapturingLogger()` / `new Mock<IOutboxRepository>().Object`):

```csharp
    private static TransactionEventHandler CreateHandler(
        ILogger<TransactionEventHandler>? logger = null,
        Mock<IOutboxRepository>? outbox = null,
        Mock<IAccountProjectionStore>? accounts = null,
        Mock<IInboxMessageRepository>? inbox = null)
    {
        inbox ??= TransactionalInbox();
        return new TransactionEventHandler(
            logger ?? new CapturingLogger(),
            (outbox ?? new Mock<IOutboxRepository>()).Object,
            inbox.Object,
            (accounts ?? new Mock<IAccountProjectionStore>()).Object);
    }

    /// <summary>A repository whose transaction just runs the delegate and whose completion succeeds.</summary>
    private static Mock<IInboxMessageRepository> TransactionalInbox()
    {
        var inbox = new Mock<IInboxMessageRepository>();
        inbox
            .Setup(r => r.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<Task>, CancellationToken>((operation, _) => operation());
        inbox
            .Setup(r => r.MarkAsCompletedAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        return inbox;
    }
```

If any existing test asserts that the handler leaves `message.Status` untouched, change it to assert `inbox.Verify(r => r.MarkAsCompletedAsync(message, It.IsAny<CancellationToken>()), Times.Once)` instead — completion is now the handler's job. Update the class `<summary>` ("never mutates InboxMessage itself") accordingly.

- [ ] **Step 3: Add the failing projection tests**

```csharp
    [Fact]
    public async Task Balance_update_for_the_debtor_settles_and_releases_the_payment_amount_inside_the_transaction()
    {
        var accounts = new Mock<IAccountProjectionStore>(MockBehavior.Strict);
        var outbox = new Mock<IOutboxRepository>();
        var inbox = TransactionalInbox();
        var calls = new List<string>();
        outbox
            .Setup(r => r.FindByIdempotencyKeyAsync("txn-4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentsApiTestData.Outbox("txn-4")); // FromAccount NL91…, Amount 12.34
        accounts
            .Setup(a => a.SettleAsync("NL91ABNA0417164300", 987.66m, "EUR", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("settle")).Returns(Task.CompletedTask);
        accounts
            .Setup(a => a.ReleaseAsync("NL91ABNA0417164300", 12.34m, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("release")).ReturnsAsync(0m);
        inbox
            .Setup(r => r.MarkAsCompletedAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("complete")).ReturnsAsync(MessageTransitionOutcome.Applied);
        var message = Inbox(Constants.BalanceUpdated, "txn-4", accountNumber: "NL91ABNA0417164300",
            payload: Serialize(new BalanceUpdatedEvent("txn-4", "NL91ABNA0417164300", -12.34m, 987.66m, "EUR")));
        var handler = CreateHandler(outbox: outbox, accounts: accounts, inbox: inbox);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        calls.Should().Equal("settle", "release", "complete");
        accounts.VerifyAll();
    }

    [Fact]
    public async Task Balance_update_for_the_creditor_only_settles()
    {
        var accounts = new Mock<IAccountProjectionStore>(MockBehavior.Strict);
        var outbox = new Mock<IOutboxRepository>();
        outbox
            .Setup(r => r.FindByIdempotencyKeyAsync("txn-4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentsApiTestData.Outbox("txn-4")); // debtor is NL91…, this event is for NL20…
        accounts
            .Setup(a => a.SettleAsync("NL20INGB0001234567", 1012.34m, "EUR", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var message = Inbox(Constants.BalanceUpdated, "txn-4", accountNumber: "NL20INGB0001234567",
            payload: Serialize(new BalanceUpdatedEvent("txn-4", "NL20INGB0001234567", 12.34m, 1012.34m, "EUR")));
        var handler = CreateHandler(outbox: outbox, accounts: accounts);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        accounts.Verify(a => a.ReleaseAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Balance_update_for_an_unknown_transaction_only_settles()
    {
        var accounts = new Mock<IAccountProjectionStore>(MockBehavior.Strict);
        var outbox = new Mock<IOutboxRepository>();
        outbox
            .Setup(r => r.FindByIdempotencyKeyAsync("someone-elses", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OutboxMessage?)null);
        accounts
            .Setup(a => a.SettleAsync("NL39RABO0300065264", 2500m, "EUR", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var message = Inbox(Constants.BalanceUpdated, "someone-elses", accountNumber: "NL39RABO0300065264",
            payload: Serialize(new BalanceUpdatedEvent("someone-elses", "NL39RABO0300065264", 5m, 2500m, "EUR")));
        var handler = CreateHandler(outbox: outbox, accounts: accounts);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        accounts.VerifyAll();
    }

    [Theory]
    [InlineData(Constants.TransactionFailed)]
    [InlineData(Constants.TransactionCancelled)]
    public async Task Failed_and_cancelled_release_the_debtor_reservation_and_record_the_outcome(string eventType)
    {
        var accounts = new Mock<IAccountProjectionStore>(MockBehavior.Strict);
        var outbox = new Mock<IOutboxRepository>();
        outbox
            .Setup(r => r.FindByIdempotencyKeyAsync("txn-r", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentsApiTestData.Outbox("txn-r"));
        outbox
            .Setup(r => r.RecordCommittedOutcomeAsync("txn-r", It.IsAny<string>(), Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        accounts
            .Setup(a => a.ReleaseAsync("NL91ABNA0417164300", 12.34m, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        var payload = eventType == Constants.TransactionFailed
            ? Serialize(new TransactionFailedEvent("txn-r", "Failed", Now, "Insufficient funds"))
            : Serialize(new TransactionCancelledEvent("txn-r", "Cancelled", Now, "budget"));
        var handler = CreateHandler(outbox: outbox, accounts: accounts);

        await handler.HandleAsync(Inbox(eventType, "txn-r", payload: payload), TestContext.Current.CancellationToken);

        accounts.VerifyAll();
        outbox.Verify(r => r.RecordCommittedOutcomeAsync("txn-r", It.IsAny<string>(), Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Completed_event_touches_no_account()
    {
        var accounts = new Mock<IAccountProjectionStore>(MockBehavior.Strict);
        var handler = CreateHandler(accounts: accounts);

        await handler.HandleAsync(
            Inbox(Constants.TransactionCompleted, "txn-1", payload: Serialize(new TransactionCompletedEvent("txn-1", "Completed", Now))),
            TestContext.Current.CancellationToken);

        accounts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Release_below_zero_clamps_and_warns()
    {
        var accounts = new Mock<IAccountProjectionStore>();
        var outbox = new Mock<IOutboxRepository>();
        outbox
            .Setup(r => r.FindByIdempotencyKeyAsync("txn-old", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentsApiTestData.Outbox("txn-old"));
        accounts
            .Setup(a => a.ReleaseAsync("NL91ABNA0417164300", 12.34m, It.IsAny<CancellationToken>()))
            .ReturnsAsync(12.34m); // nothing was reserved: projection younger than the payment
        var logger = new CapturingLogger();
        var message = Inbox(Constants.BalanceUpdated, "txn-old", accountNumber: "NL91ABNA0417164300",
            payload: Serialize(new BalanceUpdatedEvent("txn-old", "NL91ABNA0417164300", -12.34m, 100m, "EUR")));
        var handler = CreateHandler(logger, outbox, accounts);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("12.34"));
    }

    [Fact]
    public async Task A_failing_projection_write_propagates_and_never_completes_the_row()
    {
        var accounts = new Mock<IAccountProjectionStore>();
        var inbox = TransactionalInbox();
        accounts
            .Setup(a => a.SettleAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var message = Inbox(Constants.BalanceUpdated, "txn-x", accountNumber: "NL91ABNA0417164300",
            payload: Serialize(new BalanceUpdatedEvent("txn-x", "NL91ABNA0417164300", -1m, 1m, "EUR")));
        var handler = CreateHandler(accounts: accounts, inbox: inbox);

        var act = () => handler.HandleAsync(message, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        inbox.Verify(r => r.MarkAsCompletedAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Balance_update_tags_the_span_with_the_projection_state()
    {
        using var observedActivity = StartListenedActivity();
        var accounts = new Mock<IAccountProjectionStore>();
        var outbox = new Mock<IOutboxRepository>();
        outbox.Setup(r => r.FindByIdempotencyKeyAsync("txn-t", It.IsAny<CancellationToken>())).ReturnsAsync((OutboxMessage?)null);
        var message = Inbox(Constants.BalanceUpdated, "txn-t", accountNumber: "NL91ABNA0417164300",
            payload: Serialize(new BalanceUpdatedEvent("txn-t", "NL91ABNA0417164300", -1m, 99m, "EUR")));
        var handler = CreateHandler(outbox: outbox, accounts: accounts);

        await handler.HandleAsync(message, TestContext.Current.CancellationToken);

        observedActivity.Activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("account.settled_balance", 99m));
        observedActivity.Activity.TagObjects.Should().Contain(new KeyValuePair<string, object?>("account.released", 0m));
    }
```

Add `using CoreBankDemo.PaymentsAPI.Accounts;` to the test file.

- [ ] **Step 4: Run to see them fail**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~TransactionEventHandlerTests"`
Expected: build errors (constructor arity, missing interface members).

- [ ] **Step 5: Rewrite the handler**

Replace the class declaration and `HandleAsync` in `TransactionEventHandler.cs` (keep `SerializerOptions`, the three `Handle…` methods returning the outcome tuple, `HandleBalanceUpdated`'s tagging/logging and `Deserialize<TEvent>`):

```csharp
internal sealed class TransactionEventHandler(
    ILogger<TransactionEventHandler> logger,
    IOutboxRepository outboxRepository,
    IInboxMessageRepository inboxRepository,
    IAccountProjectionStore accounts)
    : IInboxMessageHandler<InboxMessage>
{
    // SerializerOptions unchanged

    public async Task HandleAsync(InboxMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["IdempotencyKey"] = message.IdempotencyKey,
            ["PartitionId"] = message.PartitionId,
            ["EventType"] = message.EventType
        });

        // Transactional inbox (spec: payments-account-projection): the event's
        // effect on the account projection, the cached outcome on the payment
        // row and this inbox row's completion commit together or not at all.
        // The kernel's MarkAsCompletedAsync afterwards finds the row terminal
        // and does nothing -- the same arrangement as CoreBank's
        // TransactionExecutionHandler (story 4.6). Any exception rolls all of
        // it back and the kernel records a retry (ADR-023).
        await inboxRepository.ExecuteInTransactionAsync(async () =>
        {
            switch (message.EventType)
            {
                case Constants.TransactionCompleted:
                    await RecordCommittedOutcomeAsync(HandleTransactionCompleted(message), cancellationToken).ConfigureAwait(false);
                    break;
                case Constants.TransactionFailed:
                    await RecordCommittedOutcomeAsync(HandleTransactionFailed(message), cancellationToken).ConfigureAwait(false);
                    await ReleaseReservationAsync(message.TransactionId, cancellationToken).ConfigureAwait(false);
                    break;
                case Constants.BalanceUpdated:
                    await ApplyBalanceUpdatedAsync(HandleBalanceUpdated(message), cancellationToken).ConfigureAwait(false);
                    break;
                case Constants.TransactionCancelled:
                    await RecordCommittedOutcomeAsync(HandleTransactionCancelled(message), cancellationToken).ConfigureAwait(false);
                    await ReleaseReservationAsync(message.TransactionId, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    // (existing comment) -- unchanged
                    throw new InvalidOperationException(
                        $"Unsupported stored transaction-events type '{message.EventType}' for inbox message {message.Id}.");
            }

            await inboxRepository.MarkAsCompletedAsync(message, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The debtor's settlement: CoreBank's reported balance already includes
    /// this debit, so the reservation for it is released in the same step. A
    /// creditor's event, or one for a transaction PaymentsAPI never accepted,
    /// only records the balance.
    /// </summary>
    private async Task ApplyBalanceUpdatedAsync(BalanceUpdatedEvent payload, CancellationToken cancellationToken)
    {
        await accounts.SettleAsync(payload.AccountNumber, payload.NewBalance, payload.Currency, cancellationToken).ConfigureAwait(false);
        var released = 0m;
        var payment = await outboxRepository.FindByIdempotencyKeyAsync(payload.TransactionId, cancellationToken).ConfigureAwait(false);
        if (payment is not null && payment.FromAccount == payload.AccountNumber)
        {
            released = await ReleaseAsync(payment, cancellationToken).ConfigureAwait(false);
        }

        var activity = Activity.Current;
        activity?.SetTag("account.settled_balance", payload.NewBalance);
        activity?.SetTag("account.released", released);
    }

    private async Task ReleaseReservationAsync(string transactionId, CancellationToken cancellationToken)
    {
        // PaymentsAPI stores TransactionId = IdempotencyKey (ADR-027), so the
        // indexed dedupe key finds the payment.
        var payment = await outboxRepository.FindByIdempotencyKeyAsync(transactionId, cancellationToken).ConfigureAwait(false);
        if (payment is not null)
        {
            await ReleaseAsync(payment, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<decimal> ReleaseAsync(OutboxMessage payment, CancellationToken cancellationToken)
    {
        var shortfall = await accounts.ReleaseAsync(payment.FromAccount, payment.Amount, cancellationToken).ConfigureAwait(false);
        if (shortfall > 0m)
        {
            // The projection is younger than the payment (recreated database):
            // nothing to block the partition over, but say so.
            logger.LogWarning(
                "Reservation for payment {TransactionId} on account {FromAccount} was short by {Shortfall}; clamped at zero",
                payment.TransactionId,
                payment.FromAccount,
                shortfall);
        }

        logger.LogInformation(
            "Released {Amount} reserved for payment {TransactionId} on account {FromAccount}",
            payment.Amount - shortfall,
            payment.TransactionId,
            payment.FromAccount);
        return payment.Amount - shortfall;
    }
```

Change `HandleBalanceUpdated` to return its deserialized `BalanceUpdatedEvent` (it currently returns `void`): `private BalanceUpdatedEvent HandleBalanceUpdated(InboxMessage message) { … return payload; }`. Add `using CoreBankDemo.PaymentsAPI.Accounts;` and `using CoreBankDemo.PaymentsAPI.Inbox;`. Rewrite the class `<summary>`: the handler now owns business state (the account projection) and its own completion, inside one transaction; it still never writes the outbox row's transport `Status`.

- [ ] **Step 6: Run the unit tests**

Same command as Step 4. Expected: `Passed!`.

- [ ] **Step 7: Update the persistence `InboxProcessorTests` construction**

Two places in `tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/InboxProcessorTests.cs`. Add `using CoreBankDemo.PaymentsAPI.Accounts;`.

In `BuildHandlerServices` (around line 297), register the store next to the repositories so the DI-built handler resolves:

```csharp
        services.AddScoped<IOutboxRepository>(sp => sp.GetRequiredService<OutboxRepository>());
        services.AddScoped<IInboxMessageRepository>(sp => sp.GetRequiredService<InboxMessageRepository>());
        services.AddScoped<IAccountProjectionStore, AccountProjectionStore>();
        services.AddScoped<IInboxMessageHandler<InboxMessage>, TransactionEventHandler>();
```

In `StartAsync_restores_the_stored_trace_parent_before_dispatching_the_handler` (around line 116) the handler is built by hand inside the factory lambda whose parameter is `_`; give it the scope's own repositories:

```csharp
                _ => new TraceCapturingHandler(
                    new TransactionEventHandler(
                        NullLogger<TransactionEventHandler>.Instance,
                        new OutboxRepository(
                            _.GetRequiredService<PaymentsDbContext>(),
                            System.TimeProvider.System,
                            TestBusinessMetrics.Instance),
                        _.GetRequiredService<InboxMessageRepository>(),
                        new AccountProjectionStore(_.GetRequiredService<PaymentsDbContext>(), System.TimeProvider.System)),
                    observedActivity)));
```

- [ ] **Step 8: Write the failing atomicity test**

`tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/TransactionEventHandlerTests.cs`:

```csharp
using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.PaymentsAPI.Accounts;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.ServiceDefaults.CloudEventTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

/// <summary>
/// The transactional inbox on real PostgreSQL (spec: payments-account-projection):
/// the projection write and the inbox row's completion are one commit.
/// </summary>
public class TransactionEventHandlerTests(PostgresContainerFixture fixture) : PaymentsPostgresTestBase(fixture)
{
    private const string Debtor = "NL91ABNA0417164300";

    [Fact]
    public async Task HandleAsync_commits_the_debtor_settlement_the_release_and_the_completion_together()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var seed = CreateContext())
        {
            seed.OutboxMessages.Add(PaymentsApiTestData.Outbox("txn-s")); // Debtor, 12.34
            seed.ProjectedAccounts.Add(new ProjectedAccount
            {
                AccountNumber = Debtor, SettledBalance = 100m, Reserved = 12.34m, Currency = "EUR",
                UpdatedAt = TimeProvider.GetUtcNow().UtcDateTime
            });
            seed.InboxMessages.Add(BalanceUpdated("txn-s", -12.34m, 87.66m)); // claimed: Processing, as the kernel hands it over
            await seed.SaveChangesAsync(ct);
        }
        await using var context = CreateContext();
        var message = await context.InboxMessages.AsNoTracking().SingleAsync(ct);
        var handler = CreateHandler(context);

        await handler.HandleAsync(message, ct);

        await using var verification = CreateContext();
        var account = await verification.ProjectedAccounts.SingleAsync(ct);
        account.SettledBalance.Should().Be(87.66m);
        account.Reserved.Should().Be(0m);
        var row = await verification.InboxMessages.SingleAsync(ct);
        row.Status.Should().Be(MessageConstants.Status.Completed);
        row.ProcessedAt.Should().Be(TimeProvider.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task HandleAsync_rolls_back_projection_when_completion_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var seed = CreateContext())
        {
            seed.InboxMessages.Add(BalanceUpdated("txn-f", -1m, 99m));
            await seed.SaveChangesAsync(ct);
        }
        await using var context = CreateContext();
        var message = await context.InboxMessages.AsNoTracking().SingleAsync(ct);
        // Every dependency runs on the failing context, so the transaction is
        // opened on it, the projection's INSERT … ON CONFLICT executes inside
        // that transaction, and the first SaveChangesAsync (the store's) throws.
        await using var failing = new ThrowingSavePaymentsDbContext(context);
        var handler = new TransactionEventHandler(
            NullLogger<TransactionEventHandler>.Instance,
            new OutboxRepository(failing, TimeProvider, TestBusinessMetrics.Instance),
            new InboxMessageRepository(failing, TimeProvider, TestBusinessMetrics.Instance),
            new AccountProjectionStore(failing, TimeProvider));

        var act = () => handler.HandleAsync(message, ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom during save");
        await using var verification = CreateContext();
        (await verification.ProjectedAccounts.CountAsync(ct)).Should().Be(0, "the upsert ran inside the rolled-back transaction");
        (await verification.InboxMessages.SingleAsync(ct)).Status.Should().Be(MessageConstants.Status.Processing, "left as claimed for the kernel to retry");
    }

    private TransactionEventHandler CreateHandler(PaymentsDbContext context) => new(
        NullLogger<TransactionEventHandler>.Instance,
        new OutboxRepository(context, TimeProvider, TestBusinessMetrics.Instance),
        new InboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance),
        new AccountProjectionStore(context, TimeProvider));

    private InboxMessage BalanceUpdated(string transactionId, decimal delta, decimal newBalance) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = transactionId,
        TransactionId = transactionId,
        EventType = Constants.BalanceUpdated,
        AccountNumber = Debtor,
        Payload = JsonSerializer.Serialize(new BalanceUpdatedEvent(transactionId, Debtor, delta, newBalance, "EUR")),
        PartitionId = 0,
        Status = MessageConstants.Status.Processing,
        ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime
    };

    /// <summary>Shares the inner context's connection so it joins the same database, but every save throws.</summary>
    private sealed class ThrowingSavePaymentsDbContext(PaymentsDbContext inner)
        : PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(inner.Database.GetDbConnection())
            .Options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom during save");
    }
}
```

Note on the second test: the `AccountProjectionStore`'s `SaveChangesAsync` is what throws, after the `INSERT … ON CONFLICT` upsert has already executed inside the transaction the failing context's `InboxMessageRepository` opened. The assertion that no `ProjectedAccounts` row exists is what proves the rollback. `ThrowingSavePaymentsDbContext` needs a `public` constructor that `PaymentsDbContext(DbContextOptions<PaymentsDbContext>)` accepts; the primary-constructor form shown compiles because `PaymentsDbContext`'s constructor is public.

- [ ] **Step 9: Run tier-2 tests for PaymentsApi**

Run: `dotnet test tests/CoreBankDemo.Persistence.IntegrationTests --filter "FullyQualifiedName~PaymentsApi"`
Expected: `Passed!`, including `InboxProcessorTests.StartAsync_claims_dispatches_and_completes_a_valid_event_without_mutating_business_state` (a `transaction.completed` event has no projection effect and ends `Completed`).

- [ ] **Step 10: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Inbox/InboxMessageRepository.cs CoreBankDemo.PaymentsAPI/Handlers/TransactionEventHandler.cs tests/CoreBankDemo.PaymentsAPI.Tests/TransactionEventHandlerTests.cs tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/TransactionEventHandlerTests.cs tests/CoreBankDemo.Persistence.IntegrationTests/PaymentsApi/InboxProcessorTests.cs
git commit -m "feat(payments): transaction events update the account projection in the inbox transaction

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Wiring, demo request, full gate

**Files:**
- Modify: `CoreBankDemo.PaymentsAPI/PaymentStorageServiceCollectionExtensions.cs:34-38`
- Modify: `demo-requests.http`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStorageRegistrationTests.cs`

**Interfaces:**
- Consumes: `IAccountProjectionStore`/`AccountProjectionStore` (Task 3).

- [ ] **Step 1: Write the failing registration test**

`PaymentStorageRegistrationTests.cs` already has a `BuildProvider(Dictionary<string, string?>)` helper (used by `Valid_configuration_registers_storage_services`) that registers `AddPaymentStorage` plus a never-connecting `PaymentsDbContext`. Add `using CoreBankDemo.PaymentsAPI.Accounts;` and this test:

```csharp
    [Fact]
    public void AddPaymentStorage_registers_the_account_projection_store_as_scoped()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["OutboxProcessing:PartitionCount"] = "4",
            ["OutboxProcessing:LockExpirySeconds"] = "30",
            ["OutboxProcessing:PollingIntervalMs"] = "200"
        });

        using var scope = provider.CreateScope();
        var first = scope.ServiceProvider.GetRequiredService<IAccountProjectionStore>();
        var second = scope.ServiceProvider.GetRequiredService<IAccountProjectionStore>();

        first.Should().BeOfType<AccountProjectionStore>().And.BeSameAs(second);
    }
```

- [ ] **Step 2: Run to see it fail**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~PaymentStorageRegistrationTests"`
Expected: `No service for type 'IAccountProjectionStore'`.

- [ ] **Step 3: Register it**

In `AddPaymentStorage`, after `services.AddScoped<IPaymentStatusHandler, PaymentStatusHandler>();`:

```csharp
        // Spec: payments-account-projection. Scoped like the repositories so
        // the inbox handler's projection writes share the scope's DbContext
        // and therefore its transaction.
        services.AddScoped<IAccountProjectionStore, AccountProjectionStore>();
```

with `using CoreBankDemo.PaymentsAPI.Accounts;`.

- [ ] **Step 4: Run the registration and wiring tests**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~Registration|FullyQualifiedName~Wiring"`
Expected: `Passed!` (the `TransactionEventProcessorWiringTests` resolve the handler through the real container).

- [ ] **Step 5: Add the demo request**

In `demo-requests.http`, after request `2b`'s block (before `### 2c.`), add:

```http
### 2e. Refused at the door: the local account projection (ADR-028)
### PaymentsAPI only refuses what it *knows* is short: after at least one
### settlement it has seen NL39RABO0300065264's balance (2500.00 at seed).
### First move money out of it so it has a settled balance locally, then ask
### for more than is left -> 422 Unprocessable Content, body
### { "errors": ["Insufficient funds"] }, nothing stored, no CoreBank call.
### An account PaymentsAPI has never seen a balance.updated for is always
### accepted and left to CoreBank to judge.
POST http://localhost:5294/api/payments
Content-Type: application/json

{
  "fromAccount": "NL39RABO0300065264",
  "toAccount": "NL20INGB0001234567",
  "amount": 2400.00,
  "currency": "EUR"
}

### then (give the balance.updated event a moment to arrive)
POST http://localhost:5294/api/payments
Content-Type: application/json

{
  "fromAccount": "NL39RABO0300065264",
  "toAccount": "NL20INGB0001234567",
  "amount": 500.00,
  "currency": "EUR"
}
```

- [ ] **Step 6: Run the whole gate**

```bash
dotnet tool restore
dotnet build CoreBankDemo.Rebuild.slnf
export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1
dotnet test CoreBankDemo.UnitTests.slnf
dotnet test CoreBankDemo.IntegrationTests.slnf
```

Expected: every project `Passed!` and the coverlet gate green for each. If the PaymentsAPI unit project's coverage dips below 90 %, the missing lines are almost certainly in `TransactionEventHandler`'s release/tag paths — add a unit case rather than an exclusion.

- [ ] **Step 7: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/PaymentStorageServiceCollectionExtensions.cs tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStorageRegistrationTests.cs demo-requests.http
git commit -m "feat(payments): wire the account projection store; demo request for the 422

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: ADR-028 and the document amendments

**Files:**
- Create: `docs/adr/ADR-028-local-account-projection-door-check.md`
- Modify: `docs/adr/ADR-023-corebank-sole-outcome-source.md:1-6`
- Modify: `docs/constraints.md:16` (§2 PaymentsAPI bullet)
- Modify: `.claude/skills/messaging-patterns/SKILL.md`
- Modify: `ARCHITECTURE.md` (Payments API schema section, after the `InboxMessages` table)
- Modify: `docs/superpowers/specs/2026-10-08-payments-account-projection-design.md` (status; Code Map)

- [ ] **Step 1: Write ADR-028**

```markdown
# ADR-028: PaymentsAPI keeps a local account projection and may refuse a debit at the door

**Date:** 2026-10-08
**Status:** Accepted
**Deciders:** Architecture team
**Amends:** ADR-023 (decision 1, "No pre-validation")

Implementation spec: [`2026-10-08-payments-account-projection-design.md`](../superpowers/specs/2026-10-08-payments-account-projection-design.md).

## Context

ADR-023 removed PaymentsAPI's call to `POST /api/accounts/validate` and wrote "no pre-validation".
That call failed in exactly the conditions the demo injects, left payments without an outcome, and
checked nothing CoreBank would not check again. The rule was about that remote call.

PaymentsAPI still accepted every well-formed debit, including one from an account it had just watched
run dry through `balance.updated`, and learned of the rejection only through `transaction.failed`.
It also lacked a concrete transactional-outbox example (its accept path wrote only the outbox row) and
a transactional-inbox example (its event handler only logged and cached an outcome).

## Decision

1. **PaymentsAPI keeps a `ProjectedAccounts` table** built only from the payments it accepts and the
   `transaction-events` it consumes: the last `NewBalance` CoreBank reported (`SettledBalance`,
   `NULL` until the first event) and the debits accepted but not yet settled (`Reserved`). It never
   calls CoreBank for it and never seeds it.
2. **Accepting a payment inserts the outbox row and raises the debtor's reservation in one database
   transaction** (`OutboxRepository.AcceptAsync`). The insert runs first, so dedupe (AD-4) is decided
   before any balance is read. The debtor row is locked `FOR UPDATE`, so concurrent debits serialise.
3. **Handling an event applies it to the projection and completes the inbox row in one transaction**
   (`TransactionEventHandler`, via the kernel's `ExecuteInTransactionAsync` and `MarkAsCompletedAsync`,
   as CoreBank's `TransactionExecutionHandler` already does). `balance.updated` sets `SettledBalance`
   and, for the debtor, releases the reservation; `transaction.failed`/`.cancelled` release it;
   `transaction.completed` has no effect. Releases clamp at zero.
4. **PaymentsAPI refuses a debit at the door, `422 Unprocessable Content`, only when the debtor's
   settled balance is known and `SettledBalance − Reserved < Amount`.** Nothing is stored, the key is
   not consumed, both rails answer the same. The body says `Insufficient funds` and nothing more.
5. **ADR-023's rule is restated:** *no remote pre-validation; CoreBank alone decides the outcome of
   every accepted payment.* A refused payment was never accepted, so there is no outcome to announce
   and no second source of outcomes.

## Consequences

- A payment certain to be rejected no longer travels to CoreBank and back.
- The check is best effort: the projection lags CoreBank by the event pipeline, and because the
  payments inbox partitions `balance.updated` by transaction id (ADR-026 §4), two settlements for one
  account can be applied in either order, leaving `SettledBalance` briefly stale until the next event.
  CoreBank still executes every accepted payment against the real ledger.
- A reservation whose payment never settles (a row stuck on infrastructure failure, ADR-023) stays
  reserved. That is correct: the funds are committed until CoreBank says otherwise.
- `EnsureCreated` adds the table to fresh databases; an existing development database must be
  recreated. `LoadTestDatabaseResetter` truncates it, or reservations would accumulate across runs.
- `BusinessMetrics.PaymentOutcome` gains `InsufficientFunds`; the refusal is tagged on the request
  span like CoreBank's rejections (`payment.outcome = rejected`, `payment.failure_reason =
  insufficient_funds`).
- The acceptance harness is unchanged: load-test accounts hold €10 000 000, so no `422` occurs.

## Alternatives considered

- **Accept and only flag a predicted shortfall.** No contract change, but nothing a caller can see.
- **Refuse only on the instant rail.** Closer to SCT vs SCT Inst, two behaviours to explain.
- **A running total from `Delta`.** Order-independent, but PaymentsAPI never learns the opening balance.
- **Replicate CoreBank's `Account` table.** More to keep in sync, nothing more to demonstrate.
```

- [ ] **Step 2: Amend ADR-023**

After the `**Deciders:**` line in `ADR-023-corebank-sole-outcome-source.md` add:

```markdown
**Amended by:** ADR-028 (decision 1 means *no remote pre-validation*; PaymentsAPI may refuse a debit at the door from its local account projection, and such a payment was never accepted)
```

- [ ] **Step 3: Amend constraints §2**

Replace the `POST /api/payments` bullet in `docs/constraints.md` with:

```markdown
  - `POST /api/payments` → validate → store in Outbox and raise the debtor's reservation in one transaction (idempotent on `Idempotency-Key` header, GUID generated if absent) → `202 Accepted`; duplicate key → `202` referencing the existing record; a debit the local account projection knows is unfunded → `422` with `Insufficient funds`, nothing stored, key not consumed (ADR-028).
```

- [ ] **Step 4: Amend the `messaging-patterns` skill**

After the "Partition assignment" section in `.claude/skills/messaging-patterns/SKILL.md`, add:

```markdown
## Atomic handlers: business state and the message row in one transaction

A handler that owns business state commits it **with** the message-store row, through the kernel's own helpers, never with a second connection:

```csharp
// Inbox: apply the event, then complete the row yourself inside the transaction.
// The kernel's MarkAsCompletedAsync afterwards sees AlreadyTerminal and does nothing.
await repository.ExecuteInTransactionAsync(async () =>
{
    await accounts.SettleAsync(...);              // business state
    await repository.MarkAsCompletedAsync(message, ct);
}, ct);

// Outbox: insert the row first (AD-4 dedupe), then the business state, one commit.
await ExecuteInTransactionAsync(async () =>
{
    OutboxMessages.Add(message); await DbContext.SaveChangesAsync(ct);   // unique violation => rollback => Duplicate
    var account = await ProjectedAccountRows.LockAsync(DbContext, message.FromAccount, now, ct);
    account.Reserved += message.Amount; await DbContext.SaveChangesAsync(ct);
}, ct);
```

Reference implementations: `CoreBankAPI/Inbox/TransactionExecutionHandler.cs` (inbox, story 4.6), `PaymentsAPI/Handlers/TransactionEventHandler.cs` (inbox, ADR-028), `PaymentsAPI/Outbox/OutboxRepository.AcceptAsync` (outbox + state, ADR-028). Any exception rolls everything back and the kernel records a retry (ADR-023); never catch inside the delegate to "save what you can".
```

Also add `CoreBankDemo.PaymentsAPI/Accounts/ProjectedAccountRows.cs` | `Account projection row upsert + FOR UPDATE (ADR-028)` to the "Key files" table.

- [ ] **Step 5: Amend `ARCHITECTURE.md`**

In "Database Schemas → Payments API (paymentsdb)", after the `InboxMessages` table block, add:

```markdown
**ProjectedAccounts Table** (ADR-028 — PaymentsAPI's local view of the accounts it has seen):
```sql
- AccountNumber (string, PK)
- SettledBalance (decimal, nullable — NULL until the first balance.updated)
- Reserved (decimal — accepted debits not yet settled)
- Currency (string, nullable)
- UpdatedAt (datetime)
```
```

and in the ADR list at the end of the file add `ADR-028`.

- [ ] **Step 6: Update the spec**

In the spec, set `> **Status:** Implemented` and in the Code Map replace the `Handlers/TransactionEventHandler.cs` bullet's dependency sentence with: "gains `IInboxMessageRepository` (for `ExecuteInTransactionAsync` and the kernel's `MarkAsCompletedAsync`, which attaches the row and stamps completion itself) and `IAccountProjectionStore`; no direct `PaymentsDbContext` dependency, so it stays unit-testable through mocks."

- [ ] **Step 7: Check links and commit**

```bash
grep -n "ADR-028" docs/adr/ADR-023-corebank-sole-outcome-source.md docs/constraints.md ARCHITECTURE.md .claude/skills/messaging-patterns/SKILL.md
git add docs/adr/ADR-028-local-account-projection-door-check.md docs/adr/ADR-023-corebank-sole-outcome-source.md docs/constraints.md .claude/skills/messaging-patterns/SKILL.md ARCHITECTURE.md docs/superpowers/specs/2026-10-08-payments-account-projection-design.md
git commit -m "docs: ADR-028 local account projection; amend ADR-023, constraints, skill, architecture

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Self-review notes

- **Spec coverage.** Entity/table → Task 2; accept path (insert-first, lock, check, reserve, rollback semantics, key not consumed) → Task 4; handler outcome, `422`, no amounts in the body, both rails → Tasks 5–6; inbox transaction, per-event effects, clamp with warning, no outbox `Status` write → Task 7; observability (metric value, span tags, `account.settled_balance`) → Tasks 1, 5, 7 (`account.reserved` from the spec is recorded as `account.released`, the value the handler actually knows without a second read — Task 9 Step 6 notes this in the spec); load-test reset → Task 2; wiring and demo request → Task 8; ADR-028 and the amendments → Task 9. Ordering is documented, not changed (spec: Ask First).
- **Type consistency.** `PaymentAcceptance` (Task 4) is what Task 5 switches on; `IAccountProjectionStore.SettleAsync/ReleaseAsync` (Task 3) are what Task 7 mocks and calls; `ProjectedAccountRows.LockAsync(PaymentsDbContext, string, DateTime, CancellationToken)` is used identically in Tasks 3 and 4; the handler constructor order `(logger, outboxRepository, inboxRepository, accounts)` is the same in Task 7's unit helper, persistence tests and `InboxProcessorTests`.
- **Deviation from the spec, deliberate.** The spec's Code Map gave the handler a `PaymentsDbContext` to attach the message; the plan uses the kernel's `MarkAsCompletedAsync` through `IInboxMessageRepository` instead, which attaches and stamps completion itself and keeps the handler free of a DbContext (unit tier stays Docker-free). Task 9 Step 6 records this in the spec.
