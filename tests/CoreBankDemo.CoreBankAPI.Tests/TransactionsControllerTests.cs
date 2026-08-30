using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI.Controllers;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
using CoreBankDemo.Messaging;
using CoreBankDemo.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace CoreBankDemo.CoreBankAPI.Tests;

/// <summary>
/// Thin controller tests (spec-4-4's code map): the <see cref="ModelState"/>-
/// invalid path (proving the manual check — reachable now that
/// <c>ApiBehaviorOptions.SuppressModelStateInvalidFilter</c> is set in
/// <c>Program.cs</c> — actually runs), and delegation-to-handler outcome
/// mapping, against a mocked <see cref="ITransactionIntakeHandler"/>. No HTTP
/// pipeline: the controller is constructed directly with a manually-built
/// <see cref="ControllerContext"/>.
/// </summary>
public class TransactionsControllerTests
{
    private const string FromAccount = "NL91ABNA0417164300";
    private const string ToAccount = "NL20INGB0001234567";
    private const string TransactionId = "txn-123";

    private readonly Mock<ITransactionIntakeHandler> _handler = new(MockBehavior.Strict);
    private readonly BusinessMetrics _businessMetrics = new();

    private static TransactionRequest ValidRequest() => new(FromAccount, ToAccount, 50m, "EUR", TransactionId);

    private TransactionsController CreateController()
    {
        var controller = new TransactionsController(_handler.Object, _businessMetrics)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        return controller;
    }

    [Fact]
    public async Task ProcessTransaction_returns_bad_request_with_all_errors_when_model_state_is_invalid()
    {
        var controller = CreateController();
        controller.ModelState.AddModelError("Amount", "Amount is required");
        controller.ModelState.AddModelError("Currency", "Currency is required");

        var result = await controller.ProcessTransaction(ValidRequest(), TestContext.Current.CancellationToken);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var errors = GetErrors(badRequest.Value);
        errors.Should().BeEquivalentTo(["Amount is required", "Currency is required"]);

        _handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessTransaction_maps_accepted_outcome_to_202_with_the_location_and_response()
    {
        var response = new TransactionResponse(TransactionId, MessageConstants.Status.Pending, DateTimeOffset.UtcNow);
        _handler.Setup(h => h.ProcessAsync(It.IsAny<TransactionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionIntakeResult(TransactionIntakeOutcome.Accepted, response, null));

        var controller = CreateController();

        var result = await controller.ProcessTransaction(ValidRequest(), TestContext.Current.CancellationToken);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.Location.Should().Be($"/api/transactions/{TransactionId}");
        accepted.Value.Should().Be(response);
    }

    [Fact]
    public async Task ProcessTransaction_maps_replayed_outcome_to_200_ok_with_the_cached_response()
    {
        var response = new TransactionResponse(TransactionId, MessageConstants.Status.Completed, DateTimeOffset.UtcNow);
        _handler.Setup(h => h.ProcessAsync(It.IsAny<TransactionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionIntakeResult(TransactionIntakeOutcome.Replayed, response, null));

        var controller = CreateController();

        var result = await controller.ProcessTransaction(ValidRequest(), TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(response);
    }

    [Fact]
    public async Task ProcessTransaction_maps_in_flight_outcome_to_202_with_current_status()
    {
        var response = new TransactionResponse(TransactionId, MessageConstants.Status.Processing, DateTimeOffset.UtcNow);
        _handler.Setup(h => h.ProcessAsync(It.IsAny<TransactionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionIntakeResult(TransactionIntakeOutcome.InFlight, response, null));

        var controller = CreateController();

        var result = await controller.ProcessTransaction(ValidRequest(), TestContext.Current.CancellationToken);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.Location.Should().Be($"/api/transactions/{TransactionId}");
        accepted.Value.Should().Be(response);
    }

    [Fact]
    public async Task ProcessTransaction_maps_transport_failed_outcome_to_bad_request_with_errors()
    {
        _handler.Setup(h => h.ProcessAsync(It.IsAny<TransactionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionIntakeResult(TransactionIntakeOutcome.TransportFailed, null, ["boom"]));

        var controller = CreateController();

        var result = await controller.ProcessTransaction(ValidRequest(), TestContext.Current.CancellationToken);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        GetErrors(badRequest.Value).Should().Equal("boom");
    }

    // ---- Story 6.5: business metrics ----

    [Theory]
    [InlineData(TransactionIntakeOutcome.Accepted, "succeeded")]
    [InlineData(TransactionIntakeOutcome.Replayed, "duplicate")]
    [InlineData(TransactionIntakeOutcome.InFlight, "duplicate")]
    [InlineData(TransactionIntakeOutcome.TransportFailed, "failed")]
    public async Task ProcessTransaction_records_the_http_receive_delivery_outcome_matching_the_intake_outcome(
        TransactionIntakeOutcome intakeOutcome, string expectedDeliveryOutcome)
    {
        var response = new TransactionResponse(TransactionId, MessageConstants.Status.Pending, DateTimeOffset.UtcNow);
        _handler.Setup(h => h.ProcessAsync(It.IsAny<TransactionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionIntakeResult(
                intakeOutcome,
                intakeOutcome == TransactionIntakeOutcome.TransportFailed ? null : response,
                intakeOutcome == TransactionIntakeOutcome.TransportFailed ? ["boom"] : null));
        using var listener = new MetricsTestListener(_businessMetrics);
        var controller = CreateController();

        await controller.ProcessTransaction(ValidRequest(), TestContext.Current.CancellationToken);

        var measurement = listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == "corebankdemo.messaging.deliveries").Which;
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["messaging.direction"] = "received",
            ["messaging.transport"] = "http",
            ["messaging.message.type"] = "transaction-command",
            ["outcome"] = expectedDeliveryOutcome,
        });
    }

    [Fact]
    public async Task ProcessTransaction_records_no_delivery_metric_when_model_state_is_invalid()
    {
        using var listener = new MetricsTestListener(_businessMetrics);
        var controller = CreateController();
        controller.ModelState.AddModelError("Amount", "Amount is required");

        await controller.ProcessTransaction(ValidRequest(), TestContext.Current.CancellationToken);

        listener.Measurements.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessTransaction_records_a_failed_http_receive_and_rethrows_handler_failure()
    {
        var failure = new InvalidOperationException("database unavailable");
        _handler.Setup(h => h.ProcessAsync(It.IsAny<TransactionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        using var listener = new MetricsTestListener(_businessMetrics);
        var controller = CreateController();

        var act = () => controller.ProcessTransaction(ValidRequest(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        listener.Measurements.Should()
            .ContainSingle(m => m.InstrumentName == BusinessMetrics.MessagingDeliveriesInstrumentName)
            .Which.Tags["outcome"].Should().Be("failed");
    }

    [Fact]
    public async Task GetTransactionStatus_returns_not_found_when_the_handler_reports_not_found()
    {
        _handler.Setup(h => h.GetStatusAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionStatusResult(false, null, null));

        var controller = CreateController();

        var result = await controller.GetTransactionStatus(TransactionId, TestContext.Current.CancellationToken);

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        GetErrors(notFound.Value).Should().Equal("Transaction not found");
    }

    [Fact]
    public async Task GetTransactionStatus_returns_ok_with_the_cached_response_when_present()
    {
        var response = new TransactionResponse(TransactionId, MessageConstants.Status.Completed, DateTimeOffset.UtcNow);
        _handler.Setup(h => h.GetStatusAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionStatusResult(true, response, null));

        var controller = CreateController();

        var result = await controller.GetTransactionStatus(TransactionId, TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(response);
    }

    [Fact]
    public async Task GetTransactionStatus_returns_ok_with_the_status_response_when_no_cached_response_is_present()
    {
        var statusResponse = new TransactionStatusResponse(TransactionId, MessageConstants.Status.Pending, DateTime.UtcNow, null);
        _handler.Setup(h => h.GetStatusAsync(TransactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionStatusResult(true, null, statusResponse));

        var controller = CreateController();

        var result = await controller.GetTransactionStatus(TransactionId, TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(statusResponse);
    }

    private static IEnumerable<string> GetErrors(object? value)
    {
        var property = value!.GetType().GetProperty("Errors");
        property.Should().NotBeNull();
        return ((IEnumerable<string>)property!.GetValue(value)!).ToList();
    }
}
