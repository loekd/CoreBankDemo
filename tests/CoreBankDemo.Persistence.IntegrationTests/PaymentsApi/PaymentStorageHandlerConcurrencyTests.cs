using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

/// <summary>
/// <see cref="PaymentStorageHandler"/>'s duplicate-key contract proved against
/// a real unique index (ADR-016): two handlers on genuinely independent
/// connections race for the same caller-supplied idempotency key. The rest of
/// the handler's behavior (validation, partitioning, logging) is pure logic and
/// stays in <c>CoreBankDemo.PaymentsAPI.Tests</c> behind a mocked repository.
/// </summary>
public class PaymentStorageHandlerConcurrencyTests(PostgresContainerFixture fixture)
    : PaymentsPostgresTestBase(fixture)
{
    private static readonly PaymentRequest Request =
        new("NL91ABNA0417164300", "NL20INGB0001234567", 12.34m, "EUR");

    [Fact]
    public async Task Concurrent_handlers_return_the_same_persisted_winner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = CreateStore();
        var contexts = store.CreateCompetingContexts();
        await using var firstContext = contexts.First;
        await using var secondContext = contexts.Second;
        var first = CreateHandler(new OutboxRepository(firstContext, System.TimeProvider.System));
        var second = CreateHandler(new OutboxRepository(secondContext, System.TimeProvider.System));

        var results = await PaymentsApiTestData.RaceAsync(
            () => first.StoreAsync(Request, "handler-race", ct),
            () => second.StoreAsync(Request, "handler-race", ct));

        results.Select(result => result.Payment!.Id).Distinct().Should().ContainSingle();
        results.Select(result => result.Outcome)
            .Should().BeEquivalentTo([PaymentStorageOutcome.Stored, PaymentStorageOutcome.Duplicate]);
        await using var verification = CreateContext();
        verification.OutboxMessages.Count(message => message.IdempotencyKey == "handler-race").Should().Be(1);
    }

    private static PaymentStorageHandler CreateHandler(IOutboxRepository repository) =>
        new(
            repository,
            Options.Create(new OutboxProcessingOptions
            {
                PartitionCount = 4,
                LockExpirySeconds = 30,
                PollingIntervalMs = 200
            }),
            new FixedTimeProvider(),
            NullLogger<PaymentStorageHandler>.Instance);
}
