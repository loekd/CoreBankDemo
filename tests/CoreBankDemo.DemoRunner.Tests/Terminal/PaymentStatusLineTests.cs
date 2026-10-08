using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Terminal;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

/// <summary>The spec's result-line table, one case per state.</summary>
public class PaymentStatusLineTests
{
    private static readonly DateTimeOffset Pressed = new(2026, 8, 29, 12, 0, 11, TimeSpan.Zero);

    private static PaymentStatusFetch Answered(int status, string? body, string? error = null, bool sent = true) =>
        new("tx-1", Pressed, new InspectionResult(
            status is >= 200 and < 300,
            status,
            KnownEndpoints.PaymentStatus,
            body,
            error,
            TimeSpan.FromMilliseconds(38),
            sent ? new HttpExchange("GET", "http://127.0.0.1:5294/api/payments/tx-1", [], null, status == 0 ? null : status, null, [], body) : null));

    private static string Body(string status, string processedAt = "2026-08-29T12:00:09+00:00") =>
        $$"""{"paymentId":"tx-1","transactionId":"tx-1","status":"{{status}}","amount":1.00,"currency":"EUR","processedAt":"{{processedAt}}"}""";

    [Fact]
    public void BeforeTheFirstFetch_TheLineIsEmpty()
    {
        PaymentStatusLine.Build(null).Should().Be(FetchLineViewModel.Empty);
        PaymentStatusLine.Caption(null, Pressed).Should().Be("Fetch");
    }

    [Fact]
    public void InFlight_ShowsTheRequest_AndTheButtonCountsUp()
    {
        var fetch = new PaymentStatusFetch("tenant/payment-1", Pressed, null);

        var line = PaymentStatusLine.Build(fetch);

        line.Status.Should().Be("~ GET /api/payments/tenant/payment-1 …");
        line.Stamp.Should().BeEmpty();
        line.Tone.Should().Be(LineTone.Neutral);
        PaymentStatusLine.Caption(fetch, Pressed.AddSeconds(2)).Should().Be("Fetching — 2s");
    }

    [Fact]
    public void A200_WithAPendingBody_SaysSince()
    {
        var line = PaymentStatusLine.Build(Answered(200, Body("Pending")));

        line.Status.Should().Be("✓ 200  Pending · 1.00 EUR · since 12:00:09");
        line.Stamp.Should().Be("fetched 12:00:11 · 38 ms");
        line.Tone.Should().Be(LineTone.Accent);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public void A200_WithACommittedBody_SaysAt(string status)
    {
        PaymentStatusLine.Build(Answered(200, Body(status))).Status
            .Should().Be($"✓ 200  {status} · 1.00 EUR · at 12:00:09");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"status":""}""")]
    [InlineData("""{"status":"Pending"}""")]
    public void A200_WithAnUnreadableBody_IsAFailure(string body)
    {
        var line = PaymentStatusLine.Build(Answered(200, body));

        line.Status.Should().Be("✗ 200  unreadable response body");
        line.Tone.Should().Be(LineTone.Failure);
    }

    [Fact]
    public void A404_IsNeutral()
    {
        var line = PaymentStatusLine.Build(Answered(404, string.Empty, "HTTP 404"));

        line.Status.Should().Be("○ 404  no payment with this id");
        line.Stamp.Should().Be("fetched 12:00:11 · 38 ms");
        line.Tone.Should().Be(LineTone.Neutral);
    }

    [Fact]
    public void AnyOtherStatus_IsAFailure()
    {
        var line = PaymentStatusLine.Build(Answered(503, "busy", "HTTP 503"));

        line.Status.Should().Be("✗ 503  unexpected answer from PaymentsAPI");
        line.Tone.Should().Be(LineTone.Failure);
    }

    [Fact]
    public void NoAnswer_ToASentRequest_IsUnreachable()
    {
        var line = PaymentStatusLine.Build(Answered(0, null, "Connection refused"));

        line.Status.Should().Be("✗ PaymentsAPI unreachable — Connection refused");
        line.Tone.Should().Be(LineTone.Failure);
    }

    [Fact]
    public void ARefusal_StatesItsReason()
    {
        var refused = new PaymentStatusFetch("", Pressed, new InspectionResult(
            false, 0, KnownEndpoints.PaymentStatus, null, "Enter a payment id.", TimeSpan.Zero));

        var line = PaymentStatusLine.Build(refused);

        line.Status.Should().Be("✗ Enter a payment id.");
        line.Stamp.Should().Be("fetched 12:00:11 · 0 ms");
        line.Tone.Should().Be(LineTone.Failure);
    }

    [Fact]
    public void TheModel_CarriesTheLine_AndDisablesFetchOnlyWhileOneIsOut()
    {
        var idle = OperatorConsoleState.Empty with { PaymentStatusFetch = Answered(404, string.Empty) };
        var busy = OperatorConsoleState.Empty with { PaymentStatusFetch = new PaymentStatusFetch("tx-1", Pressed, null) };

        var idleModel = PresentationModelBuilder.Build(idle, Pressed);
        var busyModel = PresentationModelBuilder.Build(busy, Pressed.AddSeconds(1));

        idleModel.FetchLine.Status.Should().StartWith("○ 404");
        idleModel.CanFetch.Should().BeTrue();
        idleModel.FetchCaption.Should().Be("Fetch");
        busyModel.CanFetch.Should().BeFalse();
        busyModel.FetchCaption.Should().Be("Fetching — 1s");
    }
}
