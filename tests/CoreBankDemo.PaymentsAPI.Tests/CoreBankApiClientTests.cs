using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Outbox;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Polly.Timeout;
using Xunit;
using GeneratedClient = CoreBankDemo.PaymentsAPI.GeneratedClients.CoreBank.CoreBankApiKiotaClient;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// Exercises <see cref="KiotaCoreBankApiClient"/> against an in-memory
/// <see cref="HttpMessageHandler"/> (spec-5-3's code map) -- no live
/// CoreBankAPI, no network. Covers every operation's representative 2xx/4xx,
/// malformed success data, trace-header propagation (present/absent
/// activity), caller cancellation, and a transport exception distinct from
/// caller cancellation (edge-case matrix).
/// </summary>
public class CoreBankApiClientTests
{
    private const string AccountNumber = "NL91ABNA0417164300";

    [Fact]
    public async Task ValidateAccountAsync_maps_2xx_body_to_success()
    {
        using var handler = new FakeHttpMessageHandler((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be("/api/accounts/validate");
            using var body = ReadJson(request);
            body.RootElement.GetProperty("accountNumber").GetString().Should().Be(AccountNumber);
            return JsonResponse(HttpStatusCode.OK, new
            {
                accountNumber = AccountNumber,
                isValid = true,
                accountHolderName = "Jane Doe",
                balance = 123.45m
            });
        });
        var client = CreateClient(handler);

        var result = await client.ValidateAccountAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Success);
        result.Value.Should().Be(new AccountValidation(AccountNumber, true, "Jane Doe", 123.45m));
    }

    [Fact]
    public async Task ValidateAccountAsync_treats_4xx_as_retry_without_throwing()
    {
        using var handler = new FakeHttpMessageHandler((_, _) =>
            JsonResponse(HttpStatusCode.BadRequest, new { errors = new[] { "AccountNumber is required" } }));
        var client = CreateClient(handler);

        var result = await client.ValidateAccountAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.Value.Should().BeNull();
        result.RetryReason.Should().Be(CoreBankRetryReason.TransportRejection);
        result.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ValidateAccountAsync_treats_malformed_success_body_as_retry()
    {
        using var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(HttpStatusCode.OK, new { }));
        var client = CreateClient(handler);

        var result = await client.ValidateAccountAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryReason.Should().Be(CoreBankRetryReason.MalformedResponse);
        result.StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAccountAsync_treats_whitespace_only_account_number_as_retry()
    {
        // Present but blank required string data is just as malformed as a
        // missing field entirely (edge-case matrix).
        using var handler = new FakeHttpMessageHandler((_, _) =>
            JsonResponse(HttpStatusCode.OK, new { accountNumber = "   ", isValid = true }));
        var client = CreateClient(handler);

        var result = await client.ValidateAccountAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryReason.Should().Be(CoreBankRetryReason.MalformedResponse);
        result.StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task GetAccountDetailsAsync_maps_2xx_body_to_success()
    {
        var createdAt = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
        using var handler = new FakeHttpMessageHandler((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.AbsolutePath.Should().Be($"/api/accounts/{AccountNumber}");
            return JsonResponse(HttpStatusCode.OK, new
            {
                accountNumber = AccountNumber,
                accountHolderName = "Jane Doe",
                balance = 10_000_000.00m,
                currency = "EUR",
                isActive = true,
                createdAt,
                updatedAt = (DateTimeOffset?)null
            });
        });
        var client = CreateClient(handler);

        var result = await client.GetAccountDetailsAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Success);
        result.Value.Should().Be(new AccountDetails(
            AccountNumber, "Jane Doe", 10_000_000.00m, "EUR", true, createdAt, null));
    }

    [Fact]
    public async Task GetAccountDetailsAsync_treats_404_as_retry_without_throwing()
    {
        using var handler = new FakeHttpMessageHandler((_, _) =>
            JsonResponse(HttpStatusCode.NotFound, new { errors = new[] { $"Account {AccountNumber} not found" } }));
        var client = CreateClient(handler);

        var result = await client.GetAccountDetailsAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryReason.Should().Be(CoreBankRetryReason.TransportRejection);
        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetAccountDetailsAsync_treats_mismatched_account_number_as_retry()
    {
        // A response echoing back a different account than requested is
        // malformed data, not a valid success for a different account
        // (edge-case matrix), compared ordinally.
        using var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(HttpStatusCode.OK, new
        {
            accountNumber = "NL20INGB0001234567",
            accountHolderName = "Jane Doe",
            balance = 100m,
            currency = "EUR",
            isActive = true,
            createdAt = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero)
        }));
        var client = CreateClient(handler);

        var result = await client.GetAccountDetailsAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryReason.Should().Be(CoreBankRetryReason.MalformedResponse);
        result.StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task ProcessTransactionAsync_maps_202_accepted_to_success()
    {
        var processedAt = new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);
        using var handler = new FakeHttpMessageHandler((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be("/api/transactions/process");
            using var body = ReadJson(request);
            body.RootElement.GetProperty("fromAccount").GetString().Should().Be(AccountNumber);
            body.RootElement.GetProperty("toAccount").GetString().Should().Be("NL20INGB0001234567");
            body.RootElement.GetProperty("amount").GetDecimal().Should().Be(100m);
            body.RootElement.GetProperty("currency").GetString().Should().Be("EUR");
            body.RootElement.GetProperty("transactionId").GetString().Should().Be("txn-1");
            return JsonResponse(HttpStatusCode.Accepted, new
            {
                transactionId = "txn-1",
                status = "Pending",
                processedAt
            });
        });
        var client = CreateClient(handler);
        var request = new TransactionSubmissionRequest(
            AccountNumber, "NL20INGB0001234567", 100m, "EUR", "txn-1");

        var result = await client.ProcessTransactionAsync(request, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Success);
        result.Value.Should().Be(new TransactionSubmission("txn-1", "Pending", processedAt));
    }

    [Fact]
    public async Task ProcessTransactionAsync_maps_200_replayed_duplicate_to_success()
    {
        var processedAt = new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);
        using var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(HttpStatusCode.OK, new
        {
            transactionId = "txn-1",
            status = "Completed",
            processedAt
        }));
        var client = CreateClient(handler);
        var request = new TransactionSubmissionRequest(
            AccountNumber, "NL20INGB0001234567", 100m, "EUR", "txn-1");

        var result = await client.ProcessTransactionAsync(request, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Success);
        result.Value.Should().Be(new TransactionSubmission("txn-1", "Completed", processedAt));
    }

    [Fact]
    public async Task ProcessTransactionAsync_treats_400_transport_failure_as_retry_without_throwing()
    {
        using var handler = new FakeHttpMessageHandler((_, _) =>
            JsonResponse(HttpStatusCode.BadRequest, new { errors = new[] { "Transaction failed" } }));
        var client = CreateClient(handler);
        var request = new TransactionSubmissionRequest(
            AccountNumber, "NL20INGB0001234567", 100m, "EUR", "txn-1");

        var result = await client.ProcessTransactionAsync(request, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryReason.Should().Be(CoreBankRetryReason.TransportRejection);
        result.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ProcessTransactionAsync_treats_mismatched_transaction_id_as_retry()
    {
        using var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(HttpStatusCode.Accepted, new
        {
            transactionId = "txn-does-not-match",
            status = "Pending",
            processedAt = DateTimeOffset.UtcNow
        }));
        var client = CreateClient(handler);
        var request = new TransactionSubmissionRequest(
            AccountNumber, "NL20INGB0001234567", 100m, "EUR", "txn-1");

        var result = await client.ProcessTransactionAsync(request, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryReason.Should().Be(CoreBankRetryReason.MalformedResponse);
        result.StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task ProcessTransactionAsync_throws_for_null_request_instead_of_reporting_a_transport_retry()
    {
        using var handler = new FakeHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("must not be invoked for a null request"));
        var client = CreateClient(handler);

        var act = () => client.ProcessTransactionAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GetTransactionStatusAsync_maps_cached_response_shape_to_success()
    {
        var processedAt = new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);
        using var handler = new FakeHttpMessageHandler((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.AbsolutePath.Should().Be("/api/transactions/txn-1");
            return JsonResponse(HttpStatusCode.OK, new
            {
                transactionId = "txn-1",
                status = "Completed",
                processedAt
            });
        });
        var client = CreateClient(handler);

        var result = await client.GetTransactionStatusAsync("txn-1", TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Success);
        result.Value.Should().Be(new TransactionStatus("txn-1", "Completed", null, processedAt));
    }

    [Fact]
    public async Task GetTransactionStatusAsync_maps_status_snapshot_shape_to_success()
    {
        var receivedAt = new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);
        using var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(HttpStatusCode.OK, new
        {
            transactionId = "txn-1",
            status = "Pending",
            receivedAt,
            processedAt = (DateTimeOffset?)null
        }));
        var client = CreateClient(handler);

        var result = await client.GetTransactionStatusAsync("txn-1", TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Success);
        result.Value.Should().Be(new TransactionStatus("txn-1", "Pending", receivedAt, null));
    }

    [Fact]
    public async Task GetTransactionStatusAsync_treats_404_as_retry_without_throwing()
    {
        using var handler = new FakeHttpMessageHandler((_, _) =>
            JsonResponse(HttpStatusCode.NotFound, new { errors = new[] { "Transaction not found" } }));
        var client = CreateClient(handler);

        var result = await client.GetTransactionStatusAsync("txn-1", TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryReason.Should().Be(CoreBankRetryReason.TransportRejection);
        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetTransactionStatusAsync_treats_whitespace_only_status_as_retry()
    {
        using var handler = new FakeHttpMessageHandler((_, _) => JsonResponse(HttpStatusCode.OK, new
        {
            transactionId = "txn-1",
            status = "  "
        }));
        var client = CreateClient(handler);

        var result = await client.GetTransactionStatusAsync("txn-1", TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryReason.Should().Be(CoreBankRetryReason.MalformedResponse);
        result.StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task Call_treats_5xx_as_retry_without_throwing()
    {
        using var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("upstream failure")
            });
        var client = CreateClient(handler);

        var result = await client.ValidateAccountAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryReason.Should().Be(CoreBankRetryReason.TransportRejection);
        result.StatusCode.Should().Be(500);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not valid json {{{")]
    [InlineData("\"just a json string, not the expected object\"")]
    public async Task Call_preserves_http_status_even_when_mapped_error_body_is_empty_or_malformed(string errorBody)
    {
        // 400 is a mapped ErrorResponse for ValidateAccountAsync (per
        // corebank-api.json); the point of this test is that the *status
        // code* classification must not depend on that body actually
        // deserializing (frozen matrix: "preserve status/diagnostic context
        // without generated types") -- correct Content-Type so this
        // genuinely exercises a malformed *body*, not an unsupported
        // content type.
        using var handler = new FakeHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(errorBody, Encoding.UTF8, "application/json")
        });
        var client = CreateClient(handler);

        var result = await client.ValidateAccountAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryReason.Should().Be(CoreBankRetryReason.TransportRejection);
        result.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Call_treats_resilience_pipeline_timeout_as_timeout_not_transport_exception()
    {
        // Microsoft.Extensions.Http.Resilience's standard resilience
        // handler (wired for every named HttpClient, including
        // "corebank-api", via ServiceDefaults) raises this Polly type on its
        // own attempt/total-request timeout -- distinct from an
        // OperationCanceledException raised for any other reason.
        using var handler = new FakeHttpMessageHandler((_, _) =>
            throw new TimeoutRejectedException("The operation didn't complete within the allowed timeout."));
        var client = CreateClient(handler);

        var result = await client.ValidateAccountAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryReason.Should().Be(CoreBankRetryReason.Timeout);
        result.StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task Call_treats_transport_exception_as_retry_without_throwing()
    {
        using var handler = new FakeHttpMessageHandler((_, _) =>
            throw new HttpRequestException("connection reset"));
        var client = CreateClient(handler);

        var result = await client.ValidateAccountAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryReason.Should().Be(CoreBankRetryReason.TransportException);
        result.StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task Call_treats_timeout_unrelated_to_caller_token_as_retry_without_throwing()
    {
        // Simulates HttpClient's own Timeout firing: a TaskCanceledException
        // raised independently of the CancellationToken the caller passed in
        // (which here is never cancelled) must classify as Retry, not
        // propagate as cancellation (edge-case matrix).
        using var handler = new FakeHttpMessageHandler((_, _) =>
            throw new TaskCanceledException("timed out", new TimeoutException()));
        var client = CreateClient(handler);

        var result = await client.ValidateAccountAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryReason.Should().Be(CoreBankRetryReason.Timeout);
        result.StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task Call_propagates_caller_requested_cancellation_instead_of_retry()
    {
        using var handler = new FakeHttpMessageHandler((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return JsonResponse(HttpStatusCode.OK, new { });
        });
        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => client.ValidateAccountAsync(AccountNumber, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Call_propagates_current_traceparent_and_tracestate_when_activity_present()
    {
        using var activitySource = new ActivitySource(nameof(CoreBankApiClientTests));
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("test-activity");
        activity!.TraceStateString = "vendor=value";
        // Captured up front: HttpClient's own built-in instrumentation
        // starts a child "System.Net.Http.HttpRequestOut" activity around
        // the actual send (since this test's listener matches every
        // source), so Activity.Current by the time the fake handler runs is
        // no longer this activity -- the header must still carry *this*
        // activity's id, the one ConfigureTraceContext actually read.
        var expectedTraceParent = activity.Id;

        using var handler = new FakeHttpMessageHandler((request, _) =>
        {
            request.Headers.TryGetValues("traceparent", out var traceParents).Should().BeTrue();
            traceParents!.Should().ContainSingle();
            traceParents!.Single().Should().Be(expectedTraceParent, "the outgoing header must carry the " +
                "exact ambient trace context, not merely some traceparent-shaped value");
            request.Headers.TryGetValues("tracestate", out var traceStates).Should().BeTrue();
            traceStates!.Should().ContainSingle("vendor=value");
            return JsonResponse(HttpStatusCode.OK, new
            {
                accountNumber = AccountNumber,
                isValid = true
            });
        });
        var client = CreateClient(handler);

        var result = await client.ValidateAccountAsync(AccountNumber, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Success);
    }

    [Fact]
    public async Task Call_never_invents_trace_headers_without_an_ambient_activity()
    {
        var previousActivity = Activity.Current;
        try
        {
            Activity.Current = null;
            using var handler = new FakeHttpMessageHandler((request, _) =>
            {
                request.Headers.Contains("traceparent").Should().BeFalse();
                request.Headers.Contains("tracestate").Should().BeFalse();
                return JsonResponse(HttpStatusCode.OK, new
                {
                    accountNumber = AccountNumber,
                    isValid = true
                });
            });
            var client = CreateClient(handler);

            var result = await client.ValidateAccountAsync(AccountNumber, TestContext.Current.CancellationToken);

            result.Outcome.Should().Be(CoreBankClientOutcome.Success);
        }
        finally
        {
            // Restore ambient state so this test cannot pollute whichever
            // test runs next (Activity.Current is process/async-local
            // state, not scoped to this test).
            Activity.Current = previousActivity;
        }
    }

    private static KiotaCoreBankApiClient CreateClient(HttpMessageHandler handler)
    {
        // Mirrors CoreBankClientServiceCollectionExtensions' production
        // pipeline (LastResponseStatusHandler wraps the transport) so the
        // status-preservation regression test above actually exercises the
        // fix, not just the fake handler directly.
        var statusHandler = new LastResponseStatusHandler { InnerHandler = handler };
        var httpClient = new HttpClient(statusHandler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://corebank-api")
        };
        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: httpClient);
        var generatedClient = new GeneratedClient(adapter);
        return new KiotaCoreBankApiClient(generatedClient);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, object body) =>
        new(statusCode) { Content = JsonContent.Create(body) };

    /// <summary>
    /// Synchronously parses an outgoing request's JSON body for assertions.
    /// Safe to block on: the fake handler already buffers the whole request
    /// in memory (<see cref="JsonContent"/>), there is no real I/O to wait
    /// on and no synchronization context to deadlock against under xunit.
    /// </summary>
    private static JsonDocument ReadJson(HttpRequestMessage request) =>
        JsonDocument.Parse(request.Content!.ReadAsStream());

    /// <summary>
    /// Minimal in-memory <see cref="HttpMessageHandler"/> (no live service,
    /// per this file's code-map instruction): yields to the async state
    /// machine before invoking <paramref name="respond"/> so both a returned
    /// response and a thrown exception propagate exactly like a real
    /// <see cref="HttpMessageHandler"/> would.
    /// </summary>
    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return respond(request, cancellationToken);
        }
    }
}
