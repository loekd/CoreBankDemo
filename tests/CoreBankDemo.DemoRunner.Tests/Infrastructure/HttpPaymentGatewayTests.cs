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

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }
}
