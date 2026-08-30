using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Messaging;

/// <summary>
/// <c>StoreIfNewAsync</c> on <see cref="InboxMessageRepositoryBase{TMessage,TDbContext}"/>
/// / <see cref="OutboxMessageRepositoryBase{TMessage,TDbContext}"/> (story 2.2):
/// the full I/O matrix — first store, sequential duplicate, concurrent
/// duplicate, distinct composite identities, and non-unique-violation
/// propagation — plus the tracker-cleanup acceptance criterion, all against
/// real PostgreSQL (ADR-016 tier 2).
/// </summary>
public class StoreIfNewAsyncTests(PostgresContainerFixture fixture) : MessagingPostgresTestBase(fixture)
{
    [Fact]
    public async Task First_store_inserts_row_and_returns_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var message = new TestInboxMessage { IdempotencyKey = "first-store-key" };

        var stored = await repository.StoreIfNewAsync(message, ct);

        stored.Should().BeTrue();
        (await context.InboxMessages.CountAsync(m => m.IdempotencyKey == "first-store-key", ct)).Should().Be(1);
    }

    [Fact]
    public async Task First_store_records_an_added_store_operation_metric()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var repository = new TestInboxMessageRepository(context, TimeProvider, businessMetrics);

        await repository.StoreIfNewAsync(new TestInboxMessage { IdempotencyKey = "metric-store-key" }, ct);

        var measurement = listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == "corebankdemo.messaging.store.operations").Which;
        measurement.Tags["messaging.store.kind"].Should().Be("inbox");
        measurement.Tags["outcome"].Should().Be("added");
    }

    [Fact]
    public async Task Sequential_duplicate_records_a_duplicate_store_operation_metric()
    {
        var ct = TestContext.Current.CancellationToken;
        const string key = "metric-dup-key";
        await using (var firstContext = CreateContext())
        {
            var firstRepository = new TestInboxMessageRepository(firstContext, TimeProvider, new BusinessMetrics());
            (await firstRepository.StoreIfNewAsync(new TestInboxMessage { IdempotencyKey = key }, ct)).Should().BeTrue();
        }

        await using var secondContext = CreateContext();
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var secondRepository = new TestInboxMessageRepository(secondContext, TimeProvider, businessMetrics);

        await secondRepository.StoreIfNewAsync(new TestInboxMessage { IdempotencyKey = key }, ct);

        var measurement = listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == "corebankdemo.messaging.store.operations").Which;
        measurement.Tags["outcome"].Should().Be("duplicate");
    }

    [Fact]
    public async Task Non_unique_violation_failure_records_a_failed_store_operation_metric_before_rethrow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var repository = new TestInboxMessageRepository(context, TimeProvider, businessMetrics);
        var invalidMessage = new TestInboxMessage { IdempotencyKey = "metric-failed-key", RetryCount = -1 };

        var act = async () => await repository.StoreIfNewAsync(invalidMessage, ct);

        // Recorded before the rethrow (metric contract), and the original
        // exception/state-transition behavior — a real check-constraint
        // `DbUpdateException` that is not a unique violation — is unchanged.
        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        UniqueViolation.IsUniqueViolation(thrown.Which).Should().BeFalse();
        var measurement = listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == "corebankdemo.messaging.store.operations").Which;
        measurement.Tags["messaging.store.kind"].Should().Be("inbox");
        measurement.Tags["outcome"].Should().Be("failed");
    }

    [Fact]
    public async Task Cancellation_during_save_records_no_store_operation_metric_and_rethrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var repository = new TestInboxMessageRepository(context, TimeProvider, businessMetrics);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await repository.StoreIfNewAsync(
            new TestInboxMessage { IdempotencyKey = "metric-cancelled-key" }, cancelled.Token);

        // Cancellation is not a store failure (story 6.5 boundaries): the
        // original `OperationCanceledException` still propagates, but it is
        // never counted as `failed`, `added`, or `duplicate`.
        await act.Should().ThrowAsync<OperationCanceledException>();
        listener.Measurements.Should().NotContain(m => m.InstrumentName == "corebankdemo.messaging.store.operations");
    }

    [Fact]
    public async Task Sequential_duplicate_reports_already_exists_without_throwing_and_leaves_one_row()
    {
        var ct = TestContext.Current.CancellationToken;
        const string key = "sequential-dup-key";

        await using (var firstContext = CreateContext())
        {
            var firstRepository = new TestInboxMessageRepository(firstContext, TimeProvider, TestBusinessMetrics.Instance);
            (await firstRepository.StoreIfNewAsync(new TestInboxMessage { IdempotencyKey = key }, ct)).Should().BeTrue();
        }

        await using var secondContext = CreateContext();
        var secondRepository = new TestInboxMessageRepository(secondContext, TimeProvider, TestBusinessMetrics.Instance);
        var act = async () => await secondRepository.StoreIfNewAsync(new TestInboxMessage { IdempotencyKey = key }, ct);

        var stored = await act.Should().NotThrowAsync();
        stored.Which.Should().BeFalse();
        (await secondContext.InboxMessages.CountAsync(m => m.IdempotencyKey == key, ct)).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_duplicate_stores_leave_exactly_one_row_and_no_exception_escapes()
    {
        var ct = TestContext.Current.CancellationToken;
        const string key = "concurrent-dup-key";

        async Task<bool> StoreAsync()
        {
            await using var context = CreateContext();
            var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
            return await repository.StoreIfNewAsync(new TestInboxMessage { IdempotencyKey = key }, ct);
        }

        var act = async () => await Task.WhenAll(StoreAsync(), StoreAsync());
        var results = await act.Should().NotThrowAsync();

        results.Which.Should().ContainSingle(stored => stored).And.ContainSingle(stored => !stored);

        await using var verifyContext = CreateContext();
        (await verifyContext.InboxMessages.CountAsync(m => m.IdempotencyKey == key, ct)).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_duplicate_event_identity_leaves_exactly_one_row_and_no_exception_escapes()
    {
        var ct = TestContext.Current.CancellationToken;
        const string key = "concurrent-dup-event-key";
        const string eventType = "Debited";

        async Task<bool> StoreAsync()
        {
            await using var context = CreateContext();
            var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
            return await repository.StoreIfNewAsync(
                new TestOutboxEventMessage { IdempotencyKey = key, EventType = eventType }, ct);
        }

        var act = async () => await Task.WhenAll(StoreAsync(), StoreAsync());
        var results = await act.Should().NotThrowAsync();

        results.Which.Should().ContainSingle(stored => stored).And.ContainSingle(stored => !stored);

        await using var verifyContext = CreateContext();
        (await verifyContext.OutboxEventMessages.CountAsync(
            m => m.IdempotencyKey == key && m.EventType == eventType, ct)).Should().Be(1);
    }

    [Fact]
    public async Task Distinct_event_identities_sharing_a_key_both_store_under_composite_dedupe()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        const string key = "txn-123";

        var firstStored = await repository.StoreIfNewAsync(
            new TestOutboxEventMessage { IdempotencyKey = key, EventType = "Debited" }, ct);
        var secondStored = await repository.StoreIfNewAsync(
            new TestOutboxEventMessage { IdempotencyKey = key, EventType = "Credited" }, ct);

        firstStored.Should().BeTrue();
        secondStored.Should().BeTrue();
        (await context.OutboxEventMessages.CountAsync(m => m.IdempotencyKey == key, ct)).Should().Be(2);
    }

    [Fact]
    public async Task Duplicate_event_identity_same_key_and_event_type_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestOutboxEventMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        const string key = "txn-456";

        var firstStored = await repository.StoreIfNewAsync(
            new TestOutboxEventMessage { IdempotencyKey = key, EventType = "Debited" }, ct);
        var secondStored = await repository.StoreIfNewAsync(
            new TestOutboxEventMessage { IdempotencyKey = key, EventType = "Debited" }, ct);

        firstStored.Should().BeTrue();
        secondStored.Should().BeFalse();
        (await context.OutboxEventMessages.CountAsync(m => m.IdempotencyKey == key, ct)).Should().Be(1);
    }

    [Fact]
    public async Task Non_unique_violation_failure_propagates_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var invalidMessage = new TestInboxMessage { IdempotencyKey = "invalid-retry-count", RetryCount = -1 };

        var act = async () => await repository.StoreIfNewAsync(invalidMessage, ct);

        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        UniqueViolation.IsUniqueViolation(thrown.Which).Should().BeFalse();
    }

    [Fact]
    public async Task Dbcontext_is_usable_for_further_operations_after_a_non_unique_violation_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        var invalidMessage = new TestInboxMessage { IdempotencyKey = "invalid-then-valid", RetryCount = -1 };

        var act = async () => await repository.StoreIfNewAsync(invalidMessage, ct);
        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        UniqueViolation.IsUniqueViolation(thrown.Which).Should().BeFalse();

        // The failed entity must have been detached — otherwise a further
        // operation on this context would fail outright (SaveChanges still
        // trying to insert the same invalid tracked row).
        context.Entry(invalidMessage).State.Should().Be(EntityState.Detached);

        var validMessage = new TestInboxMessage { IdempotencyKey = "invalid-then-valid-recovery" };
        var recoveryAct = async () => await repository.StoreIfNewAsync(validMessage, ct);

        var stored = await recoveryAct.Should().NotThrowAsync();
        stored.Which.Should().BeTrue();
        (await context.InboxMessages.CountAsync(m => m.IdempotencyKey == "invalid-then-valid-recovery", ct)).Should().Be(1);
    }

    [Fact]
    public async Task Losers_dbcontext_is_usable_for_further_operations_after_a_violation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider, TestBusinessMetrics.Instance);
        const string key = "tracker-cleanup-key";

        (await repository.StoreIfNewAsync(new TestInboxMessage { IdempotencyKey = key }, ct)).Should().BeTrue();

        // Losing call on the SAME context/tracker as the winner above.
        var loserMessage = new TestInboxMessage { IdempotencyKey = key };
        (await repository.StoreIfNewAsync(loserMessage, ct)).Should().BeFalse();

        // The failed entity must have been detached — otherwise a further
        // operation on this context would fail (e.g. re-throwing the stale
        // tracked entity's conflict, or SaveChanges failing outright).
        context.Entry(loserMessage).State.Should().Be(EntityState.Detached);

        var furtherMessage = new TestInboxMessage { IdempotencyKey = "tracker-cleanup-key-2" };
        var act = async () => await repository.StoreIfNewAsync(furtherMessage, ct);

        var stored = await act.Should().NotThrowAsync();
        stored.Which.Should().BeTrue();
        context.Entry(furtherMessage).State.Should().Be(EntityState.Unchanged);
    }
}
