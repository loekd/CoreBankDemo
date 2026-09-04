using System.Net;
using System.Text;
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Infrastructure;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

public class HttpPaymentGatewayTests
{
    [Fact]
    public async Task Submit_InstantSuppliedKey_SendsKnownUrlHeaderAndScheme()
    {
        Uri? capturedUri = null;
        string? capturedKey = null;
        string? capturedBody = null;
        var handler = new StubHttpHandler(async request =>
        {
            capturedUri = request.RequestUri;
            capturedKey = request.Headers.GetValues("Idempotency-Key").Single();
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"paymentId":"p1","transactionId":"key-1","status":"Completed"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        using var client = new HttpClient(handler);
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Instant),
            IdempotencyMode.Supplied,
            "key-1");

        var result = await gateway.SubmitAsync(TopologyProfile.LoadTests, submission, CancellationToken.None);

        result.Outcome.Should().Be(PaymentOutcome.Completed);
        capturedUri.Should().Be("http://127.0.0.1:5295/api/payments");
        capturedKey.Should().Be("key-1");
        capturedBody.Should().Contain("\"Scheme\":\"instant\"");
    }

    [Fact]
    public async Task Submit_OmittedKey_DoesNotSendHeaderAndMaps202Pending()
    {
        var hadIdempotencyHeader = true;
        using var client = new HttpClient(new StubHttpHandler(request =>
        {
            hadIdempotencyHeader = request.Headers.Contains("Idempotency-Key");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"paymentId":"p1","transactionId":"server-key","status":"Pending"}"""),
            });
        }));
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Omitted,
            null);

        var result = await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None);

        result.Outcome.Should().Be(PaymentOutcome.Pending);
        hadIdempotencyHeader.Should().BeFalse();
    }

    [Fact]
    public async Task Submit_Timeout_IsAmbiguous()
    {
        using var client = new HttpClient(new StubHttpHandler(_ => throw new TaskCanceledException("timeout")));
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Omitted,
            null);

        var result = await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None);

        result.Outcome.Should().Be(PaymentOutcome.Ambiguous);
        result.IsAmbiguous.Should().BeTrue();
    }

    [Fact]
    public async Task Submit_MalformedOrMismatchedSuccess_IsTransportFailure()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            new(HttpStatusCode.Accepted) { Content = new StringContent("[]") },
            new(HttpStatusCode.Accepted) { Content = new StringContent("""{"paymentId":"p","transactionId":"other","status":"Pending"}""") },
        ]);
        using var client = new HttpClient(new StubHttpHandler(_ => Task.FromResult(responses.Dequeue())));
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Supplied,
            "expected");

        (await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None)).Outcome
            .Should().Be(PaymentOutcome.TransportFailure);
        (await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None)).Outcome
            .Should().Be(PaymentOutcome.TransportFailure);
    }

    [Fact]
    public async Task Submit_202FailedBody_IsACommittedFailureNotAMalformedContract()
    {
        // spec-add-instant-payment-rail.md:184 — an instant duplicate whose row has
        // permanently failed deliberately replays 202/Failed rather than masking a
        // given-up delivery as still in flight. Reading that as a contract violation
        // reported "malformed or mismatched" for a truthful terminal answer.
        using var client = new HttpClient(new StubHttpHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"paymentId":"p","transactionId":"expected","status":"Failed"}"""),
            })));
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Instant),
            IdempotencyMode.Supplied,
            "expected");

        var result = await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None);

        result.StatusCode.Should().Be(202);
        result.Outcome.Should().Be(PaymentOutcome.Failed);
        result.ErrorSummary.Should().BeNull();
    }

    [Fact]
    public async Task Submit_ConnectionFailureWithOmittedKey_IsAmbiguous()
    {
        using var client = new HttpClient(new StubHttpHandler(_ => throw new HttpRequestException("reset")));
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Omitted,
            null);

        var result = await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None);

        result.Outcome.Should().Be(PaymentOutcome.Ambiguous);
    }

    [Fact]
    public async Task QueryAndInspect_UseOnlyCompiledEndpoints()
    {
        var requests = new List<Uri>();
        using var client = new HttpClient(new StubHttpHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }));
        var gateway = new HttpPaymentGateway(client);

        await gateway.QueryOutcomeAsync(TopologyProfile.Regular, "key/with slash", CancellationToken.None);
        await gateway.InspectAsync(TopologyProfile.LoadTests, KnownEndpoints.CoreBankInbox, CancellationToken.None);
        var rejected = await gateway.InspectAsync(TopologyProfile.Regular, KnownEndpoints.CoreBankInbox, CancellationToken.None);

        requests[0].AbsoluteUri.Should().Contain("key%2Fwith%20slash");
        requests[1].AbsoluteUri.Should().Be("http://localhost:5181/corebank/inbox");
        rejected.Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.OK, true)]
    public async Task Inspect_MapsHttpStatus(HttpStatusCode status, bool succeeded)
    {
        using var client = new HttpClient(new StubHttpHandler(_ => Task.FromResult(
            new HttpResponseMessage(status) { Content = new StringContent("{}") })));

        var result = await new HttpPaymentGateway(client)
            .InspectAsync(TopologyProfile.LoadTests, KnownEndpoints.PaymentsOutbox, CancellationToken.None);

        result.Succeeded.Should().Be(succeeded);
        result.StatusCode.Should().Be((int)status);
    }

    [Fact]
    public async Task Inspect_TimeoutAndTransportFailure_AreDistinctDetails()
    {
        var responses = new Queue<Func<Task<HttpResponseMessage>>>(
        [
            () => throw new TaskCanceledException("timeout"),
            () => throw new HttpRequestException("connection"),
        ]);
        using var client = new HttpClient(new StubHttpHandler(_ => responses.Dequeue()()));
        var gateway = new HttpPaymentGateway(client);

        var timeout = await gateway.InspectAsync(TopologyProfile.LoadTests, KnownEndpoints.PaymentsOutbox, CancellationToken.None);
        var connection = await gateway.InspectAsync(TopologyProfile.LoadTests, KnownEndpoints.PaymentsOutbox, CancellationToken.None);

        timeout.ErrorSummary.Should().Contain("timed out");
        connection.ErrorSummary.Should().Contain("connection");
    }

    [Theory]
    // PaymentsController.ToDuplicateResult: an instant-rail duplicate replay of a terminally
    // failed row answers 202 with the wire word "Failed"; ToAcceptedResult/ToResponse replays
    // the raw kernel status on the standard rail, which can be Processing or Completed once
    // the first submit has moved on. All of these are contractual, none is malformed.
    [InlineData("Failed", PaymentOutcome.Failed)]
    [InlineData("Processing", PaymentOutcome.Pending)]
    [InlineData("Completed", PaymentOutcome.Completed)]
    [InlineData("Pending", PaymentOutcome.Pending)]
    public async Task Submit_DuplicateReplayOn202_IsNotTreatedAsMalformed(string wireStatus, PaymentOutcome expected)
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(
                    $$"""{"paymentId":"demo-key-001","transactionId":"demo-key-001","status":"{{wireStatus}}"}"""),
            })));
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Instant),
            IdempotencyMode.Supplied,
            "demo-key-001");

        var result = await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None);

        result.Outcome.Should().Be(expected);
        result.ErrorSummary.Should().BeNull();
    }

    [Fact]
    public async Task Submit_UnrecognisedStatusOn202_IsStillTreatedAsMalformed()
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"paymentId":"k","transactionId":"k","status":"Teleported"}"""),
            })));
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Instant),
            IdempotencyMode.Supplied,
            "k");

        var result = await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None);

        result.Outcome.Should().Be(PaymentOutcome.TransportFailure);
        result.ErrorSummary.Should().Contain("malformed");
    }

    [Theory]
    // The regression that made an instant demo payment report "malformed or mismatched":
    // PaymentsController.ToDuplicateResult replays 200 for an already-Completed instant row,
    // and ResolveDeliveredResponse takes the status straight from the persisted CoreBank
    // delivery payload — which is "Pending" when the inline attempt was accepted for deferred
    // execution and "Processing" for an in-flight duplicate. Pairing 200 with the status word
    // rejected both as a contract violation.
    [InlineData("Completed", PaymentOutcome.Completed)]
    [InlineData("Failed", PaymentOutcome.Failed)]
    [InlineData("Pending", PaymentOutcome.Pending)]
    [InlineData("Processing", PaymentOutcome.Pending)]
    public async Task Submit_InstantDuplicateReplayOn200_ReportsTheReplayedStatus(
        string wireStatus, PaymentOutcome expected)
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"paymentId":"demo-key-001","transactionId":"demo-key-001","status":"{{wireStatus}}"}"""),
            })));
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Instant),
            IdempotencyMode.Supplied,
            "demo-key-001");

        var result = await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None);

        result.Outcome.Should().Be(expected);
        result.ErrorSummary.Should().BeNull();
    }

    [Fact]
    public async Task Submit_ContractViolation_NamesTheOffendingValueNotJustTheVerdict()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            new(HttpStatusCode.OK) { Content = new StringContent("""{"paymentId":"k","transactionId":"someone-else","status":"Completed"}""") },
            new(HttpStatusCode.OK) { Content = new StringContent("<html>gateway</html>") },
            new(HttpStatusCode.BadRequest) { Content = new StringContent("""{"errors":["Amount must be positive."]}""") },
        ]);
        using var client = new HttpClient(new StubHttpHandler(_ => Task.FromResult(responses.Dequeue())));
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Instant),
            IdempotencyMode.Supplied,
            "expected-key");

        var mismatch = await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None);
        var notJson = await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None);
        var rejected = await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None);

        mismatch.ErrorSummary.Should().Contain("expected-key").And.Contain("someone-else");
        notJson.ErrorSummary.Should().Contain("gateway");
        rejected.ErrorSummary.Should().Contain("400").And.Contain("Amount must be positive.");
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }
}
