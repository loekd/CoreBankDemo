using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Outbox;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// Exercises <see cref="HttpForwardOutboxDeliveryStrategy"/> against a fake
/// <see cref="ICoreBankApiClient"/> (spec-5-4's code map) -- no HTTP, no
/// Kiota. Covers every row of the spec's I/O &amp; Edge-Case Matrix: valid
/// destination + successful submission completes; a duplicate-accept replay
/// completes identically; an invalid destination account, a non-2xx
/// submission, and a timeout/transport exception from either call all throw
/// (so <c>OutboxProcessorBase&lt;TMessage&gt;</c>'s kernel retry path takes
/// over); caller cancellation from either call propagates unchanged.
/// </summary>
public class HttpForwardOutboxDeliveryStrategyTests
{
    private const string ToAccount = "NL20INGB0001234567";

    private static OutboxMessage Message() => PaymentsApiTestData.Outbox("forward-key");

    [Fact]
    public async Task DeliverAsync_completes_when_account_is_valid_and_submission_succeeds()
    {
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Success(
                new AccountValidation(ToAccount, true, "Jane Doe", 1000m)),
            SubmitResult = CoreBankResult<TransactionSubmission>.Success(
                new TransactionSubmission("forward-key", "Pending", DateTimeOffset.UtcNow))
        };
        var strategy = new HttpForwardOutboxDeliveryStrategy(client);

        var act = () => strategy.DeliverAsync(Message(), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        client.ValidateCalls.Should().Equal(ToAccount);
        client.SubmitCalls.Should().ContainSingle();
        client.SubmitCalls[0].FromAccount.Should().Be("NL91ABNA0417164300");
        client.SubmitCalls[0].ToAccount.Should().Be(ToAccount);
        client.SubmitCalls[0].TransactionId.Should().Be("forward-key");
    }

    [Fact]
    public async Task DeliverAsync_completes_for_a_duplicate_accept_replay()
    {
        // A 200 cached-replay submission is already classified as Success by
        // KiotaCoreBankApiClient (story 5.3) -- this strategy must treat it
        // identically to a fresh 202 acceptance (spec's edge-case matrix).
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Success(
                new AccountValidation(ToAccount, true, null, null)),
            SubmitResult = CoreBankResult<TransactionSubmission>.Success(
                new TransactionSubmission("forward-key", "Completed", DateTimeOffset.UtcNow))
        };
        var strategy = new HttpForwardOutboxDeliveryStrategy(client);

        var act = () => strategy.DeliverAsync(Message(), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeliverAsync_throws_and_never_submits_when_destination_account_is_invalid()
    {
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Success(
                new AccountValidation(ToAccount, false, null, null))
        };
        var strategy = new HttpForwardOutboxDeliveryStrategy(client);

        var act = () => strategy.DeliverAsync(Message(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        client.SubmitCalls.Should().BeEmpty();
    }

    // Theory data is expressed as the enum's name rather than the enum value
    // itself: CoreBankRetryReason is internal, and a public [Theory] method's
    // parameters must be at least as accessible as the method (CS0051).
    [Theory]
    [InlineData(nameof(CoreBankRetryReason.TransportRejection), 400)]
    [InlineData(nameof(CoreBankRetryReason.MalformedResponse), null)]
    [InlineData(nameof(CoreBankRetryReason.Timeout), null)]
    [InlineData(nameof(CoreBankRetryReason.TransportException), null)]
    public async Task DeliverAsync_throws_and_never_submits_when_validation_is_a_retry_outcome(
        string reasonName, int? statusCode)
    {
        var reason = Enum.Parse<CoreBankRetryReason>(reasonName);
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Retry(reason, statusCode)
        };
        var strategy = new HttpForwardOutboxDeliveryStrategy(client);

        var act = () => strategy.DeliverAsync(Message(), TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Contain(reason.ToString());
        client.SubmitCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(CoreBankRetryReason.TransportRejection), 503)]
    [InlineData(nameof(CoreBankRetryReason.MalformedResponse), null)]
    [InlineData(nameof(CoreBankRetryReason.Timeout), null)]
    [InlineData(nameof(CoreBankRetryReason.TransportException), null)]
    public async Task DeliverAsync_throws_with_status_preserved_when_submission_is_a_retry_outcome(
        string reasonName, int? statusCode)
    {
        var reason = Enum.Parse<CoreBankRetryReason>(reasonName);
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Success(
                new AccountValidation(ToAccount, true, null, null)),
            SubmitResult = CoreBankResult<TransactionSubmission>.Retry(reason, statusCode)
        };
        var strategy = new HttpForwardOutboxDeliveryStrategy(client);

        var act = () => strategy.DeliverAsync(Message(), TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Contain(reason.ToString());
        if (statusCode is int code)
        {
            assertion.Which.Message.Should().Contain(code.ToString());
        }
    }

    [Fact]
    public async Task DeliverAsync_propagates_caller_cancellation_from_account_validation_unchanged()
    {
        var client = new FakeCoreBankApiClient { ValidateThrows = new OperationCanceledException() };
        var strategy = new HttpForwardOutboxDeliveryStrategy(client);

        var act = () => strategy.DeliverAsync(Message(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DeliverAsync_propagates_caller_cancellation_from_transaction_submission_unchanged()
    {
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Success(
                new AccountValidation(ToAccount, true, null, null)),
            SubmitThrows = new OperationCanceledException()
        };
        var strategy = new HttpForwardOutboxDeliveryStrategy(client);

        var act = () => strategy.DeliverAsync(Message(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FakeCoreBankApiClient : ICoreBankApiClient
    {
        public CoreBankResult<AccountValidation>? ValidateResult { get; set; }

        public Exception? ValidateThrows { get; set; }

        public CoreBankResult<TransactionSubmission>? SubmitResult { get; set; }

        public Exception? SubmitThrows { get; set; }

        public List<string> ValidateCalls { get; } = new();

        public List<TransactionSubmissionRequest> SubmitCalls { get; } = new();

        public Task<CoreBankResult<AccountValidation>> ValidateAccountAsync(
            string accountNumber, CancellationToken cancellationToken)
        {
            ValidateCalls.Add(accountNumber);
            return ValidateThrows is not null
                ? Task.FromException<CoreBankResult<AccountValidation>>(ValidateThrows)
                : Task.FromResult(ValidateResult!);
        }

        public Task<CoreBankResult<AccountDetails>> GetAccountDetailsAsync(
            string accountNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the forwarding strategy.");

        public Task<CoreBankResult<TransactionSubmission>> ProcessTransactionAsync(
            TransactionSubmissionRequest request, CancellationToken cancellationToken)
        {
            SubmitCalls.Add(request);
            return SubmitThrows is not null
                ? Task.FromException<CoreBankResult<TransactionSubmission>>(SubmitThrows)
                : Task.FromResult(SubmitResult!);
        }

        public Task<CoreBankResult<TransactionStatus>> GetTransactionStatusAsync(
            string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the forwarding strategy.");
    }
}
