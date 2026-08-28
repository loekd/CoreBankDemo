using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Controllers;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Moq;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// Thin controller tests (spec-5-2's code map): the <see cref="ModelState"/>-
/// invalid path (proving the manual check -- reachable now that
/// <c>ApiBehaviorOptions.SuppressModelStateInvalidFilter</c> is set in
/// <c>Program.cs</c> -- actually runs), exact handler inputs/cancellation,
/// stored/duplicate winner mapping, location, and invalid-state handling --
/// against a mocked <see cref="IPaymentStorageHandler"/>. No HTTP pipeline:
/// the controller is constructed directly with a manually-built
/// <see cref="ControllerContext"/>.
/// </summary>
public class PaymentsControllerTests
{
    private const string FromAccount = "NL91ABNA0417164300";
    private const string ToAccount = "NL20INGB0001234567";
    private const string IdempotencyKey = "payment-key";
    private const string TransactionId = "txn-abc";

    private readonly Mock<IPaymentStorageHandler> _handler = new(MockBehavior.Strict);

    private static PaymentRequest ValidRequest() => new(FromAccount, ToAccount, 50m, "EUR");

    private static PaymentSnapshot Snapshot(
        string idempotencyKey = IdempotencyKey,
        string transactionId = TransactionId,
        string status = "Pending",
        decimal amount = 50m,
        string currency = "EUR") => new(
        Guid.NewGuid(),
        idempotencyKey,
        transactionId,
        FromAccount,
        ToAccount,
        amount,
        currency,
        PartitionId: 1,
        status,
        CreatedAt: new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc),
        TraceParent: null,
        TraceState: null);

    private PaymentsController CreateController(string? idempotencyKeyHeader = null)
    {
        var httpContext = new DefaultHttpContext();
        if (idempotencyKeyHeader is not null)
        {
            httpContext.Request.Headers["Idempotency-Key"] = idempotencyKeyHeader;
        }

        return new PaymentsController(_handler.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    [Fact]
    public async Task ProcessPayment_returns_bad_request_with_all_errors_when_model_state_is_invalid()
    {
        var controller = CreateController();
        controller.ModelState.AddModelError("Amount", "Amount is required");
        controller.ModelState.AddModelError("Currency", "Currency is required");

        var result = await controller.ProcessPayment(ValidRequest(), TestContext.Current.CancellationToken);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        GetErrors(badRequest.Value).Should().BeEquivalentTo(["Amount is required", "Currency is required"]);

        _handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessPayment_returns_a_meaningful_error_for_exception_only_model_errors()
    {
        var controller = CreateController();
        controller.ModelState.AddModelError("request", new InvalidOperationException());

        var result = await controller.ProcessPayment(ValidRequest(), TestContext.Current.CancellationToken);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        GetErrors(badRequest.Value).Should().Equal("The request is invalid.");
        _handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessPayment_passes_the_request_header_value_and_cancellation_token_unchanged_to_the_handler()
    {
        var request = ValidRequest();
        using var cts = new CancellationTokenSource();
        _handler
            .Setup(h => h.StoreAsync(request, IdempotencyKey, cts.Token))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Stored, Snapshot(), []));

        var controller = CreateController(IdempotencyKey);

        await controller.ProcessPayment(request, cts.Token);

        _handler.Verify(h => h.StoreAsync(request, IdempotencyKey, cts.Token), Times.Once);
    }

    [Fact]
    public async Task ProcessPayment_passes_a_null_idempotency_key_when_the_header_is_absent()
    {
        var request = ValidRequest();
        _handler
            .Setup(h => h.StoreAsync(request, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Stored, Snapshot(), []));

        var controller = CreateController();

        await controller.ProcessPayment(request, TestContext.Current.CancellationToken);

        _handler.Verify(h => h.StoreAsync(request, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessPayment_passes_only_the_first_idempotency_header_value()
    {
        var request = ValidRequest();
        _handler
            .Setup(h => h.StoreAsync(request, "first-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Stored, Snapshot(), []));
        var controller = CreateController();
        controller.Request.Headers["Idempotency-Key"] =
            new StringValues(["first-key", "second-key"]);

        await controller.ProcessPayment(request, TestContext.Current.CancellationToken);

        _handler.Verify(
            h => h.StoreAsync(request, "first-key", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessPayment_maps_stored_outcome_to_202_with_the_winner_derived_response_and_location()
    {
        var snapshot = Snapshot(idempotencyKey: "key-1", transactionId: "txn-1", status: "Pending", amount: 12.34m, currency: "EUR");
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Stored, snapshot, []));

        var controller = CreateController();

        var result = await controller.ProcessPayment(ValidRequest(), TestContext.Current.CancellationToken);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.Location.Should().Be("/api/payments/txn-1");
        var response = accepted.Value.Should().BeOfType<PaymentResponse>().Subject;
        response.PaymentId.Should().Be("key-1");
        response.TransactionId.Should().Be("txn-1");
        response.Status.Should().Be("Pending");
        response.Amount.Should().Be(12.34m);
        response.Currency.Should().Be("EUR");
        response.ProcessedAt.Should().Be(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task ProcessPayment_maps_duplicate_outcome_to_202_referencing_the_persisted_winner_not_retry_values()
    {
        // Retry payload differs from the persisted winner snapshot; the
        // response must reflect the winner, not the request just submitted.
        var winner = Snapshot(idempotencyKey: "key-2", transactionId: "txn-2", status: "Completed", amount: 99.99m, currency: "USD");
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Duplicate, winner, []));

        var controller = CreateController();
        var retryRequest = new PaymentRequest(FromAccount, ToAccount, 1.00m, "EUR");

        var result = await controller.ProcessPayment(retryRequest, TestContext.Current.CancellationToken);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.Location.Should().Be("/api/payments/txn-2");
        var response = accepted.Value.Should().BeOfType<PaymentResponse>().Subject;
        response.PaymentId.Should().Be("key-2");
        response.TransactionId.Should().Be("txn-2");
        response.Status.Should().Be("Completed");
        response.Amount.Should().Be(99.99m);
        response.Currency.Should().Be("USD");
        response.ProcessedAt.Should().Be(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task ProcessPayment_escapes_the_transaction_identity_in_the_location()
    {
        var snapshot = Snapshot(idempotencyKey: "order/123?#", transactionId: "order/123?#");
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Stored, snapshot, []));
        var controller = CreateController();

        var result = await controller.ProcessPayment(ValidRequest(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<AcceptedResult>().Subject.Location
            .Should().Be("/api/payments/order%2F123%3F%23");
    }

    [Fact]
    public async Task ProcessPayment_maps_validation_failed_outcome_to_bad_request_with_all_handler_errors()
    {
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(
                PaymentStorageOutcome.ValidationFailed,
                null,
                ["Idempotency key must be between 1 and 100 characters."]));

        var controller = CreateController();

        var result = await controller.ProcessPayment(ValidRequest(), TestContext.Current.CancellationToken);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        GetErrors(badRequest.Value).Should().Equal("Idempotency key must be between 1 and 100 characters.");
    }

    [Theory]
    [InlineData(PaymentStorageOutcome.Stored)]
    [InlineData(PaymentStorageOutcome.Duplicate)]
    public async Task ProcessPayment_throws_when_a_success_outcome_is_missing_its_persisted_snapshot(
        PaymentStorageOutcome outcome)
    {
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(outcome, null, []));

        var controller = CreateController();

        var act = () => controller.ProcessPayment(ValidRequest(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ProcessPayment_throws_on_an_unknown_outcome()
    {
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult((PaymentStorageOutcome)99, null, []));

        var controller = CreateController();

        var act = () => controller.ProcessPayment(ValidRequest(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static IEnumerable<string> GetErrors(object? value)
    {
        var property = value!.GetType().GetProperty("Errors");
        property.Should().NotBeNull();
        return ((IEnumerable<string>)property!.GetValue(value)!).ToList();
    }
}
