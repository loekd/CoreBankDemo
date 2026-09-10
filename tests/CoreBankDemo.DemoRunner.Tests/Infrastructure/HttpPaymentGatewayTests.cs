using System.Net;
using System.Text;
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;
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
    public async Task Submit_504Cancelled_IsAWithdrawnPaymentNotAContractViolation()
    {
        // ADR-020: the instant rail's time-out rejection. 504 with Status: Cancelled says the
        // payment was provably withdrawn before it executed -- a proven outcome the console
        // must show in the rail's own words, never as "PaymentsAPI returned HTTP 504".
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                Content = new StringContent(
                    """{"paymentId":"demo-key-001","transactionId":"demo-key-001","status":"Cancelled","processedAt":"2026-09-08T12:00:09Z"}"""),
            })));
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Instant),
            IdempotencyMode.Supplied,
            "demo-key-001");

        var result = await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None);

        result.Outcome.Should().Be(PaymentOutcome.Cancelled);
        result.StatusCode.Should().Be(504);
        result.ErrorSummary.Should().BeNull();
        result.TransactionId.Should().Be("demo-key-001");
    }

    [Theory]
    [InlineData("""{"paymentId":"k","transactionId":"k","status":"Pending"}""")]
    [InlineData("<html>upstream timed out</html>")]
    [InlineData("")]
    public async Task Submit_504WithoutACancelledBody_IsStillAGatewayFailure(string body)
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout) { Content = new StringContent(body) })));
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Instant),
            IdempotencyMode.Supplied,
            "k");

        var result = await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None);

        result.Outcome.Should().Be(PaymentOutcome.TransportFailure);
        result.ErrorSummary.Should().Contain("504");
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

    [Fact]
    public async Task Cancel_PostsCoreBanksOwnTransactionRequestToItsOwnEndpoint()
    {
        Uri? capturedUri = null;
        HttpMethod? capturedMethod = null;
        string? capturedBody = null;
        var handler = new StubHttpHandler(async request =>
        {
            capturedUri = request.RequestUri;
            capturedMethod = request.Method;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"transactionId":"tx-1","status":"Cancelled","processedAt":"2026-09-09T12:00:00Z"}"""),
            };
        });
        using var client = new HttpClient(handler);
        var gateway = new HttpPaymentGateway(client);

        var result = await gateway.CancelAsync(
            TopologyProfile.LoadTests,
            new PaymentCancellation("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", "tx-1"),
            CancellationToken.None);

        // Profile-independent, exactly like the outcome lookup: CoreBankAPI publishes the same
        // port under both AppHosts.
        capturedUri!.ToString().Should().Be("http://127.0.0.1:5032/api/transactions/cancel");
        capturedMethod.Should().Be(HttpMethod.Post);
        capturedBody.Should().Contain("\"FromAccount\"").And.Contain("\"TransactionId\":\"tx-1\"");
        result.Outcome.Should().Be(PaymentCancelOutcome.Cancelled);
        result.Status.Should().Be("Cancelled");
        result.ErrorSummary.Should().BeNull();
        // The bank's own clock for the outcome it just stated, kept apart from the console's.
        result.ProcessedAt.Should().Be(new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// Unreachable from the real server today — CoreBank answers a withdrawn row with <c>200</c>
    /// — but the console must never leave a payment open when the bank has stated it is not.
    /// </summary>
    [Fact]
    public async Task Cancel_409WhoseBodySaysCancelled_IsAWithdrawalNotARefusal()
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("""{"transactionId":"tx-1","status":"Cancelled"}"""),
            })));
        var gateway = new HttpPaymentGateway(client);

        var result = await gateway.CancelAsync(
            TopologyProfile.Regular,
            new PaymentCancellation("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", "tx-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(PaymentCancelOutcome.Cancelled);
        result.StatusCode.Should().Be(409);
    }

    /// <summary>
    /// A validation refusal answers with the bank's own <c>errors</c> array and no status word.
    /// Naming them is the difference between a reason the operator can act on and a bare code.
    /// </summary>
    [Fact]
    public async Task Cancel_ValidationRefusal_NamesTheBanksOwnErrorsRatherThanTheCodeAlone()
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"errors":["TransactionId is required","Amount is required"]}"""),
            })));
        var gateway = new HttpPaymentGateway(client);

        var result = await gateway.CancelAsync(
            TopologyProfile.Regular,
            new PaymentCancellation("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", "tx-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(PaymentCancelOutcome.TransportFailure);
        result.ErrorSummary.Should().Contain("400")
            .And.Contain("TransactionId is required")
            .And.Contain("Amount is required");
    }

    /// <summary>
    /// The body carries the business meaning, never the code alone: a <c>200</c> that answers
    /// with a committed status is the bank saying it was too late, not a cancellation.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.OK, "Cancelled", PaymentCancelOutcome.Cancelled)]
    [InlineData(HttpStatusCode.OK, "Completed", PaymentCancelOutcome.AlreadyCommitted)]
    [InlineData(HttpStatusCode.OK, "Failed", PaymentCancelOutcome.AlreadyCommitted)]
    [InlineData(HttpStatusCode.Conflict, "Processing", PaymentCancelOutcome.Refused)]
    [InlineData(HttpStatusCode.Conflict, "Pending", PaymentCancelOutcome.Refused)]
    [InlineData(HttpStatusCode.Conflict, "Failed", PaymentCancelOutcome.Refused)]
    [InlineData(HttpStatusCode.Conflict, "Cancelled", PaymentCancelOutcome.Cancelled)]
    public async Task Cancel_MapsTheBodysStatusWordRatherThanTheCodeAlone(
        HttpStatusCode code,
        string status,
        PaymentCancelOutcome expected)
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent($$"""{"transactionId":"tx-1","status":"{{status}}"}"""),
            })));
        var gateway = new HttpPaymentGateway(client);

        var result = await gateway.CancelAsync(
            TopologyProfile.Regular,
            new PaymentCancellation("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", "tx-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(expected);
        result.Status.Should().Be(status);
        result.StatusCode.Should().Be((int)code);
    }

    /// <summary>
    /// Anything else asserts nothing about the payment: another status code, a status word the
    /// console does not recognise, a body it cannot read, or a call that never came back.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.OK, "<html>gateway</html>")]
    [InlineData(HttpStatusCode.OK, """{"transactionId":"tx-1","status":"Elsewhere"}""")]
    [InlineData(HttpStatusCode.Conflict, """{"transactionId":"tx-1","status":"Elsewhere"}""")]
    public async Task Cancel_AnythingElse_IsATransportFailureThatLeavesThePaymentAlone(
        HttpStatusCode code,
        string body)
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) })));
        var gateway = new HttpPaymentGateway(client);

        var result = await gateway.CancelAsync(
            TopologyProfile.Regular,
            new PaymentCancellation("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", "tx-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(PaymentCancelOutcome.TransportFailure);
        result.ErrorSummary.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Cancel_UnreachableCoreBank_ReportsTheExactTransportFailure()
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            throw new HttpRequestException("connection refused")));
        var gateway = new HttpPaymentGateway(client);

        var result = await gateway.CancelAsync(
            TopologyProfile.Regular,
            new PaymentCancellation("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", "tx-1"),
            CancellationToken.None);

        result.Outcome.Should().Be(PaymentCancelOutcome.TransportFailure);
        result.StatusCode.Should().Be(0);
        result.ErrorSummary.Should().Contain("connection refused");
    }

    // --- The exchange the record carries -------------------------------------------------
    //
    // Every one of these asserts on PaymentResult.Exchange rather than on what the handler saw:
    // the point of the feature is that the call comes *home* on the result, and a test that only
    // inspects the outgoing message would pass with the exchange discarded exactly as it was.

    [Fact]
    public async Task Submit_GeneratedKey_BringsTheRequestAndTheResponseHomeOnTheResult()
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(
                    """{"paymentId":"p1","transactionId":"demo-key-001","status":"Pending"}""",
                    Encoding.UTF8,
                    "application/json"),
            })));
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Generated,
            "demo-key-001");

        var exchange = (await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None)).Exchange;

        exchange.Should().NotBeNull();
        exchange!.Method.Should().Be("POST");
        exchange.Url.Should().Be("http://127.0.0.1:5294/api/payments");
        // The one line the audience is asked to read, and the content type beside it: the
        // request headers are merged from the message and its content so both are present.
        exchange.RequestHeaders.Should().Contain(header =>
            header.Name == "Idempotency-Key" && header.Value == "demo-key-001");
        exchange.RequestHeaders.Should().Contain(header =>
            header.Name == "Content-Type" && header.Value.Contains("application/json"));
        exchange.RequestBody.Should().Contain("\"Scheme\":\"standard\"");
        exchange.StatusCode.Should().Be(202);
        exchange.ResponseHeaders.Should().Contain(header => header.Name == "Content-Type");
        exchange.ResponseBody.Should().Contain("\"transactionId\":\"demo-key-001\"");
    }

    [Fact]
    public async Task Submit_OmittedKey_RecordsNoKeyHeaderRatherThanAnExplanation()
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"paymentId":"p1","transactionId":"server-key","status":"Pending"}"""),
            })));
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Omitted,
            null);

        var exchange = (await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None)).Exchange;

        // The absence is the fact. A line saying "no key was sent" would be this console
        // interpreting the exchange instead of showing it.
        exchange!.RequestHeaders.Should().NotContain(header => header.Name == "Idempotency-Key");
        exchange.RequestHeaders.Should().Contain(header => header.Name == "Content-Type");
    }

    [Fact]
    public async Task Submit_NoAnswer_KeepsTheRequestAndLeavesTheStatusCodeNull()
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            throw new HttpRequestException("connection refused")));
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Generated,
            "demo-key-002");

        var exchange = (await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None)).Exchange;

        // A request that got no answer is still evidence, and the null status is how the record
        // says which half is missing.
        exchange.Should().NotBeNull();
        exchange!.RequestBody.Should().NotBeNullOrEmpty();
        exchange.RequestHeaders.Should().Contain(header => header.Name == "Idempotency-Key");
        exchange.StatusCode.Should().BeNull();
        exchange.ResponseBody.Should().BeNull();
    }

    [Fact]
    public async Task Cancel_BringsItsBodyHomeAndSetsNoIdempotencyKey()
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"transactionId":"tx-1","status":"Cancelled"}"""),
            })));
        var gateway = new HttpPaymentGateway(client);

        var result = await gateway.CancelAsync(
            TopologyProfile.Regular,
            new PaymentCancellation("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", "tx-1"),
            CancellationToken.None);

        result.Exchange!.Method.Should().Be("POST");
        result.Exchange.Url.Should().Be("http://127.0.0.1:5032/api/transactions/cancel");
        result.Exchange.RequestBody.Should().Contain("tx-1");
        result.Exchange.RequestHeaders.Should().NotContain(header => header.Name == "Idempotency-Key");
        result.Exchange.StatusCode.Should().Be(200);
        result.Exchange.ResponseBody.Should().Contain("Cancelled");
    }

    [Fact]
    public async Task Inspect_SendsABareRequestAndSaysSoRatherThanSynthesisingHeaders()
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"rows":[]}"""),
            })));
        var gateway = new HttpPaymentGateway(client);

        var result = await gateway.QueryOutcomeAsync(TopologyProfile.Regular, "tx-1", CancellationToken.None);

        result.Exchange!.Method.Should().Be("GET");
        result.Exchange.Url.Should().Be("http://127.0.0.1:5032/api/transactions/tx-1");
        result.Exchange.RequestHeaders.Should().BeEmpty("the call sets none, and none are invented to fill the column");
        result.Exchange.RequestBody.Should().BeNull();
        result.Exchange.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Inspect_UnresolvableEndpoint_YieldsARecordWithNoExchangeAtAll()
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var gateway = new HttpPaymentGateway(client);

        // The resolver refuses before a request ever exists, so there is nothing to record.
        var result = await gateway.QueryOutcomeAsync(TopologyProfile.Regular, "   ", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Exchange.Should().BeNull();
    }

    [Fact]
    public async Task Submit_BodyLongerThanTheBound_IsTruncatedRatherThanTakingThePaneApart()
    {
        var huge = "{\"pad\":\"" + new string('x', JournalText.MaxLength * 2) + "\"}";
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(huge) })));
        var gateway = new HttpPaymentGateway(client);
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Generated,
            "demo-key-003");

        var exchange = (await gateway.SubmitAsync(TopologyProfile.Regular, submission, CancellationToken.None)).Exchange;

        exchange!.ResponseBody!.Length.Should().Be(JournalText.MaxLength + 1);
        exchange.ResponseBody.Should().EndWith("…").And.NotContain("[redacted]");
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }
}
