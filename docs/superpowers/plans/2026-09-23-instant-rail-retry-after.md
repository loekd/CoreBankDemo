# Instant Rail Honours `Retry-After` — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The instant rail's forward loop is bounded by its 7.5 s window with `MaxAttempts` as a cap, waits the server's `Retry-After` (429/503) or a jittered backoff between attempts while holding the partition lock, and skips straight to the ADR-020 cancel when a wait could not be followed by a useful attempt.

**Architecture:** `Retry-After` is parsed once, in `KiotaCoreBankApiClient`, into `CoreBankResult<T>.RetryAfter`; `HttpForwardOutboxDeliveryStrategy` carries it up through a typed `CoreBankRetryException` whose message is byte-identical to today's. A pure `InstantRetryPolicy` decides "sleep this long" or "give up" from attempt number, hint, remaining window and a jitter sample; `InstantPaymentForwardingHandler` consults it after every retryable failure, sleeps on the injected `TimeProvider`, and records a span event. Options validation relaxes to "one attempt plus the cancel must fit".

**Tech Stack:** .NET 10, xUnit v3 + AwesomeAssertions + Moq, Kiota-generated CoreBank client (`Microsoft.Kiota.Bundle` 2.1.2), `System.Net.Http.Headers.RetryConditionHeaderValue`, Dev Proxy 3.2.0.

**Spec:** `docs/superpowers/specs/2026-09-23-instant-rail-retry-after-design.md` — read it first. Decision record: `docs/adr/ADR-024-instant-rail-retry-policy.md`.

## Global Constraints

- Branch: `feature/instant-rail-retry-after` (already cut from `origin/main`; the spec and ADR are committed on it). Never commit to `main`. Push and open the PR only when asked.
- Build order (the `build` skill): `dotnet tool restore` once, then `dotnet test CoreBankDemo.UnitTests.slnf` (Docker-free). `dotnet test CoreBankDemo.IntegrationTests.slnf` needs Docker and is run once at the end (no schema change is expected).
- Run a single test class with `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~<ClassName>"`.
- TDD: write the failing test, watch it fail, then implement. Coverage ≥ 90 % per logic project (coverlet-enforced).
- Honour `Retry-After` only on `429` and `503`. Every other status ignores the header. A value that does not parse, or is negative, is treated as absent. An HTTP-date in the past is a zero wait.
- `MinUsefulAttempt` = 500 ms, backoff = `250 ms · 2^(attempt−1)` with ±50 % jitter, cap 1 000 ms — internal constants, not options. `MaxAttempts` stays a hard cap; default `2` → `3`. Validation becomes `AttemptTimeoutMilliseconds + CancelTimeoutMilliseconds ≤ BudgetMilliseconds`; the half-of-`ProcessingTimeout` guard stays.
- Sleep while holding the partition lock and the claimed row. Never sleep after the last capped attempt. Never start an attempt with less than `MinUsefulAttempt` remaining. Never start a wait that ends less than `MinUsefulAttempt` before the forward deadline.
- Never retry `400`, a `2xx` with `Status: Failed`, or a replayed `Cancelled` (AD-11, ADR-020, ADR-023). The cancel phase and the residual `202` are untouched.
- The retry exception's `Message` stays exactly `"{operation} failed: {reason} (status {code})."` (status part only when present) — the background outbox's `LastError` must not change.
- No new metric instrument or attribute value. The wait is an `ActivityEvent` named `instant_rail.retry_wait` on `Activity.Current` with tags `attempt` (int), `wait_ms` (long), `source` (`"retry-after"` | `"backoff"`), `status_code` (int, only when known).
- Do not enable Kiota's `RetryHandler` or the standard resilience handler on the `corebank-api` client. Do not add `429` to `corebank-api.json`.
- Every clock read goes through `TimeProvider`; every sleep through `Task.Delay(delay, timeProvider, ct)`. Follow the `conventions`, `messaging-patterns` and `observability` skills.
- Commit messages end with: `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`

## File Structure

| File | Responsibility |
|---|---|
| `CoreBankDemo.PaymentsAPI/Outbox/RetryAfterHeader.cs` (new) | Parse a `Retry-After` header (delta-seconds or HTTP-date) into a `TimeSpan` on a `TimeProvider`. No knowledge of status codes. |
| `CoreBankDemo.PaymentsAPI/Outbox/CoreBankApiContracts.cs` | `CoreBankResult<T>` gains `RetryAfter`; the `Retry` factory takes it. |
| `CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs` | Decides *when* the header counts (429/503) and populates `RetryAfter`. Gains a `TimeProvider`. |
| `CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs` | `TryAddSingleton(TimeProvider.System)` so the client resolves. |
| `CoreBankDemo.PaymentsAPI/Outbox/CoreBankRetryException.cs` (new) | Typed retry outcome (`RetryReason`, `StatusCode`, `RetryAfter`), message unchanged. |
| `CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs` | Throws `CoreBankRetryException` instead of a bare `InvalidOperationException`. |
| `CoreBankDemo.PaymentsAPI/Handlers/InstantRetryPolicy.cs` (new) | Pure decision: sleep or give up. Constants live here. |
| `CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs` | The loop: consults the policy, sleeps holding the lock, emits the span event. |
| `CoreBankDemo.PaymentsAPI/Models/InstantRailOptions.cs`, `InstantPaymentRailServiceCollectionExtensions.cs`, `appsettings.json` | `MaxAttempts` default 3; new validation inequality. |
| `CoreBankDemo.AppHost/devproxy/config/devproxy-errors.json` | `Retry-After` 5 → 2 on the 429 fault. |
| Tests | `tests/CoreBankDemo.PaymentsAPI.Tests/RetryAfterHeaderTests.cs` (new), `CoreBankApiClientTests.cs`, `HttpForwardOutboxDeliveryStrategyTests.cs`, `InstantRetryPolicyTests.cs` (new), `InstantPaymentForwardingHandlerTests.cs`, `InstantPaymentRailRegistrationTests.cs`. |

---

### Task 1: `RetryAfterHeader.TryParse`

**Files:**
- Create: `CoreBankDemo.PaymentsAPI/Outbox/RetryAfterHeader.cs`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/RetryAfterHeaderTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `internal static class RetryAfterHeader { public const string Name = "Retry-After"; public static bool TryParse(IDictionary<string, IEnumerable<string>>? headers, TimeProvider timeProvider, out TimeSpan retryAfter); }`

- [ ] **Step 1: Write the failing tests**

```csharp
using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Outbox;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// spec: instant-rail-retry-after -- both forms RFC 9110 allows for
/// Retry-After are honoured; anything else is "absent", never an error.
/// </summary>
public class RetryAfterHeaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, IEnumerable<string>> Headers(string name, string value) =>
        new(StringComparer.OrdinalIgnoreCase) { [name] = [value] };

    [Fact]
    public void Parses_delta_seconds()
    {
        RetryAfterHeader.TryParse(Headers("Retry-After", "2"), new FakeTimeProvider(Now), out var retryAfter)
            .Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Parses_an_http_date_relative_to_the_time_provider()
    {
        var date = Now.AddSeconds(3).ToString("r");

        RetryAfterHeader.TryParse(Headers("Retry-After", date), new FakeTimeProvider(Now), out var retryAfter)
            .Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void An_http_date_in_the_past_is_a_zero_wait()
    {
        var date = Now.AddSeconds(-30).ToString("r");

        RetryAfterHeader.TryParse(Headers("Retry-After", date), new FakeTimeProvider(Now), out var retryAfter)
            .Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Matches_the_header_name_case_insensitively()
    {
        RetryAfterHeader.TryParse(Headers("retry-after", "1"), new FakeTimeProvider(Now), out var retryAfter)
            .Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("1.5")]
    public void An_unparseable_or_negative_value_is_absent(string value)
    {
        RetryAfterHeader.TryParse(Headers("Retry-After", value), new FakeTimeProvider(Now), out _)
            .Should().BeFalse();
    }

    [Fact]
    public void A_missing_header_is_absent()
    {
        RetryAfterHeader.TryParse(Headers("Content-Type", "application/json"), new FakeTimeProvider(Now), out _)
            .Should().BeFalse();
    }

    [Fact]
    public void Null_headers_are_absent()
    {
        RetryAfterHeader.TryParse(null, new FakeTimeProvider(Now), out _).Should().BeFalse();
    }
}
```

If `Microsoft.Extensions.TimeProvider.Testing` is not yet referenced by `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankDemo.PaymentsAPI.Tests.csproj`, add `<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />` (the version is pinned centrally in `Directory.Packages.props`).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~RetryAfterHeaderTests"`
Expected: build error `The name 'RetryAfterHeader' does not exist`.

- [ ] **Step 3: Implement**

```csharp
using System.Net.Http.Headers;

namespace CoreBankDemo.PaymentsAPI.Outbox;

/// <summary>
/// Reads a <c>Retry-After</c> response header (RFC 9110 §10.2.3) into a
/// wait, on the caller's <see cref="TimeProvider"/> so an HTTP-date form is
/// relative to the same clock the instant rail's budget runs on (ADR-024).
/// Deliberately ignorant of status codes: whether the header <em>counts</em>
/// is <see cref="KiotaCoreBankApiClient"/>'s decision.
/// </summary>
internal static class RetryAfterHeader
{
    public const string Name = "Retry-After";

    /// <summary>
    /// <see langword="true"/> with a non-negative wait when the header is
    /// present and well-formed; <see langword="false"/> when it is missing,
    /// unparseable or negative (spec: treated as absent). An HTTP-date
    /// already in the past is a zero wait, not an absence.
    /// </summary>
    public static bool TryParse(
        IDictionary<string, IEnumerable<string>>? headers,
        TimeProvider timeProvider,
        out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (headers is null)
        {
            return false;
        }

        var value = headers
            .Where(header => string.Equals(header.Key, Name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value) || !RetryConditionHeaderValue.TryParse(value, out var parsed))
        {
            return false;
        }

        if (parsed.Delta is TimeSpan delta)
        {
            if (delta < TimeSpan.Zero)
            {
                return false;
            }

            retryAfter = delta;
            return true;
        }

        if (parsed.Date is DateTimeOffset date)
        {
            var until = date - timeProvider.GetUtcNow();
            retryAfter = until < TimeSpan.Zero ? TimeSpan.Zero : until;
            return true;
        }

        return false;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~RetryAfterHeaderTests"`
Expected: all 10 pass. If `"-1"` or `"1.5"` unexpectedly parses, keep the explicit `delta < TimeSpan.Zero` guard and add a `long.TryParse`-based pre-check for the delta form; the contract is the test, not the BCL parser's leniency.

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Outbox/RetryAfterHeader.cs tests/CoreBankDemo.PaymentsAPI.Tests/RetryAfterHeaderTests.cs tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankDemo.PaymentsAPI.Tests.csproj
git commit -m "feat(payments): parse Retry-After in both RFC 9110 forms on the TimeProvider

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: `CoreBankResult<T>.RetryAfter`, populated by the Kiota client for 429/503

**Files:**
- Modify: `CoreBankDemo.PaymentsAPI/Outbox/CoreBankApiContracts.cs:91-121`
- Modify: `CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs:46-48`, `:245-327`
- Modify: `CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs:26-45`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankApiClientTests.cs` (`CreateClient` at `:820`, `JsonResponse` at `:864`)

**Interfaces:**
- Consumes: `RetryAfterHeader.TryParse` (Task 1).
- Produces: `CoreBankResult<T>.RetryAfter : TimeSpan?` (null unless `Outcome == Retry` and a hint applied); `CoreBankResult<T>.Retry(CoreBankRetryReason reason, int? statusCode = null, TimeSpan? retryAfter = null)`; `KiotaCoreBankApiClient(GeneratedClient client, TimeProvider timeProvider)`.

- [ ] **Step 1: Write the failing tests** (append to `CoreBankApiClientTests`)

```csharp
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ProcessTransactionAsync_carries_Retry_After_on_a_429_or_503(HttpStatusCode status)
    {
        // ADR-024: the server's hint travels with the retry outcome; nothing
        // here waits -- the instant rail decides what to do with it.
        using var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var response = JsonResponse(status, new { errors = new[] { "throttled" } });
            response.Headers.TryAddWithoutValidation("Retry-After", "2");
            return response;
        });
        var client = CreateClient(handler);

        var result = await client.ProcessTransactionAsync(SubmissionRequest(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.StatusCode.Should().Be((int)status);
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ProcessTransactionAsync_reads_an_http_date_Retry_After_on_the_time_provider()
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        using var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var response = JsonResponse(HttpStatusCode.TooManyRequests, new { errors = new[] { "throttled" } });
            response.Headers.TryAddWithoutValidation("Retry-After", now.AddSeconds(3).ToString("r"));
            return response;
        });
        var client = CreateClient(handler, new FakeTimeProvider(now));

        var result = await client.ProcessTransactionAsync(SubmissionRequest(), TestContext.Current.CancellationToken);

        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ProcessTransactionAsync_ignores_Retry_After_on_any_other_status()
    {
        using var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var response = JsonResponse(HttpStatusCode.InternalServerError, new { errors = new[] { "boom" } });
            response.Headers.TryAddWithoutValidation("Retry-After", "2");
            return response;
        });
        var client = CreateClient(handler);

        var result = await client.ProcessTransactionAsync(SubmissionRequest(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryAfter.Should().BeNull();
    }

    [Fact]
    public async Task ProcessTransactionAsync_treats_an_unparseable_Retry_After_as_absent()
    {
        using var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var response = JsonResponse(HttpStatusCode.TooManyRequests, new { errors = new[] { "throttled" } });
            response.Headers.TryAddWithoutValidation("Retry-After", "soon");
            return response;
        });
        var client = CreateClient(handler);

        var result = await client.ProcessTransactionAsync(SubmissionRequest(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryAfter.Should().BeNull();
    }

    [Fact]
    public async Task ProcessTransactionAsync_leaves_RetryAfter_null_on_a_429_without_the_header()
    {
        using var handler = new FakeHttpMessageHandler((_, _) =>
            JsonResponse(HttpStatusCode.TooManyRequests, new { errors = new[] { "throttled" } }));
        var client = CreateClient(handler);

        var result = await client.ProcessTransactionAsync(SubmissionRequest(), TestContext.Current.CancellationToken);

        result.RetryAfter.Should().BeNull();
    }

    private static TransactionSubmissionRequest SubmissionRequest() =>
        new("NL91ABNA0417164300", "NL20INGB0001234567", 50m, "EUR", "tx-retry-after");
```

Change the existing helper so the clock can be injected, and add `using Microsoft.Extensions.Time.Testing;`:

```csharp
    private static KiotaCoreBankApiClient CreateClient(HttpMessageHandler handler, TimeProvider? timeProvider = null)
    {
        // ... existing body unchanged up to the generated client ...
        return new KiotaCoreBankApiClient(generatedClient, timeProvider ?? TimeProvider.System);
    }
```

If the file already has a helper that builds a `TransactionSubmissionRequest`, use that instead of adding `SubmissionRequest()`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~CoreBankApiClientTests"`
Expected: build errors — `'CoreBankResult<TransactionSubmission>' does not contain a definition for 'RetryAfter'` and no two-argument constructor.

- [ ] **Step 3: Implement**

`CoreBankApiContracts.cs` — replace the `CoreBankResult<T>` body:

```csharp
internal sealed record CoreBankResult<T>
    where T : class
{
    public CoreBankClientOutcome Outcome { get; }
    public T? Value { get; }
    public CoreBankRetryReason? RetryReason { get; }
    public int? StatusCode { get; }

    /// <summary>
    /// The server's <c>Retry-After</c>, when a <c>429</c> or <c>503</c>
    /// carried one (ADR-024). Only ever set on a
    /// <see cref="CoreBankClientOutcome.Retry"/>; a hint, never an
    /// instruction -- the caller's own budget decides whether to wait.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    private CoreBankResult(
        CoreBankClientOutcome outcome, T? value, CoreBankRetryReason? retryReason, int? statusCode, TimeSpan? retryAfter)
    {
        Outcome = outcome;
        Value = value;
        RetryReason = retryReason;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public static CoreBankResult<T> Success(T value) =>
        new(CoreBankClientOutcome.Success, value, retryReason: null, statusCode: null, retryAfter: null);

    public static CoreBankResult<T> Retry(CoreBankRetryReason reason, int? statusCode = null, TimeSpan? retryAfter = null) =>
        new(CoreBankClientOutcome.Retry, value: default, reason, statusCode, retryAfter);

    /// <summary>A <c>409</c> answer whose body <paramref name="value"/> is the current state CoreBankAPI reported.</summary>
    public static CoreBankResult<T> Conflict(T value) =>
        new(CoreBankClientOutcome.Conflict, value, retryReason: null, statusCode: 409, retryAfter: null);

    /// <summary>A <c>400</c> answer to a transaction submission: CoreBank's verdict, never retried (ADR-023).</summary>
    public static CoreBankResult<T> Rejected(int statusCode) =>
        new(CoreBankClientOutcome.Rejected, value: default, retryReason: null, statusCode, retryAfter: null);
}
```

Also update the `<summary>` of `CoreBankRetryReason` (line 53-55): change "Deliberately does not carry response bodies, headers, or any Kiota-generated type" to "Deliberately does not carry response bodies or any Kiota-generated type (the one header that matters, `Retry-After`, travels as `CoreBankResult{T}.RetryAfter`)".

`KiotaCoreBankApiClient.cs`:

```csharp
internal sealed class KiotaCoreBankApiClient(GeneratedClient client, TimeProvider timeProvider) : ICoreBankApiClient
{
    private const int BadRequest = 400;
    private const int TooManyRequests = 429;
    private const int ServiceUnavailable = 503;
```

Make `ExecuteAsync` and `RejectionOrRetry` instance methods (drop `static` on both) and change the `ApiException` branch:

```csharp
        catch (ApiException ex)
        {
            // Any non-2xx response -- a mapped ErrorResponse (itself an
            // ApiException) or an unmapped status. An operation may first
            // classify a specific mapped status as a non-retry outcome (the
            // cancel's 409); otherwise preserve only the status code the
            // generated client already surfaces on the base ApiException
            // type, never the generated error body -- plus, on a 429 or
            // 503, the server's Retry-After (ADR-024).
            return classifyApiException?.Invoke(ex)
                ?? RejectionOrRetry<T>(ex.ResponseStatusCode, badRequestIsVerdict, RetryAfterFor(ex));
        }
```

```csharp
    private CoreBankResult<T> RejectionOrRetry<T>(int? statusCode, bool badRequestIsVerdict, TimeSpan? retryAfter = null)
        where T : class =>
        badRequestIsVerdict && statusCode == BadRequest
            ? CoreBankResult<T>.Rejected(BadRequest)
            : CoreBankResult<T>.Retry(CoreBankRetryReason.TransportRejection, statusCode, retryAfter);

    /// <summary>
    /// The server's <c>Retry-After</c>, honoured only where RFC 9110 gives it
    /// its throttling meaning: a <c>429</c> or a <c>503</c> (ADR-024). Any
    /// other status, and any value that does not parse, is "no hint".
    /// </summary>
    private TimeSpan? RetryAfterFor(ApiException ex) =>
        ex.ResponseStatusCode is TooManyRequests or ServiceUnavailable
        && RetryAfterHeader.TryParse(ex.ResponseHeaders, timeProvider, out var retryAfter)
            ? retryAfter
            : null;
```

The two other `RejectionOrRetry` call sites (the `JsonException` and generic `Exception` branches) keep their two-argument form — there is no `ApiException` there, so no headers.

`CoreBankClientServiceCollectionExtensions.cs` — inside `AddCoreBankApiClient`, before the `AddScoped<IRequestAdapter>` line:

```csharp
        // The client reads an HTTP-date Retry-After on the same clock the
        // instant rail budgets on (ADR-024); ServiceDefaults registers the
        // system clock too, so this only matters for a bare registration.
        services.TryAddSingleton(TimeProvider.System);
```

(add `using Microsoft.Extensions.DependencyInjection.Extensions;` if missing.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~CoreBankApiClientTests|FullyQualifiedName~CoreBankClientRegistrationTests"`
Expected: all pass, including the pre-existing `ProcessTransactionAsync_keeps_every_other_failure_status_a_retry` theory. If the 429 test fails with `RetryAfter` null, inspect `ex.ResponseHeaders` in the debugger: Kiota 2.1.2 populates it for both mapped and unmapped statuses; if the key casing differs, `RetryAfterHeader` already matches case-insensitively.

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Outbox/CoreBankApiContracts.cs CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankApiClientTests.cs
git commit -m "feat(payments): a 429 or 503 retry outcome carries the server's Retry-After

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: `CoreBankRetryException` carries the hint out of the forwarder

**Files:**
- Create: `CoreBankDemo.PaymentsAPI/Outbox/CoreBankRetryException.cs`
- Modify: `CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs:267-298`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/HttpForwardOutboxDeliveryStrategyTests.cs` (existing retry-message theory at `:84-105`)

**Interfaces:**
- Consumes: `CoreBankResult<T>.RetryAfter` (Task 2).
- Produces: `internal sealed class CoreBankRetryException : InvalidOperationException { CoreBankRetryReason? RetryReason; int? StatusCode; TimeSpan? RetryAfter; }` — thrown by `ICoreBankTransactionForwarder.ForwardAsync` on every retry outcome.

- [ ] **Step 1: Write the failing tests** (append to `HttpForwardOutboxDeliveryStrategyTests`)

```csharp
    [Fact]
    public async Task ForwardAsync_throws_a_typed_retry_exception_that_carries_Retry_After()
    {
        // ADR-024: the instant rail reads the hint off the exception; the
        // background kernel keeps catching the base type and storing Message.
        var client = new FakeCoreBankApiClient
        {
            SubmitResult = CoreBankResult<TransactionSubmission>.Retry(
                CoreBankRetryReason.TransportRejection, 429, TimeSpan.FromSeconds(2))
        };
        ICoreBankTransactionForwarder strategy = new HttpForwardOutboxDeliveryStrategy(
            client, _store.Object, BusinessMetrics, TimeProvider.System, NullLogger<HttpForwardOutboxDeliveryStrategy>.Instance);

        var act = () => strategy.ForwardAsync(Message(), executeInline: true, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<CoreBankRetryException>();
        assertion.Which.RetryReason.Should().Be(CoreBankRetryReason.TransportRejection);
        assertion.Which.StatusCode.Should().Be(429);
        assertion.Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(2));
        assertion.Which.Message.Should().Be("Transaction submission failed: TransportRejection (status 429).");
    }

    [Fact]
    public async Task ForwardAsync_retry_exception_message_is_unchanged_without_a_status()
    {
        var client = new FakeCoreBankApiClient
        {
            SubmitResult = CoreBankResult<TransactionSubmission>.Retry(CoreBankRetryReason.Timeout)
        };
        ICoreBankTransactionForwarder strategy = new HttpForwardOutboxDeliveryStrategy(
            client, _store.Object, BusinessMetrics, TimeProvider.System, NullLogger<HttpForwardOutboxDeliveryStrategy>.Instance);

        var act = () => strategy.ForwardAsync(Message(), executeInline: true, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<CoreBankRetryException>();
        assertion.Which.RetryAfter.Should().BeNull();
        assertion.Which.Message.Should().Be("Transaction submission failed: Timeout.");
    }
```

Check the exact `ForwardAsync` parameter order on `ICoreBankTransactionForwarder` (`HttpForwardOutboxDeliveryStrategy.cs:25-45`) and match it.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~HttpForwardOutboxDeliveryStrategyTests"`
Expected: build error `The type or namespace name 'CoreBankRetryException' could not be found`.

- [ ] **Step 3: Implement**

`CoreBankDemo.PaymentsAPI/Outbox/CoreBankRetryException.cs`:

```csharp
namespace CoreBankDemo.PaymentsAPI.Outbox;

/// <summary>
/// A retry outcome from <see cref="ICoreBankApiClient"/> surfaced by
/// <see cref="HttpForwardOutboxDeliveryStrategy"/> (ADR-024). Still an
/// <see cref="InvalidOperationException"/> with the exact message the
/// background kernel has always stored as <c>LastError</c>, so nothing
/// downstream changes; the typed members exist for the instant rail, which
/// needs the server's <see cref="RetryAfter"/> to decide how long to wait.
/// </summary>
internal sealed class CoreBankRetryException(
    string operation,
    CoreBankRetryReason? retryReason,
    int? statusCode,
    TimeSpan? retryAfter)
    : InvalidOperationException(BuildMessage(operation, retryReason, statusCode))
{
    public CoreBankRetryReason? RetryReason { get; } = retryReason;

    /// <summary>The HTTP status CoreBankAPI actually returned, when there was one.</summary>
    public int? StatusCode { get; } = statusCode;

    /// <summary>The server's <c>Retry-After</c> on a <c>429</c>/<c>503</c>; <see langword="null"/> otherwise.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    // Byte-identical to the message the strategy built before ADR-024
    // (edge-case matrix: "status preserved in the message").
    private static string BuildMessage(string operation, CoreBankRetryReason? retryReason, int? statusCode) =>
        $"{operation} failed: {retryReason}" +
        (statusCode is int code ? $" (status {code})" : string.Empty) +
        ".";
}
```

`HttpForwardOutboxDeliveryStrategy.cs` — replace the throw at `:269-270` and delete the private `RetryOutcomeException` method at `:287-298`:

```csharp
        if (submission.Outcome != CoreBankClientOutcome.Success)
        {
            throw new CoreBankRetryException(
                "Transaction submission", submission.RetryReason, submission.StatusCode, submission.RetryAfter);
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~HttpForwardOutboxDeliveryStrategyTests"`
Expected: all pass, including the existing `DeliverAsync_throws_with_status_preserved_when_submission_is_a_retry_outcome` theory (it asserts `ThrowAsync<InvalidOperationException>()`, which a subclass satisfies).

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Outbox/CoreBankRetryException.cs CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs tests/CoreBankDemo.PaymentsAPI.Tests/HttpForwardOutboxDeliveryStrategyTests.cs
git commit -m "feat(payments): the forwarder's retry outcome is a typed exception carrying Retry-After

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: `InstantRetryPolicy` — the pure decision

**Files:**
- Create: `CoreBankDemo.PaymentsAPI/Handlers/InstantRetryPolicy.cs`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/InstantRetryPolicyTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  ```csharp
  internal enum InstantRetrySource { RetryAfter, Backoff }
  internal readonly record struct InstantRetryDecision(bool Retry, TimeSpan Wait, InstantRetrySource Source);
  internal static class InstantRetryPolicy
  {
      public static readonly TimeSpan MinUsefulAttempt = TimeSpan.FromMilliseconds(500);
      public static readonly TimeSpan BackoffBase = TimeSpan.FromMilliseconds(250);
      public static readonly TimeSpan BackoffCap = TimeSpan.FromMilliseconds(1000);
      public const double BackoffJitter = 0.5;
      public static bool CanStartAttempt(TimeSpan remaining);
      public static InstantRetryDecision AfterFailure(int attempt, int maxAttempts, TimeSpan? retryAfter, TimeSpan remaining, double jitterSample);
  }
  ```
  `attempt` is 1-based (the attempt that just failed). `jitterSample` ∈ [0, 1). `Retry == false` means "go to the cancel phase now, without sleeping".

- [ ] **Step 1: Write the failing tests**

```csharp
using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Handlers;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// spec: instant-rail-retry-after, "Retry policy". Pure arithmetic: the
/// handler supplies the clock and the random sample, this decides.
/// </summary>
public class InstantRetryPolicyTests
{
    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(7.5);

    [Fact]
    public void A_server_hint_sets_the_wait_exactly()
    {
        var decision = InstantRetryPolicy.AfterFailure(
            attempt: 1, maxAttempts: 3, retryAfter: TimeSpan.FromSeconds(2), remaining: Plenty, jitterSample: 0.99);

        decision.Should().Be(new InstantRetryDecision(true, TimeSpan.FromSeconds(2), InstantRetrySource.RetryAfter));
    }

    [Theory]
    [InlineData(1, 0.0, 125)]
    [InlineData(1, 0.5, 250)]
    [InlineData(1, 0.999, 375)]
    [InlineData(2, 0.5, 500)]
    [InlineData(3, 0.5, 1000)]
    [InlineData(4, 0.5, 1000)]
    [InlineData(4, 0.999, 1000)]
    public void Without_a_hint_the_wait_is_a_jittered_capped_exponential_backoff(int attempt, double sample, int expectedMs)
    {
        var decision = InstantRetryPolicy.AfterFailure(
            attempt, maxAttempts: 10, retryAfter: null, remaining: Plenty, jitterSample: sample);

        decision.Retry.Should().BeTrue();
        decision.Source.Should().Be(InstantRetrySource.Backoff);
        decision.Wait.TotalMilliseconds.Should().BeApproximately(expectedMs, 0.5);
    }

    [Fact]
    public void A_hint_that_leaves_no_useful_attempt_gives_up_without_sleeping()
    {
        // remaining 4.5 s, hint 5 s: 5 + 0.5 > 4.5 -> cancel now.
        var decision = InstantRetryPolicy.AfterFailure(
            attempt: 1, maxAttempts: 3, retryAfter: TimeSpan.FromSeconds(5), remaining: TimeSpan.FromSeconds(4.5), jitterSample: 0.5);

        decision.Retry.Should().BeFalse();
    }

    [Fact]
    public void A_hint_that_leaves_exactly_the_minimum_attempt_still_retries()
    {
        // remaining 2.5 s, hint 2 s: 2 + 0.5 == 2.5 -> a 500 ms attempt fits.
        var decision = InstantRetryPolicy.AfterFailure(
            attempt: 1, maxAttempts: 3, retryAfter: TimeSpan.FromSeconds(2), remaining: TimeSpan.FromSeconds(2.5), jitterSample: 0.5);

        decision.Retry.Should().BeTrue();
    }

    [Fact]
    public void A_backoff_that_leaves_no_useful_attempt_gives_up_without_sleeping()
    {
        // remaining 700 ms, backoff 250 ms: 250 + 500 > 700 -> cancel now.
        var decision = InstantRetryPolicy.AfterFailure(
            attempt: 1, maxAttempts: 3, retryAfter: null, remaining: TimeSpan.FromMilliseconds(700), jitterSample: 0.5);

        decision.Retry.Should().BeFalse();
    }

    [Fact]
    public void The_last_capped_attempt_never_sleeps()
    {
        var decision = InstantRetryPolicy.AfterFailure(
            attempt: 3, maxAttempts: 3, retryAfter: TimeSpan.FromSeconds(1), remaining: Plenty, jitterSample: 0.5);

        decision.Retry.Should().BeFalse();
    }

    [Fact]
    public void A_zero_hint_retries_at_once()
    {
        var decision = InstantRetryPolicy.AfterFailure(
            attempt: 1, maxAttempts: 3, retryAfter: TimeSpan.Zero, remaining: Plenty, jitterSample: 0.5);

        decision.Should().Be(new InstantRetryDecision(true, TimeSpan.Zero, InstantRetrySource.RetryAfter));
    }

    [Theory]
    [InlineData(499, false)]
    [InlineData(500, true)]
    [InlineData(2500, true)]
    public void An_attempt_starts_only_with_the_minimum_useful_window_left(int remainingMs, bool expected)
    {
        InstantRetryPolicy.CanStartAttempt(TimeSpan.FromMilliseconds(remainingMs)).Should().Be(expected);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~InstantRetryPolicyTests"`
Expected: build error `The type or namespace name 'InstantRetryPolicy' could not be found`.

- [ ] **Step 3: Implement**

```csharp
namespace CoreBankDemo.PaymentsAPI.Handlers;

/// <summary>Where a wait between instant-rail attempts came from (span event tag <c>source</c>).</summary>
internal enum InstantRetrySource
{
    RetryAfter,
    Backoff
}

/// <summary>
/// <see cref="Retry"/> false means "go to the cancel phase now, without
/// sleeping"; true means "sleep <see cref="Wait"/>, then attempt again".
/// </summary>
internal readonly record struct InstantRetryDecision(bool Retry, TimeSpan Wait, InstantRetrySource Source);

/// <summary>
/// The instant rail's retry arithmetic (ADR-024), kept pure so it is testable
/// without a clock, a lock or a forwarder. The window is a ceiling, not a
/// quota: a wait is made only when a useful attempt can still follow it.
/// </summary>
internal static class InstantRetryPolicy
{
    /// <summary>An attempt shorter than this is a wasted command that only widens the cancel phase's ambiguity.</summary>
    public static readonly TimeSpan MinUsefulAttempt = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan BackoffBase = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan BackoffCap = TimeSpan.FromMilliseconds(1000);

    /// <summary>±50 %: the wait is <c>base · 2^(attempt−1) · (1 ± 0.5)</c>, then capped.</summary>
    public const double BackoffJitter = 0.5;

    public static bool CanStartAttempt(TimeSpan remaining) => remaining >= MinUsefulAttempt;

    /// <param name="attempt">1-based number of the attempt that just failed.</param>
    /// <param name="retryAfter">The server's hint, when a 429/503 carried one.</param>
    /// <param name="remaining">Forward window left, measured after the failure.</param>
    /// <param name="jitterSample">A uniform sample in [0, 1); the caller owns the randomness.</param>
    public static InstantRetryDecision AfterFailure(
        int attempt, int maxAttempts, TimeSpan? retryAfter, TimeSpan remaining, double jitterSample)
    {
        if (attempt >= maxAttempts)
        {
            return GiveUp;
        }

        var (wait, source) = retryAfter is TimeSpan hint
            ? (hint, InstantRetrySource.RetryAfter)
            : (Backoff(attempt, jitterSample), InstantRetrySource.Backoff);

        return wait + MinUsefulAttempt <= remaining
            ? new InstantRetryDecision(true, wait, source)
            : GiveUp;
    }

    private static readonly InstantRetryDecision GiveUp = new(false, TimeSpan.Zero, InstantRetrySource.Backoff);

    private static TimeSpan Backoff(int attempt, double jitterSample)
    {
        var nominalMs = BackoffBase.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var jitteredMs = nominalMs * (1 - BackoffJitter + 2 * BackoffJitter * jitterSample);
        return TimeSpan.FromMilliseconds(Math.Min(jitteredMs, BackoffCap.TotalMilliseconds));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~InstantRetryPolicyTests"`
Expected: all pass. Arithmetic check for the theory rows: the multiplier is `1 − 0.5 + 2·0.5·sample`, so sample 0 → 0.5×, 0.5 → 1×, 0.999 → 1.499×; attempt 1 gives 125 / 250 / 374.75 ms, attempt 3 at 1× gives 1 000 ms (exactly the cap), attempt 4 is capped at 1 000 ms for any sample.

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Handlers/InstantRetryPolicy.cs tests/CoreBankDemo.PaymentsAPI.Tests/InstantRetryPolicyTests.cs
git commit -m "feat(payments): pure instant-rail retry policy -- hint, jittered backoff, or give up

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: The handler sleeps holding the lock and records the wait

**Files:**
- Modify: `CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs:217-306` (`ForwardUnderPartitionLockAsync`)
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/InstantPaymentForwardingHandlerTests.cs` (fixture `:24-102`, `BudgetClock` `:1034-1061`, `TestLockService` `:1063+`)

**Interfaces:**
- Consumes: `CoreBankRetryException.RetryAfter` (Task 3); `InstantRetryPolicy.CanStartAttempt`, `InstantRetryPolicy.AfterFailure`, `InstantRetryDecision`, `InstantRetrySource` (Task 4).
- Produces: span event `instant_rail.retry_wait` (`attempt`, `wait_ms`, `source`, `status_code`); `internal const string RetryWaitEventName = "instant_rail.retry_wait";` on the handler.

- [ ] **Step 1: Give `BudgetClock` a record of the delays it was asked for**

In the test file's `BudgetClock` (`:1034`), add a list and record every timer's due time. `Task.Delay(x, timeProvider)` calls `CreateTimer` with `dueTime = x`:

```csharp
    private sealed class BudgetClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        /// <summary>Every due time a Task.Delay on this clock asked for, in order — the sleeps the handler made.</summary>
        public List<TimeSpan> Delays { get; } = [];

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (Delays)
            {
                Delays.Add(dueTime);
            }

            return new ImmediateTimer(callback, state);
        }
        // ImmediateTimer unchanged
    }
```

Timers fire immediately, so the clock does not move during a sleep; tests that need elapsed time advance it from the forwarder mock, exactly as the lock tests do from `OnAttempt`.

- [ ] **Step 2: Write the failing tests** (append to `InstantPaymentForwardingHandlerTests`)

```csharp
    [Fact]
    public async Task ForwardAsync_sleeps_the_server_Retry_After_holding_the_lock_and_then_settles()
    {
        // ADR-024: a 429 with Retry-After: 2 at 1.75 s -> sleep 2 s under the
        // lock and the claim -> attempt 2 at 3.75 s with 3.75 s of window.
        var clock = new BudgetClock(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        var callCount = 0;
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(1750));
                    return Task.FromException<TransactionSubmission>(new CoreBankRetryException(
                        "Transaction submission", CoreBankRetryReason.TransportRejection, 429, TimeSpan.FromSeconds(2)));
                }

                return Task.FromResult(new TransactionSubmission(Payment.TransactionId, MessageConstants.Status.Completed, clock.GetUtcNow()));
            });
        _store.Setup(s => s.MarkAsCompletedAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        using var requestSpan = new Activity("POST api/Payments").Start();
        var handler = CreateHandler(
            new InstantRailOptions { BudgetMilliseconds = 9000, AttemptTimeoutMilliseconds = 2500, MaxAttempts = 3, CancelTimeoutMilliseconds = 1500 },
            timeProvider: clock);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Completed);
        callCount.Should().Be(2);
        clock.Delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(2));
        _lock.LockNames.Should().ContainSingle("the sleep happens inside the one lock acquisition");
        _store.Verify(s => s.MarkAsFailedWithRetryAsync(It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var waitEvent = requestSpan.Events.Should().ContainSingle(e => e.Name == InstantPaymentForwardingHandler.RetryWaitEventName).Which;
        waitEvent.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["attempt"] = 1,
            ["wait_ms"] = 2000L,
            ["source"] = "retry-after",
            ["status_code"] = 429,
        });
    }

    [Fact]
    public async Task ForwardAsync_skips_the_sleep_and_cancels_when_Retry_After_exceeds_the_window()
    {
        // 429 with Retry-After: 5 at 3.0 s: 3.0 + 5 + 0.5 > 7.5 -> no sleep,
        // straight to the ADR-020 cancel; the 504 arrives at 3.0 s, not 7.5 s.
        var clock = new BudgetClock(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                clock.Advance(TimeSpan.FromSeconds(3));
                return Task.FromException<TransactionSubmission>(new CoreBankRetryException(
                    "Transaction submission", CoreBankRetryReason.TransportRejection, 429, TimeSpan.FromSeconds(5)));
            });
        _forwarder.Setup(f => f.CancelAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CancelledSubmission());
        _store.Setup(s => s.MarkAsCancelledAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        using var requestSpan = new Activity("POST api/Payments").Start();
        var handler = CreateHandler(
            new InstantRailOptions { BudgetMilliseconds = 9000, AttemptTimeoutMilliseconds = 2500, MaxAttempts = 3, CancelTimeoutMilliseconds = 1500 },
            timeProvider: clock);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Cancelled);
        _forwarder.Verify(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()), Times.Once);
        clock.Delays.Should().BeEmpty("a wait that no useful attempt can follow is never made");
        clock.GetUtcNow().Should().Be(new DateTimeOffset(2026, 9, 23, 12, 0, 3, TimeSpan.Zero));
        requestSpan.Events.Should().NotContain(e => e.Name == InstantPaymentForwardingHandler.RetryWaitEventName);
    }

    [Fact]
    public async Task ForwardAsync_backs_off_with_jitter_between_unhinted_failures_and_then_cancels()
    {
        // Three 500s: two sleeps in [125, 375] ms and [250, 750] ms, then the cap
        // is reached and the cancel phase runs without a third sleep.
        var clock = new BudgetClock(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(1200));
                return Task.FromException<TransactionSubmission>(new CoreBankRetryException(
                    "Transaction submission", CoreBankRetryReason.TransportRejection, 500, retryAfter: null));
            });
        _forwarder.Setup(f => f.CancelAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CancelledSubmission());
        _store.Setup(s => s.MarkAsCancelledAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        using var requestSpan = new Activity("POST api/Payments").Start();
        var handler = CreateHandler(
            new InstantRailOptions { BudgetMilliseconds = 9000, AttemptTimeoutMilliseconds = 2500, MaxAttempts = 3, CancelTimeoutMilliseconds = 1500 },
            timeProvider: clock);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Cancelled);
        _forwarder.Verify(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()), Times.Exactly(3));
        clock.Delays.Should().HaveCount(2);
        clock.Delays[0].Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(125)).And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(375));
        clock.Delays[1].Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250)).And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(750));
        requestSpan.Events.Where(e => e.Name == InstantPaymentForwardingHandler.RetryWaitEventName)
            .Should().HaveCount(2)
            .And.AllSatisfy(e => e.Tags.Should().Contain(new KeyValuePair<string, object?>("source", "backoff")));
    }

    [Fact]
    public async Task ForwardAsync_does_not_start_an_attempt_with_less_than_the_minimum_useful_window()
    {
        // The first attempt burns 7.1 s of the 7.5 s window: 400 ms < 500 ms,
        // so no second attempt is started even though MaxAttempts allows one.
        var clock = new BudgetClock(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(7100));
                return Task.FromException<TransactionSubmission>(new CoreBankRetryException(
                    "Transaction submission", CoreBankRetryReason.Timeout, null, retryAfter: null));
            });
        _forwarder.Setup(f => f.CancelAsync(claimed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CancelledSubmission());
        _store.Setup(s => s.MarkAsCancelledAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var handler = CreateHandler(
            new InstantRailOptions { BudgetMilliseconds = 9000, AttemptTimeoutMilliseconds = 2500, MaxAttempts = 3, CancelTimeoutMilliseconds = 1500 },
            timeProvider: clock);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Cancelled);
        _forwarder.Verify(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()), Times.Once);
        clock.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task ForwardAsync_propagates_caller_cancellation_during_a_sleep_and_leaves_the_row_claimed()
    {
        // The client disconnects while the rail is honouring Retry-After: the
        // OperationCanceledException propagates, nothing is released or
        // cancelled, no metric is recorded -- exactly as during an attempt.
        using var cancellation = new CancellationTokenSource();
        var clock = new SleepingClock(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero), onSleep: cancellation.Cancel);
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CoreBankRetryException(
                "Transaction submission", CoreBankRetryReason.TransportRejection, 429, TimeSpan.FromSeconds(2)));
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        var handler = CreateHandler(
            new InstantRailOptions { BudgetMilliseconds = 9000, AttemptTimeoutMilliseconds = 2500, MaxAttempts = 3, CancelTimeoutMilliseconds = 1500 },
            businessMetrics,
            timeProvider: clock);

        var act = () => handler.ForwardAsync(Payment, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _forwarder.Verify(f => f.CancelAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsFailedWithRetryAsync(It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsCancelledAsync(It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        listener.Measurements.Should().NotContain(m => m.InstrumentName == BusinessMetrics.PaymentInstantDurationInstrumentName);
    }
```

Add a second test clock next to `BudgetClock` whose timers never fire on their own, so the sleep is observable and the caller's token is the only way out:

```csharp
    /// <summary>
    /// A clock whose timers never fire: a Task.Delay on it completes only through its
    /// cancellation token. <paramref name="onSleep"/> runs when the delay is created.
    /// </summary>
    private sealed class SleepingClock(DateTimeOffset start, Action onSleep) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Queued so Task.Delay has finished registering its cancellation callback.
            ThreadPool.QueueUserWorkItem(_ => onSleep());
            return new NeverTimer();
        }

        private sealed class NeverTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
```

Also update the pre-existing tests that retry with `MaxAttempts = 2` on `TimeProvider.System` so they no longer sleep real time: `ForwardAsync_retries_a_transport_failure_within_budget_and_then_succeeds` (`:500`), `ForwardAsync_cancels_through_CoreBank_when_every_attempt_fails_and_CoreBank_never_stored_it` (`:574`) and `ForwardAsync_releases_the_claim_and_defers_when_the_cancel_establishes_nothing` (`:716`) — grep the file for `MaxAttempts = 2` to catch any other. Pass `timeProvider: new BudgetClock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero))` to `CreateHandler` in each. Their assertions are unchanged (`callCount == 2`, `Times.Exactly(2)` — the cap still ends them after two attempts; the one backoff sleep between them completes instantly on `BudgetClock`). Tests with `MaxAttempts = 1` never sleep (the cap check comes first in `AfterFailure`) and need no change.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~InstantPaymentForwardingHandlerTests"`
Expected: build error `'InstantPaymentForwardingHandler' does not contain a definition for 'RetryWaitEventName'`. After adding only that constant, the expected failures are: the Retry-After test (`Delays` is empty — attempt 2 ran 0 ms later); the skip test (`Times.Once` fails — today attempt 2 runs immediately instead of going to cancel); the backoff test (`Delays` has 0 entries, not 2); the minimum-window test (today a second attempt starts with 400 ms left); the cancellation test (no sleep ever happens, so the token is never observed and the call completes instead of throwing).

- [ ] **Step 4: Implement the loop**

In `InstantPaymentForwardingHandler`, add the constant near `LocalCancelReason`:

```csharp
    /// <summary>Span event recorded once per deliberate wait between attempts (ADR-024).</summary>
    internal const string RetryWaitEventName = "instant_rail.retry_wait";
```

Replace the `for` loop in `ForwardUnderPartitionLockAsync` (`:241-306`, from `var attemptTimeout = …` through the comment before `CancelThroughCoreBankAsync`) with:

```csharp
        var attemptTimeout = TimeSpan.FromMilliseconds(opts.AttemptTimeoutMilliseconds);
        var forwardDeadline = ForwardDeadline(startedAt, opts);

        // ADR-024: the window is a ceiling, not a quota. Attempts run while a
        // useful one still fits and the cap allows; between them the loop
        // waits the server's Retry-After or a jittered backoff -- still
        // holding the partition lock and the claim, so nothing behind this
        // row in its partition can overtake it while its fate is decided.
        for (var attempt = 1; attempt <= opts.MaxAttempts; attempt++)
        {
            var remaining = forwardDeadline - timeProvider.GetUtcNow();
            if (!InstantRetryPolicy.CanStartAttempt(remaining))
            {
                break;
            }

            var thisAttemptTimeout = remaining < attemptTimeout ? remaining : attemptTimeout;
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(thisAttemptTimeout);

            TimeSpan? retryAfter = null;
            int? statusCode = null;
            try
            {
                var submission = await forwarder
                    .ForwardAsync(claimed, executeInline: true, attemptCts.Token)
                    .ConfigureAwait(false);

                // CoreBank replaying a cancellation for this command (a
                // tombstone from an earlier attempt's cancel that this side
                // never heard about) is a terminal, nothing-executed answer:
                // the row must end Cancelled, never Completed.
                if (submission.Status == MessageConstants.Status.Cancelled)
                {
                    return await ConcludeCancelledAsync(payment, claimed, submission, startedAt, CoreBankCancelReason, cancellationToken)
                        .ConfigureAwait(false);
                }

                return await ConcludeDeliveredAsync(payment, claimed, submission, startedAt, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller (not the per-attempt timeout) cancelled -- no
                // measurement is recorded solely because of cancellation, and
                // the claimed row is left exactly as claiming left it
                // (Processing); it will be naturally reclaimed once its
                // claim goes stale.
                throw;
            }
            catch (OperationCanceledException)
            {
                // Per-attempt timeout: retry while budget/attempts remain.
                logger.LogInformation(
                    "Instant rail attempt {Attempt} timed out for payment {IdempotencyKey}",
                    attempt, payment.IdempotencyKey);
            }
            catch (CoreBankRetryException ex)
            {
                // Transport failure with the transport's own diagnostics: the
                // status decides nothing here (AD-11), but a 429/503's
                // Retry-After sets how long the next wait is.
                retryAfter = ex.RetryAfter;
                statusCode = ex.StatusCode;
                logger.LogWarning(
                    ex,
                    "Instant rail attempt {Attempt} failed for payment {IdempotencyKey}",
                    attempt, payment.IdempotencyKey);
            }
            catch (Exception ex)
            {
                // Transport failure -- counts toward retry, never toward a
                // business outcome (AD-11); the existing retry policy is
                // "retry within budget/attempts, then cancel".
                logger.LogWarning(
                    ex,
                    "Instant rail attempt {Attempt} failed for payment {IdempotencyKey}",
                    attempt, payment.IdempotencyKey);
            }

            var decision = InstantRetryPolicy.AfterFailure(
                attempt,
                opts.MaxAttempts,
                retryAfter,
                forwardDeadline - timeProvider.GetUtcNow(),
                Random.Shared.NextDouble());
            if (!decision.Retry)
            {
                break;
            }

            RecordRetryWait(attempt, decision, statusCode);
            logger.LogInformation(
                "Instant rail: waiting {WaitMs} ms ({Source}) before attempt {NextAttempt} for payment {IdempotencyKey}",
                (long)decision.Wait.TotalMilliseconds, decision.Source, attempt + 1, payment.IdempotencyKey);
            await Task.Delay(decision.Wait, timeProvider, cancellationToken).ConfigureAwait(false);
        }

        // Budget or attempts exhausted, still under the partition lock (so a
        // later row in this partition/priority cannot overtake this one while
        // its fate is decided): two-phase cancel through CoreBank.
        return await CancelThroughCoreBankAsync(payment, claimed, opts, startedAt, cancellationToken).ConfigureAwait(false);
```

Add the private helper next to `ForwardDeadline`:

```csharp
    /// <summary>
    /// Marks a deliberate wait on the request span so a waterfall shows the
    /// gap was chosen, not latency (ADR-024). Tags are a closed set; the
    /// status is added only when the transport reported one.
    /// </summary>
    private static void RecordRetryWait(int attempt, InstantRetryDecision decision, int? statusCode)
    {
        var tags = new ActivityTagsCollection
        {
            ["attempt"] = attempt,
            ["wait_ms"] = (long)decision.Wait.TotalMilliseconds,
            ["source"] = decision.Source == InstantRetrySource.RetryAfter ? "retry-after" : "backoff",
        };
        if (statusCode is int code)
        {
            tags["status_code"] = code;
        }

        Activity.Current?.AddEvent(new ActivityEvent(RetryWaitEventName, tags: tags));
    }
```

Note `ForwardDeadline(startedAt, opts)` is already computed in `ForwardAsync`; recomputing it here is cheaper than threading another parameter and is what the existing code did (`:242`). Keep it.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~InstantPaymentForwardingHandlerTests"`
Expected: all pass. Watch the pre-existing tests that use `MaxAttempts = 1` or `2` on the real clock — with the cap check first in `AfterFailure`, a `MaxAttempts = 1` failure never sleeps, and the two you moved to `BudgetClock` sleep instantly.

- [ ] **Step 6: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs tests/CoreBankDemo.PaymentsAPI.Tests/InstantPaymentForwardingHandlerTests.cs
git commit -m "feat(payments): the instant rail waits Retry-After or backs off between attempts, holding the lock

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: Options — `MaxAttempts` default 3, validation relaxed to one attempt plus the cancel

**Files:**
- Modify: `CoreBankDemo.PaymentsAPI/Models/InstantRailOptions.cs:39-53`
- Modify: `CoreBankDemo.PaymentsAPI/InstantPaymentRailServiceCollectionExtensions.cs:39-46`
- Modify: `CoreBankDemo.PaymentsAPI/appsettings.json:20-26`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/InstantPaymentRailRegistrationTests.cs:24-108`

**Interfaces:**
- Consumes: nothing.
- Produces: startup validation message `"Payments:InstantRail: AttemptTimeoutMilliseconds + CancelTimeoutMilliseconds must not exceed BudgetMilliseconds."`

- [ ] **Step 1: Change the tests**

In `Default_configuration_registers_a_valid_forwarding_handler` change `options.MaxAttempts.Should().Be(2);` to `options.MaxAttempts.Should().Be(3);`.

Replace `An_over_budget_attempt_configuration_fails_startup_validation` and `An_exactly_at_budget_attempt_configuration_passes_startup_validation` with:

```csharp
    [Fact]
    public void A_single_attempt_that_does_not_fit_beside_the_cancel_allowance_fails_startup_validation()
    {
        // ADR-024: one attempt plus the cancel must fit; 8000 + 1500 > 9000.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Payments:InstantRail:BudgetMilliseconds"] = "9000",
            ["Payments:InstantRail:AttemptTimeoutMilliseconds"] = "8000",
            ["Payments:InstantRail:MaxAttempts"] = "1",
            ["Payments:InstantRail:CancelTimeoutMilliseconds"] = "1500"
        });

        var act = provider.GetRequiredService<IStartupValidator>().Validate;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*AttemptTimeoutMilliseconds + CancelTimeoutMilliseconds must not exceed BudgetMilliseconds*");
    }

    [Fact]
    public void Attempts_that_only_fit_the_budget_one_at_a_time_pass_startup_validation()
    {
        // ADR-024: the loop, not the validator, bounds the number of attempts.
        // 2500 × 4 + 1500 = 11500 > 9000 was rejected under ADR-018/020; a
        // single 2500 + 1500 = 4000 fits, so it is valid now.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Payments:InstantRail:BudgetMilliseconds"] = "9000",
            ["Payments:InstantRail:AttemptTimeoutMilliseconds"] = "2500",
            ["Payments:InstantRail:MaxAttempts"] = "4",
            ["Payments:InstantRail:CancelTimeoutMilliseconds"] = "1500"
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void An_attempt_that_exactly_fits_beside_the_cancel_allowance_passes_startup_validation()
    {
        // 7500 + 1500 = 9000: the boundary itself is valid.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Payments:InstantRail:BudgetMilliseconds"] = "9000",
            ["Payments:InstantRail:AttemptTimeoutMilliseconds"] = "7500",
            ["Payments:InstantRail:MaxAttempts"] = "3",
            ["Payments:InstantRail:CancelTimeoutMilliseconds"] = "1500"
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
    }
```

Update `A_cancel_allowance_that_pushes_the_attempts_over_budget_fails_startup_validation` (`:91`) so it still fails under the new rule: set `AttemptTimeoutMilliseconds` to `"9000"` and `CancelTimeoutMilliseconds` to `"1"` (9001 > 9000), keep its `WithMessage("*CancelTimeoutMilliseconds must not exceed BudgetMilliseconds*")`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~InstantPaymentRailRegistrationTests"`
Expected: `Default_configuration…` fails (2 ≠ 3); `Attempts_that_only_fit…` fails (old validator rejects 4 × 2500); the message assertion on the new failing test does not match the old text.

- [ ] **Step 3: Implement**

`InstantRailOptions.cs`:

```csharp
    /// <summary>
    /// Hard cap on inline attempts. The forward window, not this cap, is
    /// what normally ends the loop (ADR-024): an attempt is started only
    /// while a useful one still fits before <c>Budget - CancelTimeout</c>.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "MaxAttempts must be positive.")]
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Allowance, in milliseconds, reserved <em>inside</em>
    /// <see cref="BudgetMilliseconds"/> for the cancellation call CoreBankAPI
    /// receives once the forward phase ends without a committed outcome (spec:
    /// instant-rail-timeout-cancel). The forward phase ends at
    /// <c>Budget - CancelTimeout</c>, so a request thread is never held beyond
    /// the budget; <c>AttemptTimeoutMilliseconds + CancelTimeoutMilliseconds</c>
    /// must not exceed the budget, so at least one full attempt fits (ADR-024).
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "CancelTimeoutMilliseconds must be positive.")]
    public int CancelTimeoutMilliseconds { get; init; } = 1500;
```

`InstantPaymentRailServiceCollectionExtensions.cs` — replace the first `.Validate(...)`:

```csharp
            .Validate(
                options => (long)options.AttemptTimeoutMilliseconds + options.CancelTimeoutMilliseconds
                    <= options.BudgetMilliseconds,
                "Payments:InstantRail: AttemptTimeoutMilliseconds + CancelTimeoutMilliseconds must not exceed BudgetMilliseconds.")
```

`appsettings.json`: `"MaxAttempts": 3`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~InstantPaymentRailRegistrationTests"`
Expected: all pass, including the untouched stale-claim-window tests.

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Models/InstantRailOptions.cs CoreBankDemo.PaymentsAPI/InstantPaymentRailServiceCollectionExtensions.cs CoreBankDemo.PaymentsAPI/appsettings.json tests/CoreBankDemo.PaymentsAPI.Tests/InstantPaymentRailRegistrationTests.cs
git commit -m "feat(payments): MaxAttempts defaults to 3; validation requires one attempt plus the cancel to fit

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Dev Proxy `Retry-After: 2`, spec status, full test gate

**Files:**
- Modify: `CoreBankDemo.AppHost/devproxy/config/devproxy-errors.json:30-31`
- Modify: `docs/superpowers/specs/2026-09-23-instant-rail-retry-after-design.md:3`
- Test: the two `.slnf` tiers.

- [ ] **Step 1: Change the fault**

In `devproxy-errors.json`, the `429` entry's header `{ "name": "Retry-After", "value": "5" }` becomes `"value": "2"`. Do not touch `generated/` — it is derived at start (ADR-019). If `CoreBankDemo.AppHost.Tests` (or any test) asserts on the generated session file's content, run it: `dotnet test tests/CoreBankDemo.AppHost.Tests` (skip if that project does not exist).

- [ ] **Step 2: Mark the spec implemented**

Change line 3 of the spec from `> **Status:** Approved, not yet implemented` to `> **Status:** Implemented`.

- [ ] **Step 3: Run the unit tier with coverage**

Run: `dotnet test CoreBankDemo.UnitTests.slnf`
Expected: green; the coverlet threshold on `CoreBankDemo.PaymentsAPI` passes. If coverage dips, the usual gap is `RecordRetryWait`'s `status_code is null` branch — the backoff test in Task 5 covers `statusCode = 500`; the timeout path (`CoreBankRetryReason.Timeout`, status null) is covered by `ForwardAsync_does_not_start_an_attempt_with_less_than_the_minimum_useful_window` only if a wait happens, which it does not. If needed, add one handler test: two `Timeout` failures (advance 1 200 ms each) then success, asserting the event has no `status_code` tag.

- [ ] **Step 4: Run the integration tier**

Run: `dotnet test CoreBankDemo.IntegrationTests.slnf`
Expected: green (Docker + pinned `postgres:18.3`). No schema change, so any failure here is environmental; report it with the output rather than retrying blindly.

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.AppHost/devproxy/config/devproxy-errors.json docs/superpowers/specs/2026-09-23-instant-rail-retry-after-design.md
git commit -m "feat(devproxy): the 429 fault says Retry-After: 2 so a backed-off retry can settle in the window

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

- [ ] **Step 6: Live verification (report, do not gate)**

Only if an AppHost can run in this environment (`aspire-launch` skill; needs Dev Proxy 3.2.0 on PATH for fault arming): start `CoreBankDemo.AppHost` with fault arming on, run a DemoRunner burst, and find in Tempo a `POST api/Payments` trace with a `429` child, an `instant_rail.retry_wait` event and a later `200`. Record the trace ID in the PR description. If the environment cannot run it, say so in the hand-off — the spec's Verification section lists it as a live check, not a test-gate item.

---

## Self-review

**Spec coverage.** Retry policy → Tasks 4–5. `Retry-After` plumbing (429/503 only, both forms, unparseable/negative absent, past date zero) → Tasks 1–2. Typed exception, message byte-identical → Task 3. Options and validation, `MaxAttempts` 3 → Task 6. Span event with the four tags, no metric → Task 5. Sleep holding lock and claim → Task 5 (asserted via `LockNames` single acquisition and no release calls). Caller cancellation during sleep → Task 5. Dev Proxy 2 s → Task 7. Background outbox unchanged → Task 3 keeps the base type and message; the kernel is untouched. Never sleep after the last capped attempt → Task 4 (`attempt >= maxAttempts` first) and the backoff handler test (`Delays.Count == 2` for three attempts). No Kiota `RetryHandler`, no OpenAPI 429 → nothing in the plan adds them.

**Type consistency.** `CoreBankResult<T>.Retry(reason, statusCode, retryAfter)` (Task 2) is what Task 3's tests call. `CoreBankRetryException(operation, retryReason, statusCode, retryAfter)` (Task 3) is what Task 5's tests construct. `InstantRetryPolicy.AfterFailure(attempt, maxAttempts, retryAfter, remaining, jitterSample)` and `CanStartAttempt(remaining)` (Task 4) are what Task 5's handler calls. `InstantRetryDecision(Retry, Wait, Source)` and `InstantRetrySource.RetryAfter|Backoff` are used identically in Tasks 4 and 5. `KiotaCoreBankApiClient(client, timeProvider)` (Task 2) matches the test helper change.

**Placeholders.** None. Every code step has its code; every test step has its tests and its expected failure.
