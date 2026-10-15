# CoreBank Client Circuit Breaker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put a circuit breaker, and only a circuit breaker, back on PaymentsAPI's `corebank-api` HTTP client, enabled by default with thresholds the Dev Proxy 25 % error step trips and the 5 %/10 % steps do not, and let the instant rail answer `504 Cancelled` in milliseconds for a payment the open circuit never let out of the process.

**Architecture:** Bottom-up. A validated options record bound from `Payments:CoreBankCircuitBreaker`; one named Polly resilience handler holding one `HttpCircuitBreakerStrategyOptions` strategy, added after the existing `RemoveAllResilienceHandlers()`, with the three circuit transitions logged through a small logging helper; a new `CoreBankRetryReason.CircuitOpen` the Kiota adapter returns for `BrokenCircuitException`; a per-payment "did any attempt reach the wire" flag in the instant loop that chooses between a local cancel on the held claim and an immediate jump to the ADR-020 cancel phase; Polly's meter added to the ServiceDefaults metrics pipeline; ADR-029 and the stale-document corrections. Retry stays in the outbox kernel and the instant loop; the background outbox code does not change.

**Tech Stack:** .NET 10, Microsoft.Extensions.Http.Resilience 10.10.0, Polly.Core 8.7.0 (`Polly.CircuitBreaker.BrokenCircuitException`, `HttpCircuitBreakerStrategyOptions`), xUnit v3 + AwesomeAssertions + Moq, `Microsoft.Extensions.TimeProvider.Testing`.

**Spec:** `docs/superpowers/specs/2026-10-14-corebank-circuit-breaker-design.md`

## Global Constraints

- Branch: `feature/corebank-circuit-breaker` (already checked out, cut from `origin/main` with `--no-track`). Never commit to `main`; do not push unless asked.
- Before the first build: `dotnet tool restore` (the Kiota pre-build step needs it; `build` skill). Build with `dotnet build CoreBankDemo.Rebuild.slnf`.
- Run tests with a dead OTLP endpoint: `export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1`. Tier 1 is `dotnet test CoreBankDemo.UnitTests.slnf` (Docker-free). A filtered `dotnet test --filter` run exits 1 on the coverlet 90 % gate even when every test passes; judge filtered runs by the `Passed!`/`Failed!` summary line and the gate by the unfiltered project run.
- Exactly one strategy in the `corebank-api` pipeline: the circuit breaker. `RemoveAllResilienceHandlers()` stays, before it. No retry, timeout, hedging or rate-limiter strategy. Polly's default HTTP transient predicate is not changed (`5xx`, `408`, `429`, `HttpRequestException`, `TimeoutRejectedException` count; a caller-cancelled attempt does not).
- Options section `Payments:CoreBankCircuitBreaker`; defaults `Enabled=true`, `FailureRatio=0.2`, `MinimumThroughput=10`, `SamplingDurationSeconds=30`, `BreakDurationSeconds=10`; ranges mirror Polly's own: `FailureRatio` in `(0, 1]`, `MinimumThroughput ≥ 2`, both durations in `[0.5, 86400]` seconds. Story 3.1 pattern: `AddOptions<T>().Bind(section).ValidateDataAnnotations().Validate(...).ValidateOnStart()`.
- Resilience handler name: `corebank-api-circuit-breaker`. Polly meter name: `Polly` (verified in the pinned `Polly.Core` 8.7.0 / `Polly.Extensions` assemblies; instrument `resilience.polly.strategy.events`).
- Every existing `CoreBankRetryException` message stays byte-identical. The new case reads `Transaction submission failed: CircuitOpen.`.
- New constant `InstantPaymentForwardingHandler.CircuitOpenCancelReason = "Instant rail: CoreBank circuit open; the command never left PaymentsAPI"`.
- Cancel locally only when every attempt so far was `CircuitOpen`. Never wait after a `CircuitOpen` attempt. Never answer `504` for a payment that may have reached CoreBank. No new `BusinessMetrics` instrument or attribute value. No DemoRunner change.
- `TimeProvider` for every timestamp; `MessageConstants` for every status; XML docs on new public/internal types; ≥ 90 % line coverage on `CoreBankDemo.PaymentsAPI`.
- Commit messages end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.

## Review Focus

- **Attempt 1 times out (caller's per-attempt timer), attempt 2 meets the open circuit.** Expected: no local cancel (the command may be at CoreBank), no wait, cancel phase once. Pinned in Task 4 (`ForwardAsync_goes_straight_to_the_cancel_phase_when_the_circuit_opens_after_an_attempt_reached_the_wire`, the `Timeout` row).
- **`Enabled: false` in configuration.** Expected: the `corebank-api` handler chain holds no resilience handler at all and a call still flows end to end. Pinned in Task 2 (`Disabled_breaker_registers_no_resilience_handler`).
- **A `429` from CoreBank.** Expected: counts toward the ratio like a `503` (Polly default kept). Pinned in Task 2 (`Breaker_opens_on_a_run_of_429s`).
- **`FailureRatio: 0` in configuration.** Expected: startup fails naming the field rather than Polly throwing a `ValidationException` at the first request. Pinned in Task 1 (`Out_of_range_values_fail_startup_validation`, the `FailureRatio=0` row).
- **The persistence of a circuit-open local cancel fails.** Expected: `Deferred` (`202`), the row left `Processing` for reclaim, never `504`. Pinned in Task 4 (`ForwardAsync_defers_when_the_circuit_open_local_cancel_cannot_be_persisted`).

## File Structure

| File | Action | Responsibility |
|---|---|---|
| `CoreBankDemo.PaymentsAPI/Models/CoreBankCircuitBreakerOptions.cs` | Create | Validated options record for `Payments:CoreBankCircuitBreaker` |
| `CoreBankDemo.PaymentsAPI/CoreBankCircuitBreakerLogging.cs` | Create | Three structured log calls for the circuit transitions |
| `CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs` | Modify | Bind options; add the breaker handler after `RemoveAllResilienceHandlers()` |
| `CoreBankDemo.PaymentsAPI/Program.cs:20` | Modify | Pass `builder.Configuration` |
| `CoreBankDemo.PaymentsAPI/appsettings.json` | Modify | The new section with defaults |
| `CoreBankDemo.PaymentsAPI/Outbox/CoreBankApiContracts.cs` | Modify | `CoreBankRetryReason.CircuitOpen` |
| `CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs` | Modify | `catch (BrokenCircuitException)`; corrected timeout comment |
| `CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs` | Modify | `CircuitOpenCancelReason`, wire flag, local cancel on the held claim, no-wait break |
| `CoreBankDemo.ServiceDefaults/Extensions.cs:196` | Modify | `.AddMeter("Polly")` |
| `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankCircuitBreakerRegistrationTests.cs` | Create | Options validation matrix; handler chain enabled/disabled; real-pipeline open/half-open/close |
| `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankCircuitBreakerLoggingTests.cs` | Create | The three log calls |
| `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankClientRegistrationTests.cs` | Modify | Pass a configuration to the changed signature |
| `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankApiClientTests.cs` | Modify | `BrokenCircuitException` → `CircuitOpen` |
| `tests/CoreBankDemo.PaymentsAPI.Tests/HttpForwardOutboxDeliveryStrategyTests.cs` | Modify | Exception message for the new reason |
| `tests/CoreBankDemo.PaymentsAPI.Tests/InstantPaymentForwardingHandlerTests.cs` | Modify | The circuit-open instant scenarios |
| `docs/adr/ADR-029-corebank-client-circuit-breaker.md` | Create | The decision |
| `docs/adr/ADR-007-circuit-breaker.md`, `ADR-023…`, `ADR-024…` | Modify | One header line each |
| `README.md`, `ARCHITECTURE.md`, `demo-requests.http`, `docs/backlog.md`, the spec | Modify | Corrections; spec status |

---

### Task 1: Options record, binding, validation

**Files:**
- Create: `CoreBankDemo.PaymentsAPI/Models/CoreBankCircuitBreakerOptions.cs`
- Modify: `CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs`
- Modify: `CoreBankDemo.PaymentsAPI/Program.cs:20`
- Modify: `CoreBankDemo.PaymentsAPI/appsettings.json`
- Modify: `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankClientRegistrationTests.cs` (signature change only)
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankCircuitBreakerRegistrationTests.cs`

**Interfaces:**
- Produces: `public sealed record CoreBankCircuitBreakerOptions { const string SectionName = "Payments:CoreBankCircuitBreaker"; bool Enabled = true; double FailureRatio = 0.2; int MinimumThroughput = 10; double SamplingDurationSeconds = 30; double BreakDurationSeconds = 10; }` in namespace `CoreBankDemo.PaymentsAPI.Models`.
- Produces: `internal static IServiceCollection AddCoreBankApiClient(this IServiceCollection services, IConfiguration configuration)` (the parameterless overload is removed; it had one caller).

- [ ] **Step 1: Write the failing tests**

Create `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankCircuitBreakerRegistrationTests.cs`:

```csharp
using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// Proves <c>Payments:CoreBankCircuitBreaker</c> options validation and the
/// breaker's place in the <c>corebank-api</c> handler chain (spec:
/// corebank-circuit-breaker; ADR-029; Story 3.1 options pattern).
/// </summary>
public class CoreBankCircuitBreakerRegistrationTests
{
    [Fact]
    public void Default_configuration_is_valid_and_enabled()
    {
        using var provider = BuildProvider([]);

        provider.GetRequiredService<IStartupValidator>().Validate();
        var options = provider.GetRequiredService<IOptions<CoreBankCircuitBreakerOptions>>().Value;

        options.Enabled.Should().BeTrue();
        options.FailureRatio.Should().Be(0.2);
        options.MinimumThroughput.Should().Be(10);
        options.SamplingDurationSeconds.Should().Be(30);
        options.BreakDurationSeconds.Should().Be(10);
    }

    [Theory]
    [InlineData("FailureRatio", "0", "FailureRatio")]
    [InlineData("FailureRatio", "-0.1", "FailureRatio")]
    [InlineData("FailureRatio", "1.01", "FailureRatio")]
    [InlineData("MinimumThroughput", "1", "MinimumThroughput")]
    [InlineData("SamplingDurationSeconds", "0.4", "SamplingDurationSeconds")]
    [InlineData("SamplingDurationSeconds", "86401", "SamplingDurationSeconds")]
    [InlineData("BreakDurationSeconds", "0.4", "BreakDurationSeconds")]
    [InlineData("BreakDurationSeconds", "86401", "BreakDurationSeconds")]
    public void Out_of_range_values_fail_startup_validation(string key, string value, string namedField)
    {
        // Polly validates the same ranges when the pipeline is first built,
        // at the first request; mirroring them here moves the failure to
        // startup and names the field.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"{CoreBankCircuitBreakerOptions.SectionName}:{key}"] = value
        });

        var act = provider.GetRequiredService<IStartupValidator>().Validate;

        act.Should().Throw<OptionsValidationException>().WithMessage($"*{namedField}*");
    }

    [Theory]
    [InlineData("FailureRatio", "1")]
    [InlineData("FailureRatio", "0.01")]
    [InlineData("MinimumThroughput", "2")]
    [InlineData("SamplingDurationSeconds", "0.5")]
    [InlineData("BreakDurationSeconds", "86400")]
    public void Boundary_values_pass_startup_validation(string key, string value)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"{CoreBankCircuitBreakerOptions.SectionName}:{key}"] = value
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void Disabled_breaker_still_passes_startup_validation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"{CoreBankCircuitBreakerOptions.SectionName}:Enabled"] = "false"
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
        provider.GetRequiredService<IOptions<CoreBankCircuitBreakerOptions>>().Value.Enabled.Should().BeFalse();
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddCoreBankApiClient(Configuration(values));
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration(IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
```

In `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankClientRegistrationTests.cs` replace every `services.AddCoreBankApiClient();` (four occurrences) with `services.AddCoreBankApiClient(new ConfigurationBuilder().Build());`. The `using Microsoft.Extensions.Configuration;` is already present.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd /Users/loekd/projects/CoreBankDemo && dotnet tool restore && dotnet build tests/CoreBankDemo.PaymentsAPI.Tests 2>&1 | tail -5`
Expected: compile errors, `CoreBankCircuitBreakerOptions` not found and no overload of `AddCoreBankApiClient` taking one argument.

- [ ] **Step 3: Write the options record**

Create `CoreBankDemo.PaymentsAPI/Models/CoreBankCircuitBreakerOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace CoreBankDemo.PaymentsAPI.Models;

/// <summary>
/// Validated <c>Payments:CoreBankCircuitBreaker</c> options (ADR-029), bound
/// and validated at startup following the Story 3.1 pattern. They configure
/// the one strategy in the <c>corebank-api</c> client's resilience pipeline:
/// a Polly circuit breaker. The ranges mirror Polly's own
/// <c>CircuitBreakerStrategyOptions</c> validation so a bad value fails at
/// startup, naming the field, rather than at the first request.
/// </summary>
public sealed record CoreBankCircuitBreakerOptions
{
    public const string SectionName = "Payments:CoreBankCircuitBreaker";

    /// <summary>
    /// Whether the breaker is in the pipeline at all. When
    /// <see langword="false"/> the <c>corebank-api</c> client registers no
    /// resilience handler, exactly as before ADR-029.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Failures over calls, sampled over <see cref="SamplingDurationSeconds"/>,
    /// at or above which the circuit opens. <c>0.2</c> keeps the Regular Dev
    /// Proxy profile (5 %) and the console's 10 % step closed and lets the
    /// 25 % step open it. Must be greater than zero and at most one.
    /// </summary>
    [Range(0.0, 1.0, ErrorMessage = "FailureRatio must be between 0 (exclusive) and 1 (inclusive).")]
    public double FailureRatio { get; init; } = 0.2;

    /// <summary>
    /// Calls that must fall inside the sampling window before the ratio is
    /// evaluated. Ten per PaymentsAPI replica: a console burst of 50 at
    /// concurrency 10 is enough, one manual request never is.
    /// </summary>
    [Range(2, int.MaxValue, ErrorMessage = "MinimumThroughput must be at least 2.")]
    public int MinimumThroughput { get; init; } = 10;

    /// <summary>The window, in seconds, the failure ratio is computed over.</summary>
    [Range(0.5, 86400.0, ErrorMessage = "SamplingDurationSeconds must be between 0.5 and 86400.")]
    public double SamplingDurationSeconds { get; init; } = 30;

    /// <summary>
    /// How long, in seconds, the open circuit refuses calls before letting
    /// one probe through. Ten seconds: long enough to send a request against
    /// the open circuit on stage, short enough to close while the audience
    /// is still watching.
    /// </summary>
    [Range(0.5, 86400.0, ErrorMessage = "BreakDurationSeconds must be between 0.5 and 86400.")]
    public double BreakDurationSeconds { get; init; } = 10;
}
```

- [ ] **Step 4: Bind the options in the extension and pass configuration from Program**

In `CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs` add `using CoreBankDemo.PaymentsAPI.Models;` and `using Microsoft.Extensions.Configuration;`, change the signature, and bind the options before the `AddHttpClient` call (leave the rest of the method as it is for now; Task 2 rewrites the pipeline block):

```csharp
    internal static IServiceCollection AddCoreBankApiClient(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // ADR-029: the breaker's thresholds are configuration, validated at
        // startup (Story 3.1 pattern). FailureRatio's lower bound is exclusive
        // (Polly rejects 0), which DataAnnotations' Range cannot express.
        services.AddOptions<CoreBankCircuitBreakerOptions>()
            .Bind(configuration.GetSection(CoreBankCircuitBreakerOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.FailureRatio > 0,
                $"{CoreBankCircuitBreakerOptions.SectionName}: FailureRatio must be greater than 0.")
            .ValidateOnStart();
```

In `CoreBankDemo.PaymentsAPI/Program.cs:20` change `builder.Services.AddCoreBankApiClient();` to `builder.Services.AddCoreBankApiClient(builder.Configuration);`.

In `CoreBankDemo.PaymentsAPI/appsettings.json` add, inside `"Payments"`, after the `"InstantRail"` object:

```json
    "CoreBankCircuitBreaker": {
      "Enabled": true,
      "FailureRatio": 0.2,
      "MinimumThroughput": 10,
      "SamplingDurationSeconds": 30,
      "BreakDurationSeconds": 10
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd /Users/loekd/projects/CoreBankDemo && export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1 && dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~CoreBankCircuitBreakerRegistrationTests|FullyQualifiedName~CoreBankClientRegistrationTests" 2>&1 | grep -E "Passed!|Failed!|error"`
Expected: `Passed!` with 19 tests (15 new, 4 existing).

- [ ] **Step 6: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Models/CoreBankCircuitBreakerOptions.cs CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs CoreBankDemo.PaymentsAPI/Program.cs CoreBankDemo.PaymentsAPI/appsettings.json tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankCircuitBreakerRegistrationTests.cs tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankClientRegistrationTests.cs
git commit -m "feat(payments): validated Payments:CoreBankCircuitBreaker options

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: The breaker in the pipeline, with transition logging

**Files:**
- Create: `CoreBankDemo.PaymentsAPI/CoreBankCircuitBreakerLogging.cs`
- Modify: `CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankCircuitBreakerRegistrationTests.cs`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankCircuitBreakerLoggingTests.cs`

**Interfaces:**
- Consumes: `CoreBankCircuitBreakerOptions` (Task 1).
- Produces: `internal static class CoreBankCircuitBreakerLogging { const string CategoryName = "CoreBankDemo.PaymentsAPI.CoreBankCircuitBreaker"; const string HandlerName = "corebank-api-circuit-breaker"; static void Opened(ILogger, OnCircuitOpenedArguments<HttpResponseMessage>); static void HalfOpened(ILogger, OnCircuitHalfOpenedArguments); static void Closed(ILogger, OnCircuitClosedArguments<HttpResponseMessage>); }`.
- Produces: in the `corebank-api` handler chain, exactly one handler whose type name contains `Resilience` when enabled, none when disabled.

- [ ] **Step 1: Write the failing registration tests**

Append to `CoreBankCircuitBreakerRegistrationTests` (add `using System.Net;`, `using CoreBankDemo.PaymentsAPI.Outbox;`, `using Microsoft.Extensions.Http.Resilience;` at the top):

```csharp
    [Fact]
    public void Default_configuration_puts_exactly_one_resilience_handler_in_the_corebank_api_chain()
    {
        using var provider = BuildProvider([]);
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        var names = WalkDelegatingHandlerChain(factory.CreateHandler(CoreBankClientServiceCollectionExtensions.HttpClientName));

        names.Should().ContainSingle(name => name.Contains("Resilience"),
            "ADR-029 adds one named handler holding the breaker; the standard handler from ConfigureHttpClientDefaults stays removed");
    }

    [Fact]
    public async Task The_standard_handler_is_removed_so_no_retry_happens_underneath_the_breaker()
    {
        // With the standard handler still in the chain, each failed call would
        // be retried three times inside SendAsync (AD-11 forbids that) and its
        // own breaker (MinimumThroughput 100) would never open here.
        var calls = 0;
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"{CoreBankCircuitBreakerOptions.SectionName}:MinimumThroughput"] = "2",
            [$"{CoreBankCircuitBreakerOptions.SectionName}:FailureRatio"] = "0.5",
        }, _ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ICoreBankApiClient>();

        await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);
        await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);

        calls.Should().Be(2, "one request per call: nothing in the pipeline retries");
    }

    [Fact]
    public void Disabled_breaker_registers_no_resilience_handler()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"{CoreBankCircuitBreakerOptions.SectionName}:Enabled"] = "false"
        });
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        var names = WalkDelegatingHandlerChain(factory.CreateHandler(CoreBankClientServiceCollectionExtensions.HttpClientName));

        names.Should().NotContain(name => name.Contains("Resilience"),
            "Enabled=false must restore the pre-ADR-029 chain exactly");
        names.Should().Contain(name => name.Contains(nameof(LastResponseStatusHandler)));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Breaker_opens_after_the_minimum_throughput_of_failures_and_the_client_reports_CircuitOpen(HttpStatusCode failure)
    {
        // MinimumThroughput 2 and ratio 0.5: two failures open the circuit;
        // the third call never reaches the stub. A 429 counts like a 503
        // (Polly's default predicate, kept on purpose; spec Design Notes).
        var calls = 0;
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"{CoreBankCircuitBreakerOptions.SectionName}:MinimumThroughput"] = "2",
            [$"{CoreBankCircuitBreakerOptions.SectionName}:FailureRatio"] = "0.5",
            [$"{CoreBankCircuitBreakerOptions.SectionName}:SamplingDurationSeconds"] = "30",
            [$"{CoreBankCircuitBreakerOptions.SectionName}:BreakDurationSeconds"] = "30",
        }, _ =>
        {
            calls++;
            return new HttpResponseMessage(failure);
        });
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ICoreBankApiClient>();

        var first = await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);
        var second = await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);
        var third = await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);

        first.RetryReason.Should().Be(CoreBankRetryReason.TransportRejection);
        second.RetryReason.Should().Be(CoreBankRetryReason.TransportRejection);
        third.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        third.RetryReason.Should().Be(CoreBankRetryReason.CircuitOpen);
        third.StatusCode.Should().BeNull();
        third.RetryAfter.Should().BeNull();
        calls.Should().Be(2, "an open circuit refuses the call before the transport sees it");
    }

    [Fact]
    public async Task Breaker_half_opens_after_the_break_and_closes_on_a_successful_probe()
    {
        // The shortest break Polly allows is 500 ms; this test really waits
        // it out once. Responses: two failures (opens), then success.
        var calls = 0;
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"{CoreBankCircuitBreakerOptions.SectionName}:MinimumThroughput"] = "2",
            [$"{CoreBankCircuitBreakerOptions.SectionName}:FailureRatio"] = "0.5",
            [$"{CoreBankCircuitBreakerOptions.SectionName}:BreakDurationSeconds"] = "0.5",
        }, _ => ++calls <= 2
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = System.Net.Http.Json.JsonContent.Create(new
                {
                    accountNumber = "NL91ABNA0417164300",
                    accountHolderName = "Jane Doe",
                    balance = 100m,
                    currency = "EUR",
                    isActive = true,
                    createdAt = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero)
                })
            });
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ICoreBankApiClient>();
        await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);
        await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);
        (await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken))
            .RetryReason.Should().Be(CoreBankRetryReason.CircuitOpen);

        await Task.Delay(TimeSpan.FromMilliseconds(700), TestContext.Current.CancellationToken);
        var probe = await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);
        var afterClose = await client.GetAccountDetailsAsync("NL91ABNA0417164300", TestContext.Current.CancellationToken);

        probe.Outcome.Should().Be(CoreBankClientOutcome.Success, "the half-open probe went to the wire");
        afterClose.Outcome.Should().Be(CoreBankClientOutcome.Success, "a successful probe closes the circuit");
        calls.Should().Be(4);
    }
```

Replace the private `BuildProvider` helper with an overload-bearing pair, and add the chain walker:

```csharp
    private static ServiceProvider BuildProvider(Dictionary<string, string?> values) =>
        BuildProvider(values, _ => new HttpResponseMessage(HttpStatusCode.OK));

    private static ServiceProvider BuildProvider(
        Dictionary<string, string?> values,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var services = new ServiceCollection();
        var configuration = Configuration(values);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddServiceDiscovery();
        // The real host applies the standard resilience handler to every
        // named client through ConfigureHttpClientDefaults (ServiceDefaults);
        // registering it here proves RemoveAllResilienceHandlers strips it
        // and only the breaker added afterwards survives.
        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
        services.AddCoreBankApiClient(configuration);
        // Only the primary transport is stubbed; the whole composed chain
        // (status capture, breaker, Kiota adapter) runs for real.
        services.AddHttpClient(CoreBankClientServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHttpMessageHandler(respond));
        return services.BuildServiceProvider();
    }

    private static List<string> WalkDelegatingHandlerChain(HttpMessageHandler handler)
    {
        var names = new List<string>();
        var current = handler;
        while (current is DelegatingHandler delegating)
        {
            names.Add(delegating.GetType().FullName ?? delegating.GetType().Name);
            current = delegating.InnerHandler;
            if (current is null)
            {
                break;
            }
        }

        return names;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
```


- [ ] **Step 2: Write the failing logging tests**

Create `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankCircuitBreakerLoggingTests.cs`:

```csharp
using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Polly;
using Polly.CircuitBreaker;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// The three circuit transitions each produce one structured log line so
/// Loki shows the circuit's life cycle (ADR-029). Opened is a Warning and
/// names the break duration and the outcome that tripped it; the other two
/// are Information.
/// </summary>
public class CoreBankCircuitBreakerLoggingTests
{
    private readonly Mock<ILogger> _logger = new();

    public CoreBankCircuitBreakerLoggingTests() =>
        _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

    [Fact]
    public void Opened_logs_a_warning_with_the_break_duration_and_the_status_that_tripped_it()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var args = new OnCircuitOpenedArguments<HttpResponseMessage>(
            ResilienceContextPool.Shared.Get(), Outcome.FromResult(response), TimeSpan.FromSeconds(10), isManual: false);

        CoreBankCircuitBreakerLogging.Opened(_logger.Object, args);

        var line = SingleLine(LogLevel.Warning);
        line.Should().Contain("10000").And.Contain("503");
    }

    [Fact]
    public void Opened_names_the_exception_type_when_an_exception_tripped_it()
    {
        var args = new OnCircuitOpenedArguments<HttpResponseMessage>(
            ResilienceContextPool.Shared.Get(),
            Outcome.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")),
            TimeSpan.FromSeconds(10),
            isManual: false);

        CoreBankCircuitBreakerLogging.Opened(_logger.Object, args);

        SingleLine(LogLevel.Warning).Should().Contain(nameof(HttpRequestException));
    }

    [Fact]
    public void HalfOpened_logs_information()
    {
        CoreBankCircuitBreakerLogging.HalfOpened(_logger.Object, new OnCircuitHalfOpenedArguments(ResilienceContextPool.Shared.Get()));

        SingleLine(LogLevel.Information).Should().Contain("half-open");
    }

    [Fact]
    public void Closed_logs_information()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var args = new OnCircuitClosedArguments<HttpResponseMessage>(
            ResilienceContextPool.Shared.Get(), Outcome.FromResult(response), isManual: false);

        CoreBankCircuitBreakerLogging.Closed(_logger.Object, args);

        SingleLine(LogLevel.Information).Should().Contain("closed");
    }

    private string SingleLine(LogLevel level)
    {
        var invocation = _logger.Invocations.Should().ContainSingle(i => i.Method.Name == nameof(ILogger.Log)).Which;
        invocation.Arguments[0].Should().Be(level);
        return invocation.Arguments[2].ToString()!;
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `cd /Users/loekd/projects/CoreBankDemo && dotnet build tests/CoreBankDemo.PaymentsAPI.Tests 2>&1 | grep -E "error" | head -5`
Expected: `CoreBankCircuitBreakerLogging` not found.

- [ ] **Step 4: Write the logging helper**

Create `CoreBankDemo.PaymentsAPI/CoreBankCircuitBreakerLogging.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace CoreBankDemo.PaymentsAPI;

/// <summary>
/// Structured log lines for the <c>corebank-api</c> circuit breaker's
/// transitions (ADR-029), so Loki shows when the circuit opened, why, for how
/// long, when it probed and when it closed. Polly's own telemetry carries the
/// same transitions as metrics (meter <c>Polly</c>, instrument
/// <c>resilience.polly.strategy.events</c>); these lines are the narrative.
/// </summary>
internal static class CoreBankCircuitBreakerLogging
{
    /// <summary>Logger category; one place to filter the circuit's life cycle on.</summary>
    internal const string CategoryName = "CoreBankDemo.PaymentsAPI.CoreBankCircuitBreaker";

    /// <summary>The resilience handler's name; Polly reports it as the pipeline name on its telemetry.</summary>
    internal const string HandlerName = "corebank-api-circuit-breaker";

    internal static void Opened(ILogger logger, OnCircuitOpenedArguments<HttpResponseMessage> args)
    {
        var outcome = args.Outcome.Exception is { } exception
            ? exception.GetType().Name
            : ((int?)args.Outcome.Result?.StatusCode)?.ToString() ?? "unknown";
        logger.LogWarning(
            "CoreBank circuit opened for {BreakDurationMs} ms after {TrippingOutcome}; calls to corebank-api fail fast until the break ends (manual: {IsManual})",
            (long)args.BreakDuration.TotalMilliseconds,
            outcome,
            args.IsManual);
    }

    internal static void HalfOpened(ILogger logger, OnCircuitHalfOpenedArguments args) =>
        logger.LogInformation("CoreBank circuit half-open; the next call to corebank-api is the probe");

    internal static void Closed(ILogger logger, OnCircuitClosedArguments<HttpResponseMessage> args) =>
        logger.LogInformation("CoreBank circuit closed; calls to corebank-api flow again (manual: {IsManual})", args.IsManual);
}
```

- [ ] **Step 5: Add the handler to the pipeline**

In `CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs` add `using Microsoft.Extensions.Logging;` and `using Polly;`, then replace the block from the AD-11 comment through `#pragma warning restore EXTEXP0001` with:

```csharp
        // ServiceDefaults' ConfigureHttpClientDefaults applies AddStandardResilienceHandler()
        // (rate limiter, total timeout, retry, circuit breaker, attempt timeout) to every
        // named HttpClient, including this one. AD-11 requires all CoreBank retry decisions to
        // live in the outbox layer (the background kernel, ADR-023; the instant loop, ADR-024),
        // not the HTTP transport, so the standard handler is stripped here and never returns.
        // What this pipeline does hold (ADR-029) is one circuit breaker and nothing else: it
        // sends nothing extra, it only refuses to send while CoreBank is failing, and each
        // refusal is one more failed attempt for the layer above to classify (CircuitOpen).
        // RemoveAllResilienceHandlers is the sanctioned API for opting a single named client
        // out of ConfigureHttpClientDefaults' resilience handler; it is marked experimental
        // (EXTEXP0001) pending API review, not because its behavior is unstable.
        // Enabled is read once here, at registration, because a disabled breaker must leave
        // no handler in the chain; the thresholds are read when the pipeline is first built.
        var breakerEnabled = configuration
            .GetSection(CoreBankCircuitBreakerOptions.SectionName)
            .GetValue(nameof(CoreBankCircuitBreakerOptions.Enabled), defaultValue: true);
#pragma warning disable EXTEXP0001
        var httpClientBuilder = services.AddHttpClient(HttpClientName, client =>
            {
                client.BaseAddress = new Uri($"http://{HttpClientName}");
            })
            .AddHttpMessageHandler(() => new LastResponseStatusHandler())
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        if (breakerEnabled)
        {
            httpClientBuilder.AddResilienceHandler(CoreBankCircuitBreakerLogging.HandlerName, (pipeline, context) =>
            {
                var options = context.ServiceProvider.GetRequiredService<IOptions<CoreBankCircuitBreakerOptions>>().Value;
                var logger = context.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(CoreBankCircuitBreakerLogging.CategoryName);
                // ShouldHandle is left at Polly's HTTP default on purpose: 5xx, 408, 429,
                // HttpRequestException and TimeoutRejectedException count as failures; a
                // caller-cancelled attempt (the instant rail's per-attempt timer) does not.
                pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = options.FailureRatio,
                    MinimumThroughput = options.MinimumThroughput,
                    SamplingDuration = TimeSpan.FromSeconds(options.SamplingDurationSeconds),
                    BreakDuration = TimeSpan.FromSeconds(options.BreakDurationSeconds),
                    OnOpened = args =>
                    {
                        CoreBankCircuitBreakerLogging.Opened(logger, args);
                        return default;
                    },
                    OnHalfOpened = args =>
                    {
                        CoreBankCircuitBreakerLogging.HalfOpened(logger, args);
                        return default;
                    },
                    OnClosed = args =>
                    {
                        CoreBankCircuitBreakerLogging.Closed(logger, args);
                        return default;
                    },
                });
            });
        }

        httpClientBuilder.AddServiceDiscovery();
```

Add `using Microsoft.Extensions.Options;` too. Update the class XML doc's parenthetical "(ConfigureHttpClientDefaults ... already applies resilience/service-discovery to every client the factory creates)" to "(ConfigureHttpClientDefaults in ServiceDefaults applies the standard resilience handler and service discovery to every other client; this one keeps service discovery and replaces the standard handler with a lone circuit breaker, ADR-029)".

The `third.RetryReason.Should().Be(CoreBankRetryReason.CircuitOpen)` assertions in Step 1 cannot pass until Task 3 adds the reason. Run the rest now and expect those two tests to fail only on that assertion.

- [ ] **Step 6: Run the tests**

Run: `cd /Users/loekd/projects/CoreBankDemo && export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1 && dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~CoreBankCircuitBreaker" 2>&1 | grep -E "Passed!|Failed!|error|Failed "`
Expected: logging tests, chain tests and validation tests pass; `Breaker_opens_after…` (3 rows) and `Breaker_half_opens…` fail on `RetryReason` being `TransportException` rather than `CircuitOpen` (the `calls` counts already match). If anything else fails, fix it before moving on.

- [ ] **Step 7: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/CoreBankCircuitBreakerLogging.cs CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankCircuitBreakerRegistrationTests.cs tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankCircuitBreakerLoggingTests.cs
git commit -m "feat(payments): one circuit breaker on the corebank-api client, transitions logged

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: `CircuitOpen` retry reason in the Kiota adapter

**Files:**
- Modify: `CoreBankDemo.PaymentsAPI/Outbox/CoreBankApiContracts.cs:58-72`
- Modify: `CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs:287-304`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankApiClientTests.cs`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/HttpForwardOutboxDeliveryStrategyTests.cs`

**Interfaces:**
- Produces: `CoreBankRetryReason.CircuitOpen` (fifth member, appended last).
- Produces: `KiotaCoreBankApiClient` returns `CoreBankResult<T>.Retry(CoreBankRetryReason.CircuitOpen)` for `Polly.CircuitBreaker.BrokenCircuitException`, `StatusCode` null, `RetryAfter` null.

- [ ] **Step 1: Write the failing tests**

In `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankApiClientTests.cs` add `using Polly.CircuitBreaker;` and, after `Call_treats_resilience_pipeline_timeout_as_timeout_not_transport_exception`:

```csharp
    [Fact]
    public async Task Call_treats_an_open_circuit_as_CircuitOpen_with_no_status_and_no_retry_after()
    {
        // The corebank-api pipeline's circuit breaker (ADR-029) throws this
        // before any socket is touched. Distinct from TransportException so
        // the instant rail can prove the command never left PaymentsAPI.
        using var handler = new FakeHttpMessageHandler((_, _) =>
            throw new BrokenCircuitException("The circuit is now open and is not allowing calls."));
        var client = CreateClient(handler);

        var result = await client.ProcessTransactionAsync(
            new TransactionSubmissionRequest("NL91ABNA0417164300", "NL20INGB0001234567", 10m, "EUR", "tx-1"),
            TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(CoreBankClientOutcome.Retry);
        result.RetryReason.Should().Be(CoreBankRetryReason.CircuitOpen);
        result.StatusCode.Should().BeNull();
        result.RetryAfter.Should().BeNull();
    }
```

Check `ProcessTransactionAsync`'s parameter list in `ICoreBankApiClient` (`grep -n "ProcessTransactionAsync" CoreBankDemo.PaymentsAPI/Outbox/ICoreBankApiClient.cs`) and adapt the call if it takes extra arguments; any operation is acceptable, the classification is shared.

In `tests/CoreBankDemo.PaymentsAPI.Tests/HttpForwardOutboxDeliveryStrategyTests.cs` find the existing test that asserts a `CoreBankRetryException` message for a retry outcome (`grep -n "failed: " tests/CoreBankDemo.PaymentsAPI.Tests/HttpForwardOutboxDeliveryStrategyTests.cs`) and add beside it a test whose mocked `ICoreBankApiClient.ProcessTransactionAsync` returns `CoreBankResult<TransactionSubmission>.Retry(CoreBankRetryReason.CircuitOpen)` and which asserts:

```csharp
        var act = () => strategy.ForwardAsync(message, executeInline: false, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<CoreBankRetryException>())
            .Which.Should().Match<CoreBankRetryException>(ex =>
                ex.Message == "Transaction submission failed: CircuitOpen."
                && ex.RetryReason == CoreBankRetryReason.CircuitOpen
                && ex.StatusCode == null
                && ex.RetryAfter == null);
```

Build the `message` and `strategy` exactly as the neighbouring test does (copy its arrange block).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd /Users/loekd/projects/CoreBankDemo && dotnet build tests/CoreBankDemo.PaymentsAPI.Tests 2>&1 | grep -E "error" | head -3`
Expected: `CircuitOpen` is not a member of `CoreBankRetryReason`.

- [ ] **Step 3: Add the reason and the catch**

In `CoreBankDemo.PaymentsAPI/Outbox/CoreBankApiContracts.cs`, in `enum CoreBankRetryReason`, after `TransportException`:

```csharp
    /// <summary>
    /// The client's circuit breaker rejected the call (ADR-029); nothing was
    /// sent. The one retry reason that proves the command never left
    /// PaymentsAPI.
    /// </summary>
    CircuitOpen
```

and change the enum's summary sentence "only enough shape for a caller to distinguish the four transport-failure classes" to "five transport-failure classes".

In `CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs` add `using Polly.CircuitBreaker;`, replace the `catch (TimeoutRejectedException)` comment with:

```csharp
        catch (TimeoutRejectedException)
        {
            // Polly's timeout strategy raises this type. The corebank-api
            // pipeline holds no timeout strategy (only a circuit breaker,
            // ADR-029), so today this branch is reachable only from a test or
            // if a timeout strategy is ever added; it is kept so that such a
            // timeout is classified as Timeout rather than falling through to
            // the generic TransportException below.
            return CoreBankResult<T>.Retry(CoreBankRetryReason.Timeout);
        }
```

and insert, immediately before `catch (Exception)`:

```csharp
        catch (BrokenCircuitException)
        {
            // The corebank-api pipeline's circuit breaker refused the call
            // (ADR-029). No request was sent, so there is no status and no
            // Retry-After; the instant rail reads this reason as proof the
            // command never left PaymentsAPI.
            return CoreBankResult<T>.Retry(CoreBankRetryReason.CircuitOpen);
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd /Users/loekd/projects/CoreBankDemo && export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1 && dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~CoreBankApiClientTests|FullyQualifiedName~HttpForwardOutboxDeliveryStrategyTests|FullyQualifiedName~CoreBankCircuitBreaker" 2>&1 | grep -E "Passed!|Failed!|error|Failed "`
Expected: `Passed!`, including the two Task 2 pipeline tests that were waiting on this reason.

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Outbox/CoreBankApiContracts.cs CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankApiClientTests.cs tests/CoreBankDemo.PaymentsAPI.Tests/HttpForwardOutboxDeliveryStrategyTests.cs
git commit -m "feat(payments): classify BrokenCircuitException as CoreBankRetryReason.CircuitOpen

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: The instant rail on an open circuit

**Files:**
- Modify: `CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs:83-84,309-399`
- Test: `tests/CoreBankDemo.PaymentsAPI.Tests/InstantPaymentForwardingHandlerTests.cs`

**Interfaces:**
- Consumes: `CoreBankRetryException.RetryReason == CoreBankRetryReason.CircuitOpen` (Task 3).
- Produces: `internal const string CircuitOpenCancelReason = "Instant rail: CoreBank circuit open; the command never left PaymentsAPI"`.
- Behaviour: every attempt `CircuitOpen` so far → `repository.CancelLocallyAsync(claimed, CircuitOpenCancelReason)` on the loop's own claim, result `Cancelled`; otherwise `break` to `CancelThroughCoreBankAsync` with no `Task.Delay` and no `instant_rail.retry_wait` event.

- [ ] **Step 1: Write the failing tests**

Add to `InstantPaymentForwardingHandlerTests` (the file already imports everything needed; `CoreBankRetryException` is in `CoreBankDemo.PaymentsAPI.Outbox`):

```csharp
    private static CoreBankRetryException CircuitOpen() =>
        new("Transaction submission", CoreBankRetryReason.CircuitOpen, statusCode: null, retryAfter: null);

    [Fact]
    public async Task ForwardAsync_cancels_locally_at_once_when_the_circuit_refuses_the_first_attempt()
    {
        // ADR-029: a BrokenCircuitException is thrown before any socket is
        // touched, so the command provably never left PaymentsAPI; the row
        // the loop already holds is cancelled in place and the caller gets
        // 504 in milliseconds -- no wait, no cancel call to CoreBank.
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>())).ThrowsAsync(CircuitOpen());
        _repository.Setup(r => r.CancelLocallyAsync(claimed, InstantPaymentForwardingHandler.CircuitOpenCancelReason, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var clock = new BudgetClock(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var businessMetrics = new BusinessMetrics();
        using var listener = new MetricsTestListener(businessMetrics);
        using var requestSpan = new Activity("POST api/Payments").Start();
        var handler = CreateHandler(new InstantRailOptions { MaxAttempts = 3 }, businessMetrics, clock);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Cancelled);
        _forwarder.Verify(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()), Times.Once);
        _forwarder.Verify(f => f.CancelAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(r => r.CancelLocallyAsync(claimed, InstantPaymentForwardingHandler.CircuitOpenCancelReason, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsCancelledAsync(It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.TryClaimByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never,
            "the loop cancels the claim it already holds; re-claiming would lose it");
        clock.Delays.Should().BeEmpty("nothing is waited for after a CircuitOpen attempt");
        requestSpan.Events.Should().NotContain(e => e.Name == InstantPaymentForwardingHandler.RetryWaitEventName);
        requestSpan.TagObjects.Should().Contain(
            new KeyValuePair<string, object?>(FailedPaymentTags.FailureReason, InstantPaymentForwardingHandler.CircuitOpenCancelReason));
        claimed.ResponsePayload.Should().Contain(MessageConstants.Status.Cancelled);
        ShouldHaveCountedOneCancelledItem(listener);
        _lock.LockNames.Should().ContainSingle();
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("rejection")]
    [InlineData("exception")]
    public async Task ForwardAsync_goes_straight_to_the_cancel_phase_when_the_circuit_opens_after_an_attempt_reached_the_wire(string firstFailure)
    {
        // Attempt 1 went to the wire (and may be at CoreBank); attempt 2 met
        // the open circuit. Only CoreBank can say, so the ADR-020 cancel
        // phase runs -- but without the wait a retry would have taken, and
        // without a third attempt.
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        var calls = 0;
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>()))
            .Returns<OutboxMessage, bool, CancellationToken>((_, _, _) =>
            {
                calls++;
                if (calls > 1)
                {
                    throw CircuitOpen();
                }

                throw firstFailure switch
                {
                    "timeout" => new OperationCanceledException(),
                    "rejection" => new CoreBankRetryException("Transaction submission", CoreBankRetryReason.TransportRejection, 500, null),
                    _ => new InvalidOperationException("transport failure"),
                };
            });
        _forwarder.Setup(f => f.CancelAsync(claimed, It.IsAny<CancellationToken>())).ReturnsAsync((TransactionSubmission?)null);
        _store.Setup(s => s.MarkAsFailedWithRetryAsync(claimed, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageTransitionOutcome.Applied);
        var clock = new BudgetClock(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        using var requestSpan = new Activity("POST api/Payments").Start();
        var handler = CreateHandler(new InstantRailOptions { MaxAttempts = 3 }, timeProvider: clock);

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred, "a cancel that meets the open circuit establishes nothing");
        calls.Should().Be(2, "a CircuitOpen attempt ends the forward phase; no third attempt");
        _forwarder.Verify(f => f.CancelAsync(claimed, It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.CancelLocallyAsync(It.IsAny<OutboxMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "attempt 1 may have reached CoreBank, so a local cancel would be a lie");
        clock.Delays.Should().ContainSingle("one wait after attempt 1, none after the CircuitOpen attempt");
        requestSpan.Events.Where(e => e.Name == InstantPaymentForwardingHandler.RetryWaitEventName).Should().HaveCount(1);
    }

    [Fact]
    public async Task ForwardAsync_defers_when_the_circuit_open_local_cancel_cannot_be_persisted()
    {
        var claimed = ClaimedMessage();
        _store.Setup(s => s.TryClaimByIdIfOldestAsync(Payment.Id, Payment.PartitionId, It.IsAny<CancellationToken>())).ReturnsAsync(claimed);
        _forwarder.Setup(f => f.ForwardAsync(claimed, true, It.IsAny<CancellationToken>())).ThrowsAsync(CircuitOpen());
        _repository.Setup(r => r.CancelLocallyAsync(claimed, InstantPaymentForwardingHandler.CircuitOpenCancelReason, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var handler = CreateHandler(new InstantRailOptions { MaxAttempts = 3 });

        var result = await handler.ForwardAsync(Payment, TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(InstantDeliveryOutcome.Deferred, "the honest unknown, never 504, when the cancel is not provably persisted");
        _forwarder.Verify(f => f.CancelAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }
```

If `TestLockService.ExecuteWithLockAsync` is not what releases the lock (check its body: it runs `workload` then returns `!LoseOwnershipAfterWorkload`), `_lock.LockNames.Should().ContainSingle()` proves the whole conclusion ran inside one lock acquisition; keep it.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd /Users/loekd/projects/CoreBankDemo && dotnet build tests/CoreBankDemo.PaymentsAPI.Tests 2>&1 | grep -E "error" | head -3`
Expected: `CircuitOpenCancelReason` not found.

- [ ] **Step 3: Implement**

In `CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs`, after the `CoreBankCancelReason` constant (line 84):

```csharp
    /// <summary>
    /// ADR-029: every attempt was refused by the client's circuit breaker, so
    /// no request ever left this process; the claimed row is cancelled in
    /// place, with no network call.
    /// </summary>
    internal const string CircuitOpenCancelReason = "Instant rail: CoreBank circuit open; the command never left PaymentsAPI";
```

Before the `for` loop (just above the ADR-024 comment at line 304):

```csharp
        // ADR-029: an attempt the circuit breaker refused never touched a
        // socket. While that is true of every attempt so far, the command
        // provably never left PaymentsAPI and may be cancelled locally; once
        // any attempt has gone to the wire, only CoreBank can say where the
        // command is, and the ADR-020 cancel phase decides.
        var anyAttemptReachedWire = false;
```

In the per-attempt-timeout catch (`catch (OperationCanceledException)` without a filter), add `anyAttemptReachedWire = true;` as its first statement. In `catch (Exception ex)` add `anyAttemptReachedWire = true;` as its first statement. Replace the `catch (CoreBankRetryException ex)` block with:

```csharp
            catch (CoreBankRetryException ex) when (ex.RetryReason == CoreBankRetryReason.CircuitOpen)
            {
                logger.LogWarning(
                    "Instant rail attempt {Attempt} for payment {IdempotencyKey} was refused by the open CoreBank circuit",
                    attempt, payment.IdempotencyKey);
                if (!anyAttemptReachedWire)
                {
                    // Cancel the claim this loop already holds; CancelLocallyAsync
                    // re-claims by id and would lose it.
                    var cancelledAt = timeProvider.GetUtcNow();
                    var cancelledSubmission = new TransactionSubmission(claimed.TransactionId, MessageConstants.Status.Cancelled, cancelledAt);
                    claimed.ResponsePayload = JsonSerializer.Serialize(cancelledSubmission);
                    return await ConcludeCancelledAsync(
                            payment, claimed, cancelledSubmission, startedAt, CircuitOpenCancelReason, neverForwarded: true, cancellationToken)
                        .ConfigureAwait(false);
                }

                // An earlier attempt reached the wire: no wait (the circuit
                // will not close within the window), straight to the cancel
                // phase, which asks CoreBank.
                break;
            }
            catch (CoreBankRetryException ex)
            {
                // Transport failure with the transport's own diagnostics: the
                // status decides nothing here (AD-11), but a 429/503's
                // Retry-After sets how long the next wait is.
                anyAttemptReachedWire = true;
                retryAfter = ex.RetryAfter;
                statusCode = ex.StatusCode;
                logger.LogWarning(
                    ex,
                    "Instant rail attempt {Attempt} failed for payment {IdempotencyKey}",
                    attempt, payment.IdempotencyKey);
            }
```

`CoreBankRetryReason` is in `CoreBankDemo.PaymentsAPI.Outbox`, already imported. Update the comment above `CancelThroughCoreBankAsync`'s call site ("Budget or attempts exhausted") to "Budget or attempts exhausted, or the circuit opened after an attempt reached the wire (ADR-029)".

- [ ] **Step 4: Run the handler tests**

Run: `cd /Users/loekd/projects/CoreBankDemo && export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1 && dotnet test tests/CoreBankDemo.PaymentsAPI.Tests --filter "FullyQualifiedName~InstantPaymentForwardingHandlerTests" 2>&1 | grep -E "Passed!|Failed!|error|Failed "`
Expected: `Passed!` with the three new tests (five rows) and every existing test green.

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs tests/CoreBankDemo.PaymentsAPI.Tests/InstantPaymentForwardingHandlerTests.cs
git commit -m "feat(payments): instant rail cancels locally when every attempt met the open circuit

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: Polly's meter in ServiceDefaults, full unit tier, coverage gate

**Files:**
- Modify: `CoreBankDemo.ServiceDefaults/Extensions.cs:196`

- [ ] **Step 1: Add the meter**

In `CoreBankDemo.ServiceDefaults/Extensions.cs` change

```csharp
                        .AddMeter(BusinessMetrics.MeterName)
```

to

```csharp
                        .AddMeter(BusinessMetrics.MeterName)
                        // Polly's resilience telemetry (meter "Polly": resilience.polly.strategy.events
                        // and the duration histograms) so the corebank-api circuit breaker's
                        // open/half-open/close events reach Prometheus (ADR-029).
                        .AddMeter("Polly")
```

No unit test: the metrics pipeline is hosting boilerplate the existing ServiceDefaults tests do not introspect, and the meter name was verified against the pinned assembly (Global Constraints).

- [ ] **Step 2: Run the whole unit tier with the coverage gate**

Run: `cd /Users/loekd/projects/CoreBankDemo && export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1 && dotnet test CoreBankDemo.UnitTests.slnf 2>&1 | grep -E "Passed!|Failed!|error|Total|Line|threshold" | head -40`
Expected: every project `Passed!`; no coverlet threshold error for `CoreBankDemo.PaymentsAPI` or `CoreBankDemo.ServiceDefaults`. If PaymentsAPI's line coverage dips under 90 %, the uncovered lines are in `CoreBankClientServiceCollectionExtensions` (the `OnHalfOpened`/`OnClosed` lambdas when the half-open test did not run) or the handler's new branches; add the missing scenario rather than excluding code.

- [ ] **Step 3: Run the integration tier (Docker required)**

Run: `cd /Users/loekd/projects/CoreBankDemo && export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1 && docker info >/dev/null 2>&1 && dotnet test CoreBankDemo.IntegrationTests.slnf 2>&1 | grep -E "Passed!|Failed!|error" || echo "DOCKER UNAVAILABLE: report this, do not claim the tier passed"`
Expected: `Passed!` (no schema change), or an explicit note in the final report that Docker was unavailable.

- [ ] **Step 4: Commit**

```bash
git add CoreBankDemo.ServiceDefaults/Extensions.cs
git commit -m "feat(servicedefaults): export Polly's resilience meter

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: ADR-029 and the document corrections

**Files:**
- Create: `docs/adr/ADR-029-corebank-client-circuit-breaker.md`
- Modify: `docs/adr/ADR-007-circuit-breaker.md:1-6`, `docs/adr/ADR-023-corebank-sole-outcome-source.md:1-7`, `docs/adr/ADR-024-instant-rail-retry-policy.md:1-13`
- Modify: `README.md:18`, `ARCHITECTURE.md:23,93,180,579-582`, `demo-requests.http:100-101`, `docs/backlog.md:232,236`
- Modify: `docs/superpowers/specs/2026-10-14-corebank-circuit-breaker-design.md:3`

- [ ] **Step 1: Write ADR-029**

Create `docs/adr/ADR-029-corebank-client-circuit-breaker.md`:

```markdown
# ADR-029: The `corebank-api` client carries a circuit breaker and no other resilience strategy

**Date:** 2026-10-14
**Status:** Accepted
**Deciders:** Architecture team
**Supersedes in part:** ADR-007's Implementation section (the breaker no longer comes from the standard resilience handler, which the `corebank-api` client does not use; the decision itself stands). ADR-024's sentence "Neither Kiota's `RetryHandler` nor the standard resilience handler is enabled on the `corebank-api` client" — still true, now read as: no *retry* middleware is enabled; a breaker is.
**Corrects:** ADR-023's statements (Decision 2, Alternatives "Stored backoff", Consequences) that the resilience handler backs off and opens a circuit for CoreBank calls. It never did between 2026-08-30 and this record; from this record a breaker exists and no backoff does.

Implementation spec: [`2026-10-14-corebank-circuit-breaker-design.md`](../superpowers/specs/2026-10-14-corebank-circuit-breaker-design.md).

## Context

Story 5.3 (2026-08-30) stripped the standard resilience handler from the `corebank-api` `HttpClient` with
`RemoveAllResilienceHandlers()`, because its retry strategy would re-send inside one `SendAsync` under a
layer that already decides when to retry (AD-11; later ADR-023 for the background outbox and ADR-024 for
the instant rail). The circuit breaker went with it. README, `ARCHITECTURE.md`, ADR-007 and ADR-023
kept describing one, and the talk's Journey A narrates one. With CoreBank down or Dev Proxy answering
errors, every 200 ms poll tick and every instant attempt went to the wire and failed on its own.

## Decision

1. **One strategy, the circuit breaker.** The `corebank-api` pipeline is `LastResponseStatusHandler` →
   `RemoveAllResilienceHandlers()` → one named resilience handler (`corebank-api-circuit-breaker`)
   holding one `HttpCircuitBreakerStrategyOptions` strategy → service discovery. No retry, timeout,
   hedging or rate limiter. Retry stays where ADR-023 and ADR-024 put it.
2. **Polly's HTTP transient predicate is kept.** `5xx`, `408`, `429`, `HttpRequestException` and
   `TimeoutRejectedException` count as failures. A `429` counts: the Azure Well-Architected throttling
   guidance pairs "respect `Retry-After`" with "fail fast on sustained throttling"; retry handles one
   throttled request, the breaker handles a throttle window. The background outbox does not yet honour
   `Retry-After`, so the breaker is the only back-pressure it has. A caller-cancelled attempt (the
   instant rail's per-attempt timer) is not a failure.
3. **Thresholds are configuration** (`Payments:CoreBankCircuitBreaker`, validated at startup):
   `Enabled` true, `FailureRatio` 0.2, `MinimumThroughput` 10, `SamplingDurationSeconds` 30,
   `BreakDurationSeconds` 10. The Regular Dev Proxy profile (5 %) and the console's 10 % step keep the
   circuit closed; the 25 % step opens it; one manual request never can. `Enabled: false` removes the
   handler entirely.
4. **`CircuitOpen` is a retry reason.** `KiotaCoreBankApiClient` maps `BrokenCircuitException` to
   `Retry(CircuitOpen)` with no status and no `Retry-After`. The background outbox treats it as any other
   retry: the row returns to `Pending`, `LastError` reads `Transaction submission failed: CircuitOpen.`,
   and the next tick retries, without limit (ADR-023).
5. **The instant rail may cancel locally on a never-sent command.** A `BrokenCircuitException` is thrown
   before any socket is touched. If every attempt of a payment ended that way, the command provably never
   left PaymentsAPI (ADR-020's condition for a local cancel): the claimed row is cancelled in place,
   reason `Instant rail: CoreBank circuit open; the command never left PaymentsAPI`, and the caller gets
   `504 Cancelled` in milliseconds. If any earlier attempt reached the wire, the loop skips its wait and
   enters the ADR-020 cancel phase, whose honest answer against an open circuit is `202`.
6. **Observability.** The three transitions are logged (opened at Warning with the break duration and the
   tripping outcome; half-opened and closed at Information) under the category
   `CoreBankDemo.PaymentsAPI.CoreBankCircuitBreaker`; Polly's meter (`Polly`,
   `resilience.polly.strategy.events`) is exported through ServiceDefaults. No new `BusinessMetrics`
   instrument or attribute value.
7. **Per-process state** (ADR-014): two PaymentsAPI replicas keep two circuits. A burst split evenly trips
   both within the same window; a small one may trip one.

## Alternatives considered

- **Restore the standard resilience handler.** Brings back retry inside `SendAsync`, which ADR-024 ruled
  out for four reasons, all about retrying; none applies to refusing.
- **Exclude `429` from the predicate.** Would move the on-stage trip point from the 25 % step to the 40 %
  step (Dev Proxy returns `503`/`429`/`500` with equal probability) and leave the background outbox with
  no back-pressure during a throttle window. Rejected.
- **A per-endpoint breaker (`SelectPipelineBy`).** One client, one destination; unnecessary.
- **Map the break onto the outbox row's `HoldUntil`.** The separate backlog decision on background
  `Retry-After`; not taken here. During a break each tick fails in microseconds, `RetryCount` climbs, and
  the rows drain once the circuit closes; nothing is lost.

## Consequences

- A failing CoreBank is refused at the client for the break duration instead of being hammered every
  200 ms per partition; the `retry_scheduled` panel spikes during a break and settles when it closes.
- An instant payment sent during a break answers `504` in milliseconds with a reason that says why.
- The k6 acceptance gate is unchanged: it injects no faults, and the LoadTests latency profile's
  caller-cancelled attempts are not counted.
- `ARCHITECTURE.md` and README now say what the pipeline holds; the console brief's Journey A is true again.

## Key takeaway

> Retry decides *when to send again*; the breaker decides *whether to send at all*. They live in different
> layers on purpose, and only the second one belongs in the HTTP pipeline.
```

- [ ] **Step 2: Header lines on the three ADRs**

`docs/adr/ADR-007-circuit-breaker.md` line 5 (the existing `**Superseded in part by:**` line) becomes:

```markdown
**Superseded in part by:** ADR-008 and ADR-013 replace the named HTTP client implementation; ADR-029 replaces the Implementation section (the breaker is a lone strategy on the `corebank-api` client, not part of the standard resilience handler, which that client does not use). The circuit-breaker decision remains accepted.
```

`docs/adr/ADR-023-corebank-sole-outcome-source.md`: after the `**Amended by:** ADR-028 (…)` line add:

```markdown
**Corrected by:** ADR-029 (the `corebank-api` client runs no backoff in its HTTP pipeline, only a circuit breaker; the statements below that "the resilience handler backs off" were never true for CoreBank calls)
```

`docs/adr/ADR-024-instant-rail-retry-policy.md`: after the `**Corrects:** …` paragraph (ends line 13) add:

```markdown
**Superseded in part by:** ADR-029 (the `corebank-api` client now carries a circuit breaker; the Decision's "neither Kiota's `RetryHandler` nor the standard resilience handler is enabled" still holds for *retry* middleware)
```

- [ ] **Step 3: README, ARCHITECTURE, demo-requests, backlog, spec status**

`README.md:18` — replace the row's second cell with:

```markdown
| Survive transient faults | **Retry in the outbox layer, circuit breaker on the client** — the background outbox retries every poll tick (ADR-023), the instant rail retries inside its budget and honours `Retry-After` (ADR-024), and a circuit breaker on the CoreBank client fails fast while CoreBank is failing (ADR-029); every other HTTP client keeps `AddStandardResilienceHandler()` (ADR-006, ADR-007) |
```

`ARCHITECTURE.md:23` — the table row becomes:

```markdown
| Microsoft.Extensions.Http.Resilience | Circuit breaker on the CoreBank client (ADR-029); the standard handler on every other HTTP client |
```

`ARCHITECTURE.md:93` — the box line `• Standard Resilience Handler (Retry, CB, Timeout)       │  │` becomes `• Circuit breaker on the CoreBank client (ADR-029)       │  │` (keep the column width: the replacement is the same length as the original text up to the `│`; count characters and pad with spaces so the right border still aligns).

`ARCHITECTURE.md:180` — in the sentence "the `corebank-api` client runs without the HTTP resilience pipeline (AD-11)" replace with "the `corebank-api` client's HTTP pipeline holds a circuit breaker and nothing else (AD-11, ADR-029): a break makes each tick fail fast instead of going to the wire".

`ARCHITECTURE.md:579-582` — the "Network Layer" item becomes:

```markdown
1. **Network Layer** - HTTP client pipelines
   - CoreBank client: circuit breaker only (ADR-029); retry lives in the outbox layer (ADR-023, ADR-024)
   - Every other client: the standard resilience handler (retry, circuit breaker, timeout; ADR-006, ADR-007)
   - Handles ~95% of transient issues
```

`demo-requests.http:100-101` becomes:

```http
### ===== STAGE 1: RETRY & CIRCUIT BREAKER =====
### (Arm faults in the DemoRunner console or Dev Proxy. The CoreBank client's
### breaker (ADR-029) opens at a 20 % failure ratio over 30 s once 10 calls
### were seen per replica, and stays open 10 s: the 5 % and 10 % error steps
### keep it closed, the 25 % step opens it. During a break an instant payment
### answers 504 at once; a standard payment is accepted and drains afterwards.)
```

`docs/backlog.md:232` — append to the bullet: ` Since ADR-029 the client's circuit breaker bounds the hammering during a sustained `429`/`503` window: a break makes each tick fail fast for the break duration.`

`docs/backlog.md:236` — replace the bullet with:

```markdown
- `KiotaCoreBankApiClient`'s `catch (TimeoutRejectedException)` branch is reachable only if a timeout strategy is ever added to the `corebank-api` pipeline (ADR-029 added a breaker, no timeout); the comment now says so. Kept on purpose so such a timeout would classify as `Timeout`; retire only together with a decision never to add one.
```

`docs/superpowers/specs/2026-10-14-corebank-circuit-breaker-design.md:3` — `> **Status:** Draft` becomes `> **Status:** Implemented`.

- [ ] **Step 4: Check the ARCHITECTURE box still aligns and nothing stale remains**

Run: `cd /Users/loekd/projects/CoreBankDemo && sed -n 88,96p ARCHITECTURE.md && grep -rn "AddStandardResilienceHandler()\` on the HTTP client\|Standard Resilience Handler (Retry, CB, Timeout)\|runs without the HTTP resilience pipeline" README.md ARCHITECTURE.md docs/adr demo-requests.http; echo "exit $?"`
Expected: the box's right border is in the same column on every line; the grep prints nothing (`exit 1`).

- [ ] **Step 5: Commit**

```bash
git add docs/adr/ADR-029-corebank-client-circuit-breaker.md docs/adr/ADR-007-circuit-breaker.md docs/adr/ADR-023-corebank-sole-outcome-source.md docs/adr/ADR-024-instant-rail-retry-policy.md README.md ARCHITECTURE.md demo-requests.http docs/backlog.md docs/superpowers/specs/2026-10-14-corebank-circuit-breaker-design.md
git commit -m "docs: ADR-029 corebank client circuit breaker; correct README, ARCHITECTURE, ADR-007/023/024

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Final verification

- [ ] **Step 1: Build the gate solution filter and run the unit tier once more**

Run: `cd /Users/loekd/projects/CoreBankDemo && export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1 && dotnet build CoreBankDemo.Rebuild.slnf 2>&1 | grep -E "warning|error|Build succeeded" | grep -v "EXTEXP0001" | head -20 && dotnet test CoreBankDemo.UnitTests.slnf --no-build 2>&1 | grep -E "Passed!|Failed!|threshold"`
Expected: `Build succeeded`, no new warnings from the touched files, every project `Passed!`.

- [ ] **Step 2: Confirm the branch state**

Run: `cd /Users/loekd/projects/CoreBankDemo && git status --porcelain | wc -l && git log --oneline origin/main..HEAD`
Expected: `0` and the seven commits of this plan on top of the spec commit. Do not push.

- [ ] **Step 3: Report**

Report, in this order: what was verified (unit tier, coverage, integration tier or why not), what was not (the AppHost 10 %/25 % scenario and the k6 gate need a running AppHost and are the user's manual acceptance per the spec), and the commits.
