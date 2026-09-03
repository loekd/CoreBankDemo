using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Controllers;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
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
    private readonly Mock<IInstantPaymentForwardingHandler> _instantHandler = new(MockBehavior.Strict);
    private readonly BusinessMetrics _businessMetrics = new();

    private static PaymentRequest ValidRequest() => new(FromAccount, ToAccount, 50m, "EUR");

    private static PaymentSnapshot Snapshot(
        string idempotencyKey = IdempotencyKey,
        string transactionId = TransactionId,
        string status = "Pending",
        decimal amount = 50m,
        string currency = "EUR",
        string? responsePayload = null) => new(
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
        TraceState: null,
        ResponsePayload: responsePayload);

    private PaymentsController CreateController(string? idempotencyKeyHeader = null)
    {
        var httpContext = new DefaultHttpContext();
        if (idempotencyKeyHeader is not null)
        {
            httpContext.Request.Headers["Idempotency-Key"] = idempotencyKeyHeader;
        }

        return new PaymentsController(_handler.Object, _instantHandler.Object, _businessMetrics)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    [Fact]
    public async Task ProcessPayment_returns_bad_request_with_all_errors_when_model_state_is_invalid()
    {
        using var listener = new MetricsTestListener(_businessMetrics);
        var controller = CreateController();
        controller.ModelState.AddModelError("Amount", "Amount is required");
        controller.ModelState.AddModelError("Currency", "Currency is required");

        var result = await controller.ProcessPayment(ValidRequest(), TestContext.Current.CancellationToken);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        GetErrors(badRequest.Value).Should().BeEquivalentTo(["Amount is required", "Currency is required"]);

        _handler.VerifyNoOtherCalls();
        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.PaymentIntakeInstrumentName)
            .Which.Tags["outcome"].Should().Be("validation_failed");
    }

    [Fact]
    public async Task ProcessPayment_returns_a_meaningful_error_for_exception_only_model_errors()
    {
        var controller = CreateController();
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(string));
        controller.ModelState.AddModelError("request", new InvalidOperationException(), metadata);

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

    // ---- Spec: add-instant-payment-rail -- scheme=instant branching ----

    private static PaymentRequest InstantRequest() =>
        new(FromAccount, ToAccount, 50m, "EUR", PaymentSchemes.Instant);

    [Fact]
    public async Task ProcessPayment_forwards_a_freshly_stored_instant_payment_and_maps_completed_to_200()
    {
        var snapshot = Snapshot(idempotencyKey: "key-3", transactionId: "txn-3", status: "Pending", amount: 12.34m, currency: "EUR");
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Stored, snapshot, []));
        var processedAt = new DateTimeOffset(2026, 8, 28, 12, 0, 5, TimeSpan.Zero);
        _instantHandler
            .Setup(h => h.ForwardAsync(snapshot, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InstantForwardResult(InstantDeliveryOutcome.Completed, processedAt));

        var controller = CreateController();

        var result = await controller.ProcessPayment(InstantRequest(), TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<PaymentResponse>().Subject;
        response.PaymentId.Should().Be("key-3");
        response.TransactionId.Should().Be("txn-3");
        response.Status.Should().Be("Completed");
        response.Amount.Should().Be(12.34m);
        response.Currency.Should().Be("EUR");
        response.ProcessedAt.Should().Be(processedAt);
    }

    [Fact]
    public async Task ProcessPayment_maps_a_business_rejection_on_the_instant_rail_to_200_with_failed_status()
    {
        var snapshot = Snapshot(status: "Pending");
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Stored, snapshot, []));
        var processedAt = DateTimeOffset.UtcNow;
        _instantHandler
            .Setup(h => h.ForwardAsync(snapshot, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InstantForwardResult(InstantDeliveryOutcome.Rejected, processedAt));

        var controller = CreateController();

        var result = await controller.ProcessPayment(InstantRequest(), TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<PaymentResponse>().Subject.Status.Should().Be("Failed");
    }

    [Fact]
    public async Task ProcessPayment_maps_a_deferred_instant_forward_to_202_exactly_like_the_standard_rail()
    {
        var snapshot = Snapshot(idempotencyKey: "key-4", transactionId: "txn-4", status: "Pending");
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Stored, snapshot, []));
        _instantHandler
            .Setup(h => h.ForwardAsync(snapshot, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InstantForwardResult(InstantDeliveryOutcome.Deferred, DateTimeOffset.UtcNow));

        var controller = CreateController();

        var result = await controller.ProcessPayment(InstantRequest(), TestContext.Current.CancellationToken);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.Location.Should().Be("/api/payments/txn-4");
        var response = accepted.Value.Should().BeOfType<PaymentResponse>().Subject;
        response.Status.Should().Be("Pending");
    }

    [Fact]
    public async Task ProcessPayment_never_forwards_a_standard_scheme_payment()
    {
        var snapshot = Snapshot(status: "Pending");
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Stored, snapshot, []));

        var controller = CreateController();

        var result = await controller.ProcessPayment(ValidRequest(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<AcceptedResult>();
        _instantHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessPayment_replays_a_completed_instant_duplicate_as_200_without_a_second_forward_attempt()
    {
        var winner = Snapshot(idempotencyKey: "key-5", transactionId: "txn-5", status: "Completed", amount: 5m, currency: "EUR");
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Duplicate, winner, []));

        var controller = CreateController();

        var result = await controller.ProcessPayment(InstantRequest(), TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<PaymentResponse>().Subject;
        response.Status.Should().Be("Completed");
        response.PaymentId.Should().Be("key-5");
        _instantHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessPayment_replays_a_pending_instant_duplicate_as_202_without_a_forward_attempt()
    {
        var winner = Snapshot(status: "Pending");
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Duplicate, winner, []));

        var controller = CreateController();

        var result = await controller.ProcessPayment(InstantRequest(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<AcceptedResult>();
        _instantHandler.VerifyNoOtherCalls();
    }

    // ---- Review loop 1: duplicate replay derives Status from the persisted
    // ResponsePayload, never the raw kernel Status column (AD-11), and never
    // leaks the internal "Processing" wire word. ----

    [Fact]
    public async Task ProcessPayment_replays_a_completed_instant_duplicate_business_rejection_as_200_failed()
    {
        // The row's raw kernel Status is Completed either way (AD-11:
        // transport-state-only) -- only the persisted ResponsePayload
        // distinguishes a business rejection from a business success.
        var payload = JsonSerializer.Serialize(
            new TransactionSubmission("txn-6", "Failed", DateTimeOffset.UtcNow));
        var winner = Snapshot(
            idempotencyKey: "key-6", transactionId: "txn-6", status: "Completed", responsePayload: payload);
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Duplicate, winner, []));

        var controller = CreateController();

        var result = await controller.ProcessPayment(InstantRequest(), TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<PaymentResponse>().Subject;
        response.Status.Should().Be("Failed");
        response.PaymentId.Should().Be("key-6");
        _instantHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessPayment_replays_a_completed_instant_duplicate_success_as_200_completed_from_payload()
    {
        var payload = JsonSerializer.Serialize(
            new TransactionSubmission("txn-7", "Completed", DateTimeOffset.UtcNow));
        var winner = Snapshot(
            idempotencyKey: "key-7", transactionId: "txn-7", status: "Completed", responsePayload: payload);
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Duplicate, winner, []));

        var controller = CreateController();

        var result = await controller.ProcessPayment(InstantRequest(), TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<PaymentResponse>().Subject.Status.Should().Be("Completed");
    }

    [Fact]
    public async Task ProcessPayment_falls_back_to_the_raw_status_when_a_completed_duplicates_payload_is_missing()
    {
        // Defensive: should not happen going forward (every completed
        // delivery now populates ResponsePayload), but a duplicate replay
        // must never crash over a null/corrupt payload.
        var winner = Snapshot(idempotencyKey: "key-8", transactionId: "txn-8", status: "Completed", responsePayload: null);
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Duplicate, winner, []));

        var controller = CreateController();

        var result = await controller.ProcessPayment(InstantRequest(), TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<PaymentResponse>().Subject.Status.Should().Be("Completed");
    }

    [Fact]
    public async Task ProcessPayment_falls_back_to_the_raw_status_when_a_completed_duplicates_payload_is_corrupt()
    {
        var winner = Snapshot(
            idempotencyKey: "key-9", transactionId: "txn-9", status: "Completed", responsePayload: "{not-valid-json");
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Duplicate, winner, []));

        var controller = CreateController();

        var result = await controller.ProcessPayment(InstantRequest(), TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<PaymentResponse>().Subject.Status.Should().Be("Completed");
    }

    [Fact]
    public async Task ProcessPayment_replays_an_instant_duplicate_still_claimed_as_202_pending_not_processing()
    {
        // The matrix only ever documents the wire word "Pending" for a
        // not-yet-delivered instant duplicate -- the row's internal
        // "Processing" value (a live claim in flight) must never leak onto
        // the wire (review loop 1).
        var winner = Snapshot(idempotencyKey: "key-10", transactionId: "txn-10", status: "Processing");
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Duplicate, winner, []));

        var controller = CreateController();

        var result = await controller.ProcessPayment(InstantRequest(), TestContext.Current.CancellationToken);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        var response = accepted.Value.Should().BeOfType<PaymentResponse>().Subject;
        response.Status.Should().Be("Pending");
        response.PaymentId.Should().Be("key-10");
        _instantHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessPayment_replays_a_permanently_failed_instant_duplicate_as_202_failed_not_pending()
    {
        // Review loop 2: a row whose transport retries were exhausted
        // (MarkAsFailedWithRetryAsync's terminal transition) must never be
        // masked as still-in-flight "Pending" -- it is genuinely given up on.
        var winner = Snapshot(idempotencyKey: "key-11", transactionId: "txn-11", status: "Failed");
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Duplicate, winner, []));

        var controller = CreateController();

        var result = await controller.ProcessPayment(InstantRequest(), TestContext.Current.CancellationToken);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        var response = accepted.Value.Should().BeOfType<PaymentResponse>().Subject;
        response.Status.Should().Be("Failed");
        response.PaymentId.Should().Be("key-11");
        _instantHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessPayment_replays_a_completed_instant_duplicates_actual_settlement_processed_at()
    {
        // Review loop 2: ProcessedAt must come from the same deserialized
        // TransactionSubmission as Status -- the row's CreatedAt is when the
        // payment was accepted, not when CoreBank actually settled it.
        var settledAt = new DateTimeOffset(2026, 9, 3, 8, 30, 0, TimeSpan.Zero);
        var payload = JsonSerializer.Serialize(new TransactionSubmission("txn-12", "Completed", settledAt));
        var winner = Snapshot(
            idempotencyKey: "key-12", transactionId: "txn-12", status: "Completed", responsePayload: payload);
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Duplicate, winner, []));

        var controller = CreateController();

        var result = await controller.ProcessPayment(InstantRequest(), TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<PaymentResponse>().Subject;
        response.ProcessedAt.Should().Be(settledAt);
        response.ProcessedAt.Should().NotBe(new DateTimeOffset(DateTime.SpecifyKind(winner.CreatedAt, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task ProcessPayment_falls_back_to_created_at_for_processed_at_when_a_completed_duplicates_payload_is_missing()
    {
        var winner = Snapshot(idempotencyKey: "key-13", transactionId: "txn-13", status: "Completed", responsePayload: null);
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Duplicate, winner, []));

        var controller = CreateController();

        var result = await controller.ProcessPayment(InstantRequest(), TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<PaymentResponse>().Subject;
        response.ProcessedAt.Should().Be(new DateTimeOffset(DateTime.SpecifyKind(winner.CreatedAt, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task ProcessPayment_standard_rail_duplicate_still_surfaces_the_raw_status_verbatim()
    {
        // Standard rail must stay byte-identical to baseline: even an
        // internal "Processing" value is reproduced verbatim (never
        // normalized), because that normalization is a NEW instant-rail wire
        // promise, not a standard-rail behaviour change.
        var winner = Snapshot(status: "Processing");
        _handler
            .Setup(h => h.StoreAsync(It.IsAny<PaymentRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStorageResult(PaymentStorageOutcome.Duplicate, winner, []));

        var controller = CreateController();

        var result = await controller.ProcessPayment(ValidRequest(), TestContext.Current.CancellationToken);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.Value.Should().BeOfType<PaymentResponse>().Subject.Status.Should().Be("Processing");
    }

    private static IEnumerable<string> GetErrors(object? value)
    {
        var property = value!.GetType().GetProperty("Errors");
        property.Should().NotBeNull();
        return ((IEnumerable<string>)property!.GetValue(value)!).ToList();
    }
}
