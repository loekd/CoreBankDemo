using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults;
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
    private static readonly BusinessMetrics BusinessMetrics = new();

    private static OutboxMessage Message() => PaymentsApiTestData.Outbox("forward-key");

    [Fact]
    public async Task DeliverAsync_completes_when_account_is_valid_and_submission_succeeds()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Success(
                new AccountValidation(ToAccount, true, "Jane Doe", 1000m)),
            SubmitResult = CoreBankResult<TransactionSubmission>.Success(
                new TransactionSubmission("forward-key", "Pending", DateTimeOffset.UtcNow))
        };
        var strategy = new HttpForwardOutboxDeliveryStrategy(client, BusinessMetrics);
        var message = Message();

        var act = () => strategy.DeliverAsync(message, cancellation.Token);

        await act.Should().NotThrowAsync();
        client.ValidateCalls.Should().Equal(ToAccount);
        client.ValidateCancellationTokens.Should().Equal(cancellation.Token);
        client.SubmitCalls.Should().ContainSingle();
        client.SubmitCancellationTokens.Should().Equal(cancellation.Token);
        client.SubmitCalls[0].FromAccount.Should().Be("NL91ABNA0417164300");
        client.SubmitCalls[0].ToAccount.Should().Be(ToAccount);
        client.SubmitCalls[0].Amount.Should().Be(message.Amount);
        client.SubmitCalls[0].Currency.Should().Be(message.Currency);
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
        var strategy = new HttpForwardOutboxDeliveryStrategy(client, BusinessMetrics);

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
        var strategy = new HttpForwardOutboxDeliveryStrategy(client, BusinessMetrics);

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
        var strategy = new HttpForwardOutboxDeliveryStrategy(client, BusinessMetrics);

        var act = () => strategy.DeliverAsync(Message(), TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Contain(reason.ToString());
        if (statusCode is int code)
        {
            assertion.Which.Message.Should().Contain(code.ToString());
        }

        client.ValidateCalls.Should().Equal(ToAccount);
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
        var strategy = new HttpForwardOutboxDeliveryStrategy(client, BusinessMetrics);

        var act = () => strategy.DeliverAsync(Message(), TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Contain(reason.ToString());
        if (statusCode is int code)
        {
            assertion.Which.Message.Should().Contain(code.ToString());
        }

        client.ValidateCalls.Should().Equal(ToAccount);
        client.SubmitCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task DeliverAsync_propagates_caller_cancellation_from_account_validation_unchanged()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var expected = new OperationCanceledException(cancellation.Token);
        var client = new FakeCoreBankApiClient { ValidateThrows = expected };
        var strategy = new HttpForwardOutboxDeliveryStrategy(client, BusinessMetrics);

        var act = () => strategy.DeliverAsync(Message(), cancellation.Token);

        var assertion = await act.Should().ThrowAsync<OperationCanceledException>();
        assertion.Which.Should().BeSameAs(expected);
        client.ValidateCancellationTokens.Should().Equal(cancellation.Token);
    }

    [Fact]
    public async Task DeliverAsync_propagates_caller_cancellation_from_transaction_submission_unchanged()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var expected = new OperationCanceledException(cancellation.Token);
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Success(
                new AccountValidation(ToAccount, true, null, null)),
            SubmitThrows = expected
        };
        var strategy = new HttpForwardOutboxDeliveryStrategy(client, BusinessMetrics);

        var act = () => strategy.DeliverAsync(Message(), cancellation.Token);

        var assertion = await act.Should().ThrowAsync<OperationCanceledException>();
        assertion.Which.Should().BeSameAs(expected);
        client.ValidateCancellationTokens.Should().Equal(cancellation.Token);
        client.SubmitCancellationTokens.Should().Equal(cancellation.Token);
    }

    // ---- Story 6.5: business metrics ----

    [Fact]
    public async Task DeliverAsync_records_a_succeeded_http_send_delivery_metric_for_the_transaction_command()
    {
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Success(
                new AccountValidation(ToAccount, true, null, null)),
            SubmitResult = CoreBankResult<TransactionSubmission>.Success(
                new TransactionSubmission("forward-key", "Pending", DateTimeOffset.UtcNow))
        };
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var strategy = new HttpForwardOutboxDeliveryStrategy(client, businessMetrics);

        await strategy.DeliverAsync(Message(), TestContext.Current.CancellationToken);

        var measurement = listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == "corebankdemo.messaging.deliveries").Which;
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["messaging.direction"] = "sent",
            ["messaging.transport"] = "http",
            ["messaging.message.type"] = "transaction-command",
            ["outcome"] = "succeeded",
        });
    }

    [Fact]
    public async Task DeliverAsync_records_a_failed_http_send_delivery_metric_when_submission_is_a_retry_outcome()
    {
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Success(
                new AccountValidation(ToAccount, true, null, null)),
            SubmitResult = CoreBankResult<TransactionSubmission>.Retry(CoreBankRetryReason.Timeout)
        };
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var strategy = new HttpForwardOutboxDeliveryStrategy(client, businessMetrics);

        var act = () => strategy.DeliverAsync(Message(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        listener.Measurements.Should().ContainSingle(m => m.InstrumentName == "corebankdemo.messaging.deliveries")
            .Which.Tags["outcome"].Should().Be("failed");
    }

    [Fact]
    public async Task DeliverAsync_records_no_delivery_metric_when_account_validation_is_a_retry_outcome()
    {
        // Account validation is a different message shape, outside the
        // closed message-type vocabulary, so it is never a source of a
        // "transaction-command" delivery measurement.
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Retry(CoreBankRetryReason.Timeout)
        };
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var strategy = new HttpForwardOutboxDeliveryStrategy(client, businessMetrics);

        var act = () => strategy.DeliverAsync(Message(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        listener.Measurements.Should().BeEmpty();
    }

    // ---- Spec: add-instant-payment-rail -- ICoreBankTransactionForwarder ----

    [Fact]
    public async Task ForwardAsync_returns_the_submission_and_carries_execute_inline_only_when_requested()
    {
        var submission = new TransactionSubmission("forward-key", "Completed", DateTimeOffset.UtcNow);
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Success(
                new AccountValidation(ToAccount, true, null, null)),
            SubmitResult = CoreBankResult<TransactionSubmission>.Success(submission)
        };
        ICoreBankTransactionForwarder strategy = new HttpForwardOutboxDeliveryStrategy(client, BusinessMetrics);

        var result = await strategy.ForwardAsync(Message(), executeInline: true, TestContext.Current.CancellationToken);

        result.Should().Be(submission);
        client.SubmitExecuteInlineFlags.Should().Equal(true);
    }

    // ---- Review loop 1: ResponsePayload is populated on every completed
    // delivery, not only the instant rail. ----

    [Fact]
    public async Task ForwardAsync_persists_the_serialized_submission_onto_the_message_on_success()
    {
        var submission = new TransactionSubmission("forward-key", "Failed", DateTimeOffset.UtcNow);
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Success(
                new AccountValidation(ToAccount, true, null, null)),
            SubmitResult = CoreBankResult<TransactionSubmission>.Success(submission)
        };
        ICoreBankTransactionForwarder strategy = new HttpForwardOutboxDeliveryStrategy(client, BusinessMetrics);
        var message = Message();
        message.ResponsePayload.Should().BeNull();

        await strategy.ForwardAsync(message, executeInline: true, TestContext.Current.CancellationToken);

        message.ResponsePayload.Should().NotBeNullOrEmpty();
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<TransactionSubmission>(message.ResponsePayload!);
        roundTripped.Should().Be(submission);
    }

    [Fact]
    public async Task DeliverAsync_also_persists_the_serialized_submission_for_the_background_path()
    {
        var submission = new TransactionSubmission("forward-key", "Completed", DateTimeOffset.UtcNow);
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Success(
                new AccountValidation(ToAccount, true, null, null)),
            SubmitResult = CoreBankResult<TransactionSubmission>.Success(submission)
        };
        var strategy = new HttpForwardOutboxDeliveryStrategy(client, BusinessMetrics);
        var message = Message();

        await strategy.DeliverAsync(message, TestContext.Current.CancellationToken);

        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<TransactionSubmission>(message.ResponsePayload!);
        roundTripped.Should().Be(submission);
    }

    [Fact]
    public async Task ForwardAsync_never_persists_a_response_payload_when_submission_fails()
    {
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Success(
                new AccountValidation(ToAccount, true, null, null)),
            SubmitResult = CoreBankResult<TransactionSubmission>.Retry(CoreBankRetryReason.Timeout)
        };
        ICoreBankTransactionForwarder strategy = new HttpForwardOutboxDeliveryStrategy(client, BusinessMetrics);
        var message = Message();

        var act = () => strategy.ForwardAsync(message, executeInline: true, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        message.ResponsePayload.Should().BeNull();
    }

    [Fact]
    public async Task DeliverAsync_always_forwards_with_execute_inline_false()
    {
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Success(
                new AccountValidation(ToAccount, true, null, null)),
            SubmitResult = CoreBankResult<TransactionSubmission>.Success(
                new TransactionSubmission("forward-key", "Pending", DateTimeOffset.UtcNow))
        };
        var strategy = new HttpForwardOutboxDeliveryStrategy(client, BusinessMetrics);

        await strategy.DeliverAsync(Message(), TestContext.Current.CancellationToken);

        client.SubmitExecuteInlineFlags.Should().Equal(false);
    }

    [Fact]
    public async Task ForwardAsync_throws_on_a_business_rejection_at_account_validation_regardless_of_execute_inline()
    {
        var client = new FakeCoreBankApiClient
        {
            ValidateResult = CoreBankResult<AccountValidation>.Success(
                new AccountValidation(ToAccount, false, null, null))
        };
        ICoreBankTransactionForwarder strategy = new HttpForwardOutboxDeliveryStrategy(client, BusinessMetrics);

        var act = () => strategy.ForwardAsync(Message(), executeInline: true, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        client.SubmitCalls.Should().BeEmpty();
    }

    private sealed class FakeCoreBankApiClient : ICoreBankApiClient
    {
        public CoreBankResult<AccountValidation>? ValidateResult { get; set; }

        public Exception? ValidateThrows { get; set; }

        public CoreBankResult<TransactionSubmission>? SubmitResult { get; set; }

        public Exception? SubmitThrows { get; set; }

        public List<string> ValidateCalls { get; } = new();

        public List<CancellationToken> ValidateCancellationTokens { get; } = new();

        public List<TransactionSubmissionRequest> SubmitCalls { get; } = new();

        public List<CancellationToken> SubmitCancellationTokens { get; } = new();

        public Task<CoreBankResult<AccountValidation>> ValidateAccountAsync(
            string accountNumber, CancellationToken cancellationToken)
        {
            ValidateCalls.Add(accountNumber);
            ValidateCancellationTokens.Add(cancellationToken);
            return ValidateThrows is not null
                ? Task.FromException<CoreBankResult<AccountValidation>>(ValidateThrows)
                : Task.FromResult(ValidateResult!);
        }

        public Task<CoreBankResult<AccountDetails>> GetAccountDetailsAsync(
            string accountNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the forwarding strategy.");

        public List<bool> SubmitExecuteInlineFlags { get; } = new();

        public Task<CoreBankResult<TransactionSubmission>> ProcessTransactionAsync(
            TransactionSubmissionRequest request, CancellationToken cancellationToken, bool executeInline = false)
        {
            SubmitCalls.Add(request);
            SubmitCancellationTokens.Add(cancellationToken);
            SubmitExecuteInlineFlags.Add(executeInline);
            return SubmitThrows is not null
                ? Task.FromException<CoreBankResult<TransactionSubmission>>(SubmitThrows)
                : Task.FromResult(SubmitResult!);
        }

        public Task<CoreBankResult<TransactionStatus>> GetTransactionStatusAsync(
            string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the forwarding strategy.");
    }
}
