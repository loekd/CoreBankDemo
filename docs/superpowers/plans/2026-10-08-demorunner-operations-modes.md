# DemoRunner Operations Modes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Operations compose bar's `Burst mode`/`Single mode` toggle with a three-way `OptionSelector` (`Single`, `Burst`, `Fetch`), give each mode its own control bar, and let Fetch call PaymentsAPI's `GET /api/payments/{transactionId}` with a persistent result line.

**Architecture:** Bottom-up. A new allow-listed endpoint id and gateway call (Infrastructure); a controller command that records Evidence and holds the last fetch in `OperatorConsoleState` without touching tracked payments (Application); a pure projection of that fetch into a result line, a button caption and an enabled flag (Terminal presentation model); then the window: selector, three bars, wiring. The card's Look up outcome is untouched.

**Tech Stack:** .NET 10, Terminal.Gui 2.5.0 (`OptionSelector<TEnum>`), xUnit v3 + AwesomeAssertions, headless Terminal.Gui render tests.

**Spec:** `docs/superpowers/specs/2026-10-08-demorunner-operations-modes-design.md`

## Global Constraints

- Branch: `feature/demorunner-operations-modes` (already checked out, cut from `origin/main`). Never commit to `main`; do not push.
- Before any build: `dotnet tool restore`.
- DemoRunner only. No change to PaymentsAPI, CoreBank or any other project.
- The focus card's state stays driven by the outcome feed only: a Fetch never changes `TrackedPayments`.
- The console reaches the endpoint only through the allow-listed endpoint id `payments.status` (ADR-015).
- Every Fetch writes an Evidence record (`EvidenceKind.OutcomeQuery`).
- Fetch is never behind the single-action-in-flight lock; switching modes is never locked.
- Layout fits the 80×24 floor (71 usable columns) with every caption drawn; layout tests assert the drawn screen buffer, not only geometry.
- No `Tabs` control. No new colour token.
- Run the DemoRunner tests with a dead OTLP endpoint: `export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1` (ServiceDefaults tests otherwise push into a live collector).
- A filtered `dotnet test --filter` run exits 1 on the coverlet 90% gate even when every test passes; judge filtered runs by the `Passed!`/`Failed!` summary line, and the gate by the unfiltered project run.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

- **Switching modes while a single payment is in flight.** Expected: the selector still switches (display state only), the in-flight submission completes, and the card keeps tracking it. Pinned in Task 4 (`SwitchingModes_IsNeverLocked_ByAnInFlightAction`).
- **A Fetch while another Fetch is in flight.** Expected: the Fetch button is disabled with `Fetching — Ns`; no second call. Pinned in Task 3 (`CanFetch` false while in flight) and Task 4 (button disabled while in flight).
- **An id the operator typed survives switching away and back to Fetch.** Expected: not overwritten by the card's id. Pinned in Task 4 (`FetchPrefill_NeverOverwritesATypedId`).
- **Enter in a field fires the active bar's button only.** Expected: exactly one of Submit / Send burst / Fetch is the default button. Pinned in Task 4 (`EachMode_HasExactlyOneDefaultButton`).
- **An id with `/`.** Expected: sent as `%2F` (PaymentsAPI restores it, ADR-027). Pinned in Task 1 (gateway test).

## File Structure

| File | Action | Responsibility |
|---|---|---|
| `CoreBankDemo.DemoRunner/Application/KnownOperatorSurface.cs` | Modify | `KnownEndpoints.PaymentStatus` |
| `CoreBankDemo.DemoRunner/Infrastructure/EndpointResolver.cs` | Modify | Map `payments.status` to the profile's PaymentsAPI `GET /api/payments/{id}` |
| `CoreBankDemo.DemoRunner/Application/Ports/IPaymentGateway.cs` | Modify | `FetchPaymentStatusAsync` |
| `CoreBankDemo.DemoRunner/Infrastructure/HttpPaymentGateway.cs` | Modify | Implement it through `SendInspectionAsync` |
| `CoreBankDemo.DemoRunner/Application/OperatorModels.cs` | Modify | `PaymentStatusFetch` record; `OperatorConsoleState.PaymentStatusFetch` |
| `CoreBankDemo.DemoRunner/Application/EvidenceTitles.cs` | Modify | Two titles |
| `CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs` | Modify | `FetchPaymentStatusAsync` |
| `CoreBankDemo.DemoRunner/Terminal/PaymentStatusLine.cs` | Create | Fetch result line, caption, tone |
| `CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs` | Modify | Three model fields |
| `CoreBankDemo.DemoRunner/Terminal/ComposeMode.cs` | Create | `ComposeMode` enum (selector labels) |
| `CoreBankDemo.DemoRunner/Terminal/MainWindow.cs` | Modify | Selector, three bars, Fetch wiring; toggle removed |
| `tests/CoreBankDemo.DemoRunner.Tests/Fakes/OperatorHarness.cs` | Modify | Fake gateway member |
| `tests/CoreBankDemo.DemoRunner.Tests/Infrastructure/EndpointResolverTests.cs` | Modify | Resolver cases |
| `tests/CoreBankDemo.DemoRunner.Tests/Infrastructure/HttpPaymentGatewayTests.cs` | Modify | Gateway cases |
| `tests/CoreBankDemo.DemoRunner.Tests/Application/OperatorConsolePaymentStatusFetchTests.cs` | Create | Controller cases |
| `tests/CoreBankDemo.DemoRunner.Tests/Terminal/PaymentStatusLineTests.cs` | Create | Line cases |
| `tests/CoreBankDemo.DemoRunner.Tests/Terminal/ComposeBarRenderTests.cs` | Modify | Drawn bars and result line |
| `tests/CoreBankDemo.DemoRunner.Tests/Terminal/MainWindowTests.cs` | Modify | Toggle tests → selector tests |
| `docs/superpowers/specs/2026-09-03-demorunner-ux-experience-design.md`, `2026-09-03-demorunner-ux-design.md` | Modify | Compose bar, burst control, outcome query, floor rows |

---

### Task 1: `payments.status` endpoint and gateway call

**Files:**
- Modify: `CoreBankDemo.DemoRunner/Application/KnownOperatorSurface.cs` (`KnownEndpoints`)
- Modify: `CoreBankDemo.DemoRunner/Infrastructure/EndpointResolver.cs` (`EndpointFor`)
- Modify: `CoreBankDemo.DemoRunner/Application/Ports/IPaymentGateway.cs`
- Modify: `CoreBankDemo.DemoRunner/Infrastructure/HttpPaymentGateway.cs` (after `QueryOutcomeAsync`)
- Modify: `tests/CoreBankDemo.DemoRunner.Tests/Fakes/OperatorHarness.cs` (`FakePaymentGateway`)
- Test: `tests/CoreBankDemo.DemoRunner.Tests/Infrastructure/EndpointResolverTests.cs`, `tests/CoreBankDemo.DemoRunner.Tests/Infrastructure/HttpPaymentGatewayTests.cs`

**Interfaces:**
- Consumes: `EndpointResolver.PaymentsBaseUrl(profile)` (private, existing), `RequirePathParameter` (private, existing), `HttpPaymentGateway.SendInspectionAsync` (private, existing).
- Produces:
  - `public const string KnownEndpoints.PaymentStatus = "payments.status";`
  - `Task<InspectionResult> IPaymentGateway.FetchPaymentStatusAsync(TopologyProfile profile, string transactionId, CancellationToken ct)` — `Target` is `"payments.status"`.
  - `FakePaymentGateway.FetchIds` (`List<string>`), `FetchProfiles` (`List<TopologyProfile>`), `FetchStarted` / `ReleaseFetch` (`TaskCompletionSource?`); returns the next queued inspection (`QueueInspections`) or a default `200`.

- [ ] **Step 1: Write the failing resolver tests**

In `EndpointResolverTests`, add a case to the `EndpointFor_AllCompiledEndpointsResolve` theory:

```csharp
    [InlineData(KnownEndpoints.PaymentStatus, "key", "5295/api/payments/key")]
```

and add these tests:

```csharp
    /// <summary>
    /// PaymentsAPI's own status read (ADR-027), on the profile's PaymentsAPI port, reached only
    /// through this allow-listed id (ADR-015).
    /// </summary>
    [Theory]
    [InlineData(TopologyProfile.Regular, "http://127.0.0.1:5294/api/payments/tx-1")]
    [InlineData(TopologyProfile.LoadTests, "http://127.0.0.1:5295/api/payments/tx-1")]
    public void EndpointFor_PaymentStatus_IsAGetOnTheProfilesPaymentsApi(TopologyProfile profile, string expected)
    {
        var (url, method) = EndpointResolver.EndpointFor(profile, KnownEndpoints.PaymentStatus, "tx-1");

        url.Should().Be(expected);
        method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public void EndpointFor_PaymentStatus_RequiresAnId()
    {
        var missing = () => EndpointResolver.EndpointFor(TopologyProfile.Regular, KnownEndpoints.PaymentStatus);
        var blank = () => EndpointResolver.EndpointFor(TopologyProfile.Regular, KnownEndpoints.PaymentStatus, "  ");

        missing.Should().Throw<ArgumentException>();
        blank.Should().Throw<ArgumentException>();
    }
```

- [ ] **Step 2: Write the failing gateway tests**

In `HttpPaymentGatewayTests`, add:

```csharp
    [Fact]
    public async Task FetchPaymentStatus_IsABareGetOnPaymentsApi_WithTheIdEscaped()
    {
        var requests = new List<HttpRequestMessage>();
        using var client = new HttpClient(new StubHttpHandler(request =>
        {
            requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"Pending"}"""),
            });
        }));

        var result = await new HttpPaymentGateway(client)
            .FetchPaymentStatusAsync(TopologyProfile.Regular, "tenant/payment-1", CancellationToken.None);

        requests.Should().ContainSingle();
        requests[0].Method.Should().Be(HttpMethod.Get);
        requests[0].RequestUri!.AbsoluteUri.Should().Be("http://127.0.0.1:5294/api/payments/tenant%2Fpayment-1");
        result.Succeeded.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.Target.Should().Be(KnownEndpoints.PaymentStatus);
        result.Body.Should().Contain("Pending");
        result.Exchange!.Method.Should().Be("GET");
    }

    [Fact]
    public async Task FetchPaymentStatus_NotFound_IsAnAnsweredCallThatDidNotSucceed()
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));

        var result = await new HttpPaymentGateway(client)
            .FetchPaymentStatusAsync(TopologyProfile.LoadTests, "unknown", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(404);
        result.Exchange.Should().NotBeNull("the call was made and answered");
    }

    [Fact]
    public async Task FetchPaymentStatus_ConnectionFailure_HasNoStatusButKeepsTheRequest()
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            throw new HttpRequestException("Connection refused")));

        var result = await new HttpPaymentGateway(client)
            .FetchPaymentStatusAsync(TopologyProfile.Regular, "tx-1", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(0);
        result.ErrorSummary.Should().Contain("Connection refused");
        result.Exchange.Should().NotBeNull("the request was sent; only the answer is missing");
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet tool restore && dotnet test tests/CoreBankDemo.DemoRunner.Tests --filter "FullyQualifiedName~EndpointResolverTests|FullyQualifiedName~HttpPaymentGatewayTests"`
Expected: build FAIL — `'KnownEndpoints' does not contain a definition for 'PaymentStatus'`.

- [ ] **Step 4: Implement**

In `KnownOperatorSurface.cs`, `KnownEndpoints`, after `TransactionOutcome`:

```csharp
    /// <summary>
    /// PaymentsAPI's own status read, <c>GET /api/payments/{transactionId}</c> (ADR-027): the
    /// payments side's projection of CoreBank's outcome. The card's Look up outcome keeps asking
    /// CoreBank through <see cref="TransactionOutcome"/>.
    /// </summary>
    public const string PaymentStatus = "payments.status";
```

In `EndpointResolver.EndpointFor`, after the `PaymentsSubmit` arm:

```csharp
        KnownEndpoints.PaymentStatus => (
            $"{PaymentsBaseUrl(profile)}/api/payments/{Uri.EscapeDataString(RequirePathParameter(endpointId, pathParameter))}",
            HttpMethod.Get),
```

In `IPaymentGateway`, after `QueryOutcomeAsync`:

```csharp
    /// <summary>
    /// Reads a payment's status from PaymentsAPI (ADR-027). Read-only; the answer is never proof
    /// of an outcome on the card.
    /// </summary>
    Task<InspectionResult> FetchPaymentStatusAsync(
        TopologyProfile profile,
        string transactionId,
        CancellationToken ct);
```

In `HttpPaymentGateway`, after `QueryOutcomeAsync`:

```csharp
    public Task<InspectionResult> FetchPaymentStatusAsync(
        TopologyProfile profile,
        string transactionId,
        CancellationToken ct) =>
        SendInspectionAsync(profile, KnownEndpoints.PaymentStatus, transactionId, null, ct);
```

In `FakePaymentGateway` (`tests/.../Fakes/OperatorHarness.cs`), next to the query members add:

```csharp
    public List<string> FetchIds { get; } = [];
    public List<TopologyProfile> FetchProfiles { get; } = [];
    public TaskCompletionSource? FetchStarted { get; set; }
    public TaskCompletionSource? ReleaseFetch { get; set; }
```

and after `QueryOutcomeAsync`:

```csharp
    public async Task<InspectionResult> FetchPaymentStatusAsync(
        TopologyProfile profile,
        string transactionId,
        CancellationToken ct)
    {
        FetchProfiles.Add(profile);
        FetchIds.Add(transactionId);
        FetchStarted?.TrySetResult();
        if (ReleaseFetch is not null)
        {
            await ReleaseFetch.Task.WaitAsync(ct);
        }

        return NextInspection(KnownEndpoints.PaymentStatus);
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/CoreBankDemo.DemoRunner.Tests --filter "FullyQualifiedName~EndpointResolverTests|FullyQualifiedName~HttpPaymentGatewayTests"`
Expected: `Passed!`, 0 failed.

- [ ] **Step 6: Commit**

```bash
git add CoreBankDemo.DemoRunner/Application/KnownOperatorSurface.cs CoreBankDemo.DemoRunner/Infrastructure/EndpointResolver.cs CoreBankDemo.DemoRunner/Application/Ports/IPaymentGateway.cs CoreBankDemo.DemoRunner/Infrastructure/HttpPaymentGateway.cs tests/CoreBankDemo.DemoRunner.Tests/Fakes/OperatorHarness.cs tests/CoreBankDemo.DemoRunner.Tests/Infrastructure/EndpointResolverTests.cs tests/CoreBankDemo.DemoRunner.Tests/Infrastructure/HttpPaymentGatewayTests.cs
git commit -m "feat(demorunner): allow-list PaymentsAPI's payment status read

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Controller `FetchPaymentStatusAsync`

**Files:**
- Modify: `CoreBankDemo.DemoRunner/Application/OperatorModels.cs` (new record near `InspectionResult`; `OperatorConsoleState` init property)
- Modify: `CoreBankDemo.DemoRunner/Application/EvidenceTitles.cs`
- Modify: `CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs` (after `QueryOutcomeAsync`)
- Test: `tests/CoreBankDemo.DemoRunner.Tests/Application/OperatorConsolePaymentStatusFetchTests.cs` (create)

**Interfaces:**
- Consumes: `IPaymentGateway.FetchPaymentStatusAsync` (Task 1); controller privates `RefusedInspection`, `CaptureContext`, `Provenance(OperationContext)`, `AddEvidence(EvidenceProvenance, …, exchange:, account:)`, `Update`, `_time`, `_payments`.
- Produces:
  - `public sealed record PaymentStatusFetch(string TransactionId, DateTimeOffset StartedAt, InspectionResult? Result) { public bool InFlight => Result is null; }`
  - `OperatorConsoleState.PaymentStatusFetch` (`PaymentStatusFetch?`, init, default `null`)
  - `EvidenceTitles.PaymentStatusFetched = "Payment status fetched"`, `EvidenceTitles.PaymentStatusFetchFailed = "Payment status fetch failed"`
  - `public Task<InspectionResult> OperatorConsoleController.FetchPaymentStatusAsync(string transactionId, CancellationToken ct)` — sets `PaymentStatusFetch` to in-flight before the call and to the result after; a refusal sets it straight to the result.

- [ ] **Step 1: Write the failing tests**

Create `tests/CoreBankDemo.DemoRunner.Tests/Application/OperatorConsolePaymentStatusFetchTests.cs`:

```csharp
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application;

/// <summary>
/// Fetch reads PaymentsAPI's view of a payment (ADR-027). It is evidence, never proof: the card
/// and the still-open strip stay driven by the outcome feed.
/// </summary>
public class OperatorConsolePaymentStatusFetchTests
{
    private static readonly PaymentRequest StandardPayment =
        new("NL91ABNA0417164300", "NL20INGB0001234567", 10m, "EUR", PaymentRail.Standard);

    private const string PendingBody =
        """{"paymentId":"tx-1","transactionId":"tx-1","status":"Pending","amount":10.00,"currency":"EUR","processedAt":"2026-08-29T12:00:00+00:00"}""";

    private static async Task<(OperatorConsoleController Controller, OperatorHarness Harness)> AttachedAsync(
        TopologyProfile profile = TopologyProfile.Regular)
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(profile));
        var controller = harness.CreateController();
        (await controller.AttachAsync(profile, CancellationToken.None)).Succeeded.Should().BeTrue();
        return (controller, harness);
    }

    [Fact]
    public async Task WithoutATopology_IsRefused_RecordedAndNeverSent()
    {
        var harness = new OperatorHarness();
        var controller = harness.CreateController();

        var result = await controller.FetchPaymentStatusAsync("tx-1", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        harness.Payments.FetchIds.Should().BeEmpty();
        var record = controller.State.Evidence.Last();
        record.Title.Should().Be("Payment status fetch refused");
        record.Summary.Should().Contain("Start or attach a topology");
        controller.State.PaymentStatusFetch!.Result.Should().BeSameAs(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankId_IsRefused_AndNeverSent(string id)
    {
        var (controller, harness) = await AttachedAsync();

        var result = await controller.FetchPaymentStatusAsync(id, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        harness.Payments.FetchIds.Should().BeEmpty();
        controller.State.Evidence.Last().Summary.Should().Contain("Payment status fetch refused").And.Contain("payment id");
    }

    [Theory]
    [InlineData(TopologyProfile.Regular)]
    [InlineData(TopologyProfile.LoadTests)]
    public async Task AnAnswer_IsRecordedAsFetched_OnTheAttachedProfile(TopologyProfile profile)
    {
        var (controller, harness) = await AttachedAsync(profile);
        harness.Payments.QueueInspections(new InspectionResult(
            true, 200, KnownEndpoints.PaymentStatus, PendingBody, null, TimeSpan.FromMilliseconds(38)));

        await controller.FetchPaymentStatusAsync("  tx-1 ", CancellationToken.None);

        harness.Payments.FetchIds.Should().Equal("tx-1");
        harness.Payments.FetchProfiles.Should().Equal(profile);
        var record = controller.State.Evidence.Last();
        record.Kind.Should().Be(EvidenceKind.OutcomeQuery);
        record.Title.Should().Be(EvidenceTitles.PaymentStatusFetched);
        record.Succeeded.Should().BeTrue();
        record.StatusCode.Should().Be(200);
        var fetch = controller.State.PaymentStatusFetch!;
        fetch.TransactionId.Should().Be("tx-1");
        fetch.InFlight.Should().BeFalse();
        fetch.Result!.Body.Should().Be(PendingBody);
    }

    [Fact]
    public async Task NotFound_IsAnAnswer_NotAFailure()
    {
        var (controller, harness) = await AttachedAsync();
        harness.Payments.QueueInspections(new InspectionResult(
            false, 404, KnownEndpoints.PaymentStatus, string.Empty, "HTTP 404", TimeSpan.FromMilliseconds(21)));

        await controller.FetchPaymentStatusAsync("unknown", CancellationToken.None);

        var record = controller.State.Evidence.Last();
        record.Title.Should().Be(EvidenceTitles.PaymentStatusFetched);
        record.Succeeded.Should().BeTrue("an unknown id is an answer");
    }

    [Fact]
    public async Task NoAnswer_IsRecordedAsFailed()
    {
        var (controller, harness) = await AttachedAsync();
        harness.Payments.QueueInspections(new InspectionResult(
            false, 0, KnownEndpoints.PaymentStatus, null, "Connection refused", TimeSpan.FromSeconds(3)));

        await controller.FetchPaymentStatusAsync("tx-1", CancellationToken.None);

        var record = controller.State.Evidence.Last();
        record.Title.Should().Be(EvidenceTitles.PaymentStatusFetchFailed);
        record.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task WhileTheCallIsOut_TheFetchIsInFlight()
    {
        var (controller, harness) = await AttachedAsync();
        harness.Payments.FetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.ReleaseFetch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var fetch = controller.FetchPaymentStatusAsync("tx-1", CancellationToken.None);
        await harness.Payments.FetchStarted.Task;

        controller.State.PaymentStatusFetch!.InFlight.Should().BeTrue();
        controller.State.PaymentStatusFetch.StartedAt.Should().Be(harness.Time.GetUtcNow());

        harness.Payments.ReleaseFetch.SetResult();
        await fetch;
        controller.State.PaymentStatusFetch!.InFlight.Should().BeFalse();
    }

    [Fact]
    public async Task AFetch_NeverChangesATrackedPayment()
    {
        var (controller, harness) = await AttachedAsync();
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "tx-1", "tx-1", "Pending", "{}", null, TimeSpan.FromMilliseconds(5)));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Supplied, "tx-1", CancellationToken.None);
        var before = controller.State.TrackedPayments;
        harness.Payments.QueueInspections(new InspectionResult(
            true, 200, KnownEndpoints.PaymentStatus,
            PendingBody.Replace("Pending", "Completed"), null, TimeSpan.FromMilliseconds(38)));

        await controller.FetchPaymentStatusAsync("tx-1", CancellationToken.None);

        controller.State.TrackedPayments.Should().Equal(before, "the card is driven by the feed, never by a fetch");
        controller.State.Evidence.Last().Account.Should().Be(StandardPayment.ToAccount);
    }

    [Fact]
    public async Task AFetch_IsNotHeldBackByAnInFlightAction()
    {
        var (controller, harness) = await AttachedAsync();
        harness.Payments.SubmissionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.ReleaseSubmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "tx-1", "tx-1", "Pending", "{}", null, TimeSpan.FromMilliseconds(5)));
        var submit = controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Supplied, "tx-1", CancellationToken.None);
        await harness.Payments.SubmissionStarted.Task;
        controller.State.ActiveMutation.Should().NotBeNull();

        await controller.FetchPaymentStatusAsync("tx-1", CancellationToken.None);

        harness.Payments.FetchIds.Should().Equal("tx-1");
        harness.Payments.ReleaseSubmission.SetResult();
        await submit;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CoreBankDemo.DemoRunner.Tests --filter "FullyQualifiedName~OperatorConsolePaymentStatusFetchTests"`
Expected: build FAIL — `'OperatorConsoleController' does not contain a definition for 'FetchPaymentStatusAsync'`.

- [ ] **Step 3: Implement**

In `OperatorModels.cs`, after the `InspectionResult` record:

```csharp
/// <summary>
/// The console's latest PaymentsAPI status read (ADR-027), for the Fetch result line. Held until
/// the next Fetch replaces it. <see cref="Result"/> is <see langword="null"/> while the call is
/// out. Never consulted for a payment's outcome: the card is driven by the feed.
/// </summary>
public sealed record PaymentStatusFetch(string TransactionId, DateTimeOffset StartedAt, InspectionResult? Result)
{
    public bool InFlight => Result is null;
}
```

In `OperatorConsoleState`, after `CancellingSince`:

```csharp
    /// <summary>The latest Fetch on the Operations compose area, or null before the first.</summary>
    public PaymentStatusFetch? PaymentStatusFetch { get; init; }
```

In `EvidenceTitles.cs`, after `OutcomeQueryFailed`:

```csharp
    public const string PaymentStatusFetched = "Payment status fetched";
    public const string PaymentStatusFetchFailed = "Payment status fetch failed";
```

In `OperatorConsoleController.cs`, after `QueryOutcomeAsync`:

```csharp
    /// <summary>
    /// Reads the payment's status from PaymentsAPI (ADR-027). Read-only and never blocked by the
    /// single-action-in-flight rule, like the outcome query. The answer is recorded as evidence and
    /// held for the Fetch result line; it never touches a tracked payment, whose outcome is the
    /// feed's to prove.
    /// </summary>
    public async Task<InspectionResult> FetchPaymentStatusAsync(string transactionId, CancellationToken ct)
    {
        var state = State;
        var id = transactionId?.Trim() ?? string.Empty;
        var startedAt = _time.GetUtcNow();
        InspectionResult result;
        if (state.Profile == TopologyProfile.None || state.Ownership == TopologyOwnership.None)
        {
            result = RefusedInspection(
                EvidenceKind.OutcomeQuery,
                "Payment status fetch",
                KnownEndpoints.PaymentStatus,
                "Start or attach a topology before fetching a payment status.");
        }
        else if (id.Length == 0)
        {
            result = RefusedInspection(
                EvidenceKind.OutcomeQuery,
                "Payment status fetch",
                KnownEndpoints.PaymentStatus,
                "Enter a payment id.");
        }
        else
        {
            Update(current => current with { PaymentStatusFetch = new PaymentStatusFetch(id, startedAt, null) });
            var context = CaptureContext(state);
            result = await _payments.FetchPaymentStatusAsync(context.Profile, id, ct);
            var answered = result.StatusCode > 0;
            AddEvidence(
                Provenance(context),
                EvidenceKind.OutcomeQuery,
                answered ? EvidenceTitles.PaymentStatusFetched : EvidenceTitles.PaymentStatusFetchFailed,
                answered ? $"Payment status fetch returned HTTP {result.StatusCode}" : "Payment status fetch failed",
                "GET",
                result.Target,
                result.StatusCode,
                result.Duration,
                result.Body ?? result.ErrorSummary ?? string.Empty,
                // An unknown id is an answer, not a failure.
                result.Succeeded || result.StatusCode == 404,
                exchange: result.Exchange,
                // Known only for a payment this console still tracks; a bare id names no creditor.
                account: state.TrackedPayments.FirstOrDefault(payment =>
                    payment.TransactionId == id || payment.IdempotencyKey == id)?.ToAccount);
        }

        Update(current => current with { PaymentStatusFetch = new PaymentStatusFetch(id, startedAt, result) });
        return result;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CoreBankDemo.DemoRunner.Tests --filter "FullyQualifiedName~OperatorConsolePaymentStatusFetchTests"`
Expected: `Passed!`, 0 failed (10 tests).

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.DemoRunner/Application/OperatorModels.cs CoreBankDemo.DemoRunner/Application/EvidenceTitles.cs CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs tests/CoreBankDemo.DemoRunner.Tests/Application/OperatorConsolePaymentStatusFetchTests.cs
git commit -m "feat(demorunner): fetch a payment's status from PaymentsAPI as evidence

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: The Fetch result line in the presentation model

**Files:**
- Create: `CoreBankDemo.DemoRunner/Terminal/PaymentStatusLine.cs`
- Modify: `CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs` (`OperatorPresentationModel` record and its single construction in `PresentationModelBuilder.Build`)
- Test: `tests/CoreBankDemo.DemoRunner.Tests/Terminal/PaymentStatusLineTests.cs` (create)

**Interfaces:**
- Consumes: `PaymentStatusFetch`, `OperatorConsoleState.PaymentStatusFetch` (Task 2); `OutcomeFeedNarrative.Clock(DateTimeOffset?)` (`HH:mm:ss`).
- Produces:
  - `public enum LineTone { Neutral, Accent, Failure }`
  - `public sealed record FetchLineViewModel(string Status, string Stamp, LineTone Tone)` with `static FetchLineViewModel Empty`
  - `public static class PaymentStatusLine` with `FetchLineViewModel Build(PaymentStatusFetch? fetch)`, `string Caption(PaymentStatusFetch? fetch, DateTimeOffset now)`, `const string FetchCaption = "Fetch"`
  - `OperatorPresentationModel` gains trailing `FetchLineViewModel FetchLine, string FetchCaption, bool CanFetch`.

- [ ] **Step 1: Write the failing tests**

Create `tests/CoreBankDemo.DemoRunner.Tests/Terminal/PaymentStatusLineTests.cs`:

```csharp
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
```

`HttpExchange` is `(Method, Url, RequestHeaders, RequestBody, StatusCode, ReasonPhrase, ResponseHeaders, ResponseBody)`; the line only checks whether one is present.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CoreBankDemo.DemoRunner.Tests --filter "FullyQualifiedName~PaymentStatusLineTests"`
Expected: build FAIL — `The name 'PaymentStatusLine' does not exist in the current context`.

- [ ] **Step 3: Implement**

Create `CoreBankDemo.DemoRunner/Terminal/PaymentStatusLine.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Terminal;

/// <summary>Which of the theme's existing tokens a line wears. No new colour is introduced.</summary>
public enum LineTone
{
    Neutral,
    Accent,
    Failure,
}

/// <summary>The Fetch result line: what came back on the left, when and how long on the right.</summary>
public sealed record FetchLineViewModel(string Status, string Stamp, LineTone Tone)
{
    public static FetchLineViewModel Empty { get; } = new(string.Empty, string.Empty, LineTone.Neutral);
}

/// <summary>
/// Projects the latest <see cref="PaymentStatusFetch"/> onto the Fetch result line (spec:
/// demorunner-operations-modes). The line persists until the next Fetch, and its stamp is the
/// press time, so a repeated identical answer still visibly changes.
/// </summary>
public static class PaymentStatusLine
{
    public const string FetchCaption = "Fetch";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static FetchLineViewModel Build(PaymentStatusFetch? fetch)
    {
        if (fetch is null)
        {
            return FetchLineViewModel.Empty;
        }

        if (fetch.Result is not { } result)
        {
            return new($"~ GET /api/payments/{fetch.TransactionId} …", string.Empty, LineTone.Neutral);
        }

        var stamp = $"fetched {OutcomeFeedNarrative.Clock(fetch.StartedAt)} · {result.Duration.TotalMilliseconds:F0} ms";
        return result.StatusCode switch
        {
            200 => TryRead(result.Body) is { } body
                ? new(
                    $"✓ 200  {body.Status} · {body.Amount.ToString("N2", CultureInfo.InvariantCulture)} {body.Currency} · "
                    + $"{(body.Status == "Pending" ? "since" : "at")} {OutcomeFeedNarrative.Clock(body.ProcessedAt)}",
                    stamp,
                    LineTone.Accent)
                : new("✗ 200  unreadable response body", stamp, LineTone.Failure),
            404 => new("○ 404  no payment with this id", stamp, LineTone.Neutral),
            > 0 => new($"✗ {result.StatusCode}  unexpected answer from PaymentsAPI", stamp, LineTone.Failure),
            // No status: either the request was never built (a refusal) or it got no answer.
            _ when result.Exchange is null => new($"✗ {result.ErrorSummary}", stamp, LineTone.Failure),
            _ => new($"✗ PaymentsAPI unreachable — {result.ErrorSummary}", stamp, LineTone.Failure),
        };
    }

    /// <summary>The Fetch button's caption: a running clock while its call is out, like <c>Cancelling — 3s</c>.</summary>
    public static string Caption(PaymentStatusFetch? fetch, DateTimeOffset now) =>
        fetch is { InFlight: true } inFlight
            ? $"Fetching — {Math.Max(0, (now - inFlight.StartedAt).TotalSeconds):F0}s"
            : FetchCaption;

    private static PaymentStatusBody? TryRead(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<PaymentStatusBody>(body, Web);
            return string.IsNullOrWhiteSpace(parsed?.Status) || string.IsNullOrWhiteSpace(parsed.Currency)
                ? null
                : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // PaymentsAPI's frozen PaymentResponse, as much of it as the line prints.
    private sealed record PaymentStatusBody(string? Status, decimal Amount, string? Currency, DateTimeOffset ProcessedAt);
}
```

In `PresentationModel.cs`, extend the record — after the `string TopologyStatus` parameter:

```csharp
    string TopologyStatus,
    // The Fetch result line and button (spec: demorunner-operations-modes). Never locked by the
    // single-action-in-flight rule; disabled only while its own call is out.
    FetchLineViewModel FetchLine,
    string FetchCaption,
    bool CanFetch);
```

(Replace the existing `string TopologyStatus);` ending.) In `PresentationModelBuilder.Build`, the `new OperatorPresentationModel(` call ends with the topology-status argument; append the three new arguments after it:

```csharp
            state.ActiveMutation is null
                ? state.StatusLine
                : $"{state.ActiveMutation.Kind} · {state.ActiveMutation.Target} · Running",
            PaymentStatusLine.Build(state.PaymentStatusFetch),
            PaymentStatusLine.Caption(state.PaymentStatusFetch, now),
            state.PaymentStatusFetch is not { InFlight: true });
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CoreBankDemo.DemoRunner.Tests --filter "FullyQualifiedName~PaymentStatusLineTests|FullyQualifiedName~PresentationModelBuilderTests"`
Expected: `Passed!`, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.DemoRunner/Terminal/PaymentStatusLine.cs CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs tests/CoreBankDemo.DemoRunner.Tests/Terminal/PaymentStatusLineTests.cs
git commit -m "feat(demorunner): project the latest fetch onto a result line

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Selector and three bars in `MainWindow`

**Files:**
- Create: `CoreBankDemo.DemoRunner/Terminal/ComposeMode.cs`
- Modify: `CoreBankDemo.DemoRunner/Terminal/MainWindow.cs`
- Test: `tests/CoreBankDemo.DemoRunner.Tests/Terminal/ComposeBarRenderTests.cs`, `tests/CoreBankDemo.DemoRunner.Tests/Terminal/MainWindowTests.cs`

**Interfaces:**
- Consumes: `OperatorConsoleController.FetchPaymentStatusAsync` (Task 2); `OperatorPresentationModel.FetchLine/FetchCaption/CanFetch`, `LineTone` (Task 3); `OptionSelector<TEnum>` (Terminal.Gui 2.5: labels from the enum names, `Value` defaults to the first member, `Orientation.Horizontal` draws `◉ Single  ○ Burst  ○ Fetch` on one row, 26 cells; `ValueChanged` passes `EventArgs<TEnum?>` with `.Value`; setting `Value` raises it).
- Produces (test hooks): `ModeSelector` (`OptionSelector<ComposeMode>`), `SendBurstButton`, `FetchButton` (`Button`), `FetchIdField` (`TextField`), `FetchStatusText`, `FetchStampText` (`string`), `FetchStatusLabel` (`View`), `SelectComposeModeForTest(ComposeMode)`, `TriggerFetchForTestAsync()`. `BurstButton` is removed.

The row ladder after this task (compose-area rows, relative to `_operationsMain`):

| Row | Single | Burst | Fetch |
|---|---|---|---|
| 0 | selector | selector | selector |
| 1 | From · → To · **Submit** | From · → To · **Send burst** | Payment id · **Fetch** |
| 2 | Amount · Rail · Key | Amount · Rail · Count · at once | result line · stamp |
| 3 | Supplied/Omitted line (only in those key modes) | — | — |

Columns at the 71-column floor: Amount caption 1, field 8–15; Rail chip 17–37; Key chip 40–60 (Single); `Count` caption 40, field 46–51; `at once` caption 53, field 61–66 (Burst); `Payment id` caption 1, field 12 to `Dim.Fill(CardActionSlotWidth + 1)` (≥ 38 cells, a GUID fits); result status `Dim.Fill(FetchStampWidth + 1)`, stamp `AnchorEnd(FetchStampWidth)` with `FetchStampWidth = 26`.

- [ ] **Step 1: Replace the toggle's render test with per-mode render tests (failing)**

In `ComposeBarRenderTests.cs`, replace the whole `TheModeButton_DrawsOnItsOwnLine_AndTheBurstFieldsShareIt` test (and its `<summary>`) with:

```csharp
    /// <summary>
    /// Each mode draws its own bar under the selector, whole, at the floor and at a comfortable
    /// width; every mode is three rows, so the card's first line stays on the same screen row
    /// whichever mode is selected.
    /// </summary>
    [Theory]
    [InlineData(80, 24)]
    [InlineData(100, 30)]
    public void EachMode_DrawsItsOwnBar_AndTheCardNeverMoves(int width, int height)
    {
        using var app = TerminalAppFactory.CreateHeadless(width, height);
        OperatorTheme.Register(ThemeMode.Dark);
        var controller = new OperatorHarness().CreateController();
        using var window = new MainWindow(
            app, controller, () => Task.CompletedTask, null, startPolling: false, marshalUpdates: false);
        app.Begin(window);
        window.Frame = new System.Drawing.Rectangle(0, 0, width, height);
        window.HandleKeyForTest(Key.D1);
        window.ResizeForTest(width, height);

        List<string> Draw(ComposeMode mode)
        {
            window.SelectComposeModeForTest(mode);
            window.RenderForTest();
            app.LayoutAndDraw(true);
            return AsDrawn(app, window.SubmitButton.SuperView!);
        }

        var single = Draw(ComposeMode.Single);
        var singleCardRow = window.CardStateLabel.FrameToScreen().Y;
        single[0].Should().Be(" ◉ Single  ○ Burst  ○ Fetch");
        single[1].Should().StartWith(" From").And.Contain("→ To").And.EndWith("⟦► Submit ◄⟧");
        single[2].Should().StartWith(" Amount").And.Contain("⟦ Rail ‹ standard ›⟧").And.Contain("⟦ Key ‹ Generated ›⟧");

        var burst = Draw(ComposeMode.Burst);
        burst[0].Should().Be(" ○ Single  ◉ Burst  ○ Fetch");
        burst[1].Should().StartWith(" From").And.Contain("→ To").And.EndWith("⟦► Send burst ◄⟧");
        burst[2].Should().StartWith(" Amount").And.Contain("⟦ Rail ‹ standard ›⟧")
            .And.Contain("Count").And.Contain("at once").And.NotContain("Key ‹", "a burst never reads the key mode");
        window.CardStateLabel.FrameToScreen().Y.Should().Be(singleCardRow);

        var fetch = Draw(ComposeMode.Fetch);
        fetch[0].Should().Be(" ○ Single  ○ Burst  ◉ Fetch");
        fetch[1].Should().StartWith(" Payment id").And.EndWith("⟦► Fetch ◄⟧").And.NotContain("From");
        fetch[2].Should().BeEmpty("nothing has been fetched yet");
        window.CardStateLabel.FrameToScreen().Y.Should().Be(singleCardRow);
    }

    /// <summary>
    /// The result line is drawn under the Fetch bar and stays until the next Fetch; a second,
    /// identical answer still changes the line, because its stamp is the press time.
    /// </summary>
    [Fact]
    public async Task FetchResultLine_IsDrawnUnderItsBar_AndARepeatChangesOnlyTheStamp()
    {
        using var app = TerminalAppFactory.CreateHeadless(80, 24);
        OperatorTheme.Register(ThemeMode.Dark);
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = new MainWindow(
            app, controller, () => Task.CompletedTask, null, startPolling: false, marshalUpdates: false, time: harness.Time);
        app.Begin(window);
        window.Frame = new System.Drawing.Rectangle(0, 0, 80, 24);
        window.HandleKeyForTest(Key.D1);
        window.ResizeForTest(80, 24);
        window.SelectComposeModeForTest(ComposeMode.Fetch);
        window.FetchIdField.Text = "tx-1";
        const string body =
            """{"paymentId":"tx-1","transactionId":"tx-1","status":"Pending","amount":1.00,"currency":"EUR","processedAt":"2026-08-29T11:59:58+00:00"}""";
        harness.Payments.QueueInspections(
            new InspectionResult(true, 200, KnownEndpoints.PaymentStatus, body, null, TimeSpan.FromMilliseconds(38)),
            new InspectionResult(true, 200, KnownEndpoints.PaymentStatus, body, null, TimeSpan.FromMilliseconds(41)));

        await window.TriggerFetchForTestAsync();
        window.RenderForTest();
        app.LayoutAndDraw(true);
        var first = AsDrawn(app, window.SubmitButton.SuperView!)[2];

        harness.Time.Advance(TimeSpan.FromSeconds(5));
        await window.TriggerFetchForTestAsync();
        window.RenderForTest();
        app.LayoutAndDraw(true);
        var second = AsDrawn(app, window.SubmitButton.SuperView!)[2];

        first.Should().StartWith(" ✓ 200  Pending · 1.00 EUR · since 11:59:58").And.EndWith("fetched 12:00:00 · 38 ms");
        second.Should().StartWith(" ✓ 200  Pending · 1.00 EUR · since 11:59:58").And.EndWith("fetched 12:00:05 · 41 ms");
        window.FetchStatusLabel.SchemeName.Should().Be(OperatorTheme.LockExemptScheme, "a 200 takes the teal accent");
        harness.Payments.FetchIds.Should().Equal("tx-1", "tx-1");
    }
```

Add `using CoreBankDemo.DemoRunner.Application;` to the file's usings if it is not there. The harness clock starts at `2026-08-29T12:00:00Z` and moves only through `Advance`, so the first stamp is `12:00:00`. At 80 columns the status label is 43 cells wide (71 − 1 − 27) and the asserted `✓ 200` text is exactly 43 characters: a wider stamp or caption would truncate it, which this test catches.

- [ ] **Step 2: Rewrite the toggle tests in `MainWindowTests` (failing)**

Replace these four tests (with their `<summary>` blocks): `ArmedBurst_SubmitSendsTheBurstInsteadOfASinglePayment`, `SubmitButtonLabel_TogglesToSendBurst_WhileBurstSetupIsArmed`, `BurstModeButton_NamesTheModeItSwitchesTo`, `BurstModeButton_MatchesSubmitAndOwnsTheThirdRow` with:

```csharp
    [Fact]
    public async Task SendBurst_SendsABurst_AndSubmitAlwaysSendsOnePayment()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);
        window.SelectComposeModeForTest(ComposeMode.Burst);
        window.BurstCountField.Text = "2";
        window.BurstConcurrencyField.Text = "1";

        window.SendBurstButton.InvokeCommand(Command.Accept);
        await window.LastDispatchedTask!;

        harness.Payments.Submissions.Should().HaveCount(2);
        window.SubmitButton.Text.ToString().Should().Be("Submit", "no button changes its caption with the mode");
        window.SendBurstButton.Text.ToString().Should().Be("Send burst");

        window.SelectComposeModeForTest(ComposeMode.Single);
        await window.TriggerSubmitForTestAsync();

        harness.Payments.Submissions.Should().HaveCount(3, "Submit sends exactly one payment in every mode");
    }

    [Fact]
    public void EachMode_ShowsOnlyItsOwnControls()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);

        window.ModeSelector.Value.Should().Be(ComposeMode.Single);
        window.SubmitButton.Visible.Should().BeTrue();
        window.IdempotencyButton.Visible.Should().BeTrue();
        window.SendBurstButton.Visible.Should().BeFalse();
        window.BurstCountField.Visible.Should().BeFalse();
        window.FetchButton.Visible.Should().BeFalse();

        window.SelectComposeModeForTest(ComposeMode.Burst);
        window.SendBurstButton.Visible.Should().BeTrue();
        window.BurstCountField.Visible.Should().BeTrue();
        window.BurstConcurrencyField.Visible.Should().BeTrue();
        window.FromAccountField.Visible.Should().BeTrue();
        window.SubmitButton.Visible.Should().BeFalse();
        window.IdempotencyButton.Visible.Should().BeFalse("a burst never reads the key mode");

        window.SelectComposeModeForTest(ComposeMode.Fetch);
        window.FetchButton.Visible.Should().BeTrue();
        window.FetchIdField.Visible.Should().BeTrue();
        window.FromAccountField.Visible.Should().BeFalse();
        window.AmountField.Visible.Should().BeFalse();
        window.RailButton.Visible.Should().BeFalse();
        window.SendBurstButton.Visible.Should().BeFalse();
    }

    [Theory]
    [InlineData(ComposeMode.Single)]
    [InlineData(ComposeMode.Burst)]
    [InlineData(ComposeMode.Fetch)]
    public void EachMode_HasExactlyOneDefaultButton(ComposeMode mode)
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        window.SelectComposeModeForTest(mode);

        new[] { window.SubmitButton, window.SendBurstButton, window.FetchButton }
            .Where(button => button.IsDefault)
            .Should().ContainSingle()
            .Which.Should().BeSameAs(mode switch
            {
                ComposeMode.Single => window.SubmitButton,
                ComposeMode.Burst => window.SendBurstButton,
                _ => window.FetchButton,
            });
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(100, 30)]
    public void Selector_OwnsTheFirstRow_AndEveryModeIsThreeRowsTall(int width, int height)
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        window.ResizeForTest(width, height);

        foreach (var mode in new[] { ComposeMode.Single, ComposeMode.Burst, ComposeMode.Fetch })
        {
            window.SelectComposeModeForTest(mode);
            window.RenderForTest();
            window.ModeSelector.Frame.Y.Should().Be(0);
            window.ComposeRuleLabel.Frame.Y.Should().Be(3, "every mode is three rows ({0})", mode);
        }

        window.SelectComposeModeForTest(ComposeMode.Burst);
        window.RenderForTest();
        window.BurstCountField.Frame.Y.Should().Be(2, "the burst fields share the Amount line");
        window.SendBurstButton.Frame.X.Should().Be(window.SubmitButton.Frame.X, "each bar's action sits in the same slot");
        window.SendBurstButton.SchemeName.Should().Be(OperatorTheme.ActionScheme);
        window.FetchButton.SchemeName.Should().Be(OperatorTheme.ActionScheme);
    }

    [Fact]
    public void SingleAndBurst_ShareTheAccountAndAmountValues()
    {
        var controller = new OperatorHarness().CreateController();
        using var window = CreateWindow(controller);
        window.AmountField.Text = "7.50";

        window.SelectComposeModeForTest(ComposeMode.Burst);
        window.SelectComposeModeForTest(ComposeMode.Single);

        window.AmountField.Text.ToString().Should().Be("7.50");
    }

    [Fact]
    public async Task FetchPrefill_TakesTheCardsPayment_AndNeverOverwritesATypedId()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.ResizeForTest(100, 30);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "card-key", "card-key", "Pending", "{}", null, TimeSpan.FromMilliseconds(5)));
        window.IdempotencyButton.InvokeCommand(Command.Accept); // Generated → Supplied
        window.SuppliedKeyField.Text = "card-key";
        await window.TriggerSubmitForTestAsync();
        window.RenderForTest();

        window.SelectComposeModeForTest(ComposeMode.Fetch);
        window.FetchIdField.Text.ToString().Should().Be("card-key");

        window.FetchIdField.Text = "typed-id";
        window.SelectComposeModeForTest(ComposeMode.Single);
        window.SelectComposeModeForTest(ComposeMode.Fetch);
        window.FetchIdField.Text.ToString().Should().Be("typed-id");
    }

    [Fact]
    public async Task SwitchingModes_IsNeverLocked_ByAnInFlightAction()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        harness.Payments.SubmissionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.ReleaseSubmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "tx-1", "tx-1", "Pending", "{}", null, TimeSpan.FromMilliseconds(5)));
        var submit = window.TriggerSubmitForTestAsync();
        await harness.Payments.SubmissionStarted.Task;
        window.RenderForTest();

        window.ModeSelector.Enabled.Should().BeTrue();
        window.SelectComposeModeForTest(ComposeMode.Fetch);
        window.RenderForTest();
        window.FetchButton.Enabled.Should().BeTrue("a fetch is read-only and never locked");

        harness.Payments.ReleaseSubmission.SetResult();
        await submit;
        controller.State.TrackedPayments.Should().ContainSingle();
    }

    [Fact]
    public async Task FetchButton_CountsUpAndIsDisabled_WhileItsCallIsOut()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        using var window = CreateWindow(controller);
        window.SelectComposeModeForTest(ComposeMode.Fetch);
        window.FetchIdField.Text = "tx-1";
        harness.Payments.FetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.ReleaseFetch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var fetch = window.TriggerFetchForTestAsync();
        await harness.Payments.FetchStarted.Task;
        window.RenderForTest();

        window.FetchButton.Enabled.Should().BeFalse();
        window.FetchButton.Text.ToString().Should().StartWith("Fetching — ");
        window.FetchStatusText.Should().Be("~ GET /api/payments/tx-1 …");

        harness.Payments.ReleaseFetch.SetResult();
        await fetch;
        window.RenderForTest();
        window.FetchButton.Enabled.Should().BeTrue();
        window.FetchButton.Text.ToString().Should().Be("Fetch");
        window.LastUiMessage.Should().BeEmpty("the result line carries the answer, not the announcement");
    }
```

Then, in `ComposeBar_KeepsBothCaptionsAndEveryControl_AtEveryWidth` (near line 1505), replace

```csharp
        window.SubmitButton.Frame.Y.Should().Be(0);
        window.IdempotencyButton.Frame.Y.Should().Be(1);
        window.BurstButton.Frame.Y.Should().Be(2, "the mode button never shares the chips' line at {0}x{1}", width, height);
        window.ComposeRuleLabel.Frame.Y.Should().BeGreaterThan(
            window.BurstButton.Frame.Y,
            "the rule closes the bar beneath every line it grew");
```

with

```csharp
        window.ModeSelector.Frame.Y.Should().Be(0);
        window.SubmitButton.Frame.Y.Should().Be(1);
        window.IdempotencyButton.Frame.Y.Should().Be(2, "the chips keep their own line at {0}x{1}", width, height);
        window.ComposeRuleLabel.Frame.Y.Should().BeGreaterThan(
            window.IdempotencyButton.Frame.Y,
            "the rule closes the bar beneath every line it grew");
```

Update that test's `<summary>`: replace "the mode button on its own third line" wording with "the selector on its own first line". Search the rest of `MainWindowTests.cs` for `BurstButton`, `Burst mode`, `Single mode`, `.Frame.Y.Should().Be(0)` on `SubmitButton`, and `ComposeRuleLabel.Frame.Y.Should().Be(3` assertions that assumed the old ladder; each must be updated to the new ladder above (Submit on row 1, chips row 2, rule row 3), never deleted.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/CoreBankDemo.DemoRunner.Tests --filter "FullyQualifiedName~ComposeBarRenderTests|FullyQualifiedName~MainWindowTests"`
Expected: build FAIL — `The type or namespace name 'ComposeMode' could not be found` / `'MainWindow' does not contain a definition for 'SelectComposeModeForTest'`.

- [ ] **Step 4: Add `ComposeMode`**

Create `CoreBankDemo.DemoRunner/Terminal/ComposeMode.cs`:

```csharp
namespace CoreBankDemo.DemoRunner.Terminal;

/// <summary>
/// The Operations compose area's three modes. The member names are the selector's labels
/// (<c>OptionSelector&lt;ComposeMode&gt;</c>), so their order and spelling are what the room reads.
/// </summary>
public enum ComposeMode
{
    Single,
    Burst,
    Fetch,
}
```

- [ ] **Step 5: Rework `MainWindow` — constants and fields**

Remove `BurstModeCaption`, `SingleModeCaption` and their `<summary>`; remove `ModeButtonRow` and its `<summary>`. Rewrite the `ComposeBarRows` `<summary>` and add the new constants:

```csharp
    /// <summary>
    /// The selector's row plus the active mode's two-line bar, before any mode-specific line.
    /// Every mode is this tall, so switching modes never moves the card.
    /// </summary>
    private const int ComposeBarRows = 3;

    private const int SelectorRow = 0;
    private const int ComposeFirstRow = 1;
    private const int ComposeSecondRow = 2;

    // Burst's second line: Count and at once share it with Amount and the rail chip, in the
    // column the Key chip uses in Single, narrowed to fit the 71-column floor (ends at 66).
    private const int BurstFieldWidth = 6;
    private const int BurstCountCaptionX = 40;
    private const int BurstConcurrencyCaptionX = 53;

    /// <summary>"fetched HH:mm:ss · 12345 ms" — the Fetch result line's right-hand stamp.</summary>
    private const int FetchStampWidth = 26;
```

Replace the field `private readonly Button _burstButton = NewButton(BurstModeCaption);` with:

```csharp
    private readonly OptionSelector<ComposeMode> _modeSelector = new() { Orientation = Orientation.Horizontal };
    private readonly Button _sendBurstButton = NewButton("Send burst");
    private readonly Button _fetchButton = NewButton(PaymentStatusLine.FetchCaption);
    private readonly TextField _fetchId = new() { Text = string.Empty };
    private readonly Label _fetchStatus = new();
    private readonly Label _fetchStamp = new();
```

Replace `private bool _burstSetupVisible;` with:

```csharp
    private ComposeMode _composeMode = ComposeMode.Single;

    /// <summary>The id Fetch's field was last filled with from the card, so a typed id is never overwritten.</summary>
    private string? _lastFetchPrefill;
```

Next to `_modeLine` / `_burstSetupLabel` / `_burstConcurrencyLabel`, add:

```csharp
    private Label _fromCaption = null!;
    private Label _toCaption = null!;
    private Label _amountCaption = null!;
    private Label _fetchIdCaption = null!;
```

In the constructor's theme block, replace

```csharp
        // Submit and the mode button are Operations' two filled-teal controls: the mode button
        // wears Submit's treatment so the compose bar's own two actions read as a pair. The
```

with

```csharp
        // Submit, Send burst and Fetch are the compose area's filled-teal controls, one per mode,
        // in the same right-hand slot. The
```

and replace `OperatorTheme.Apply(_burstButton, OperatorTheme.ActionScheme);` with:

```csharp
        OperatorTheme.Apply(_sendBurstButton, OperatorTheme.ActionScheme);
        OperatorTheme.Apply(_fetchButton, OperatorTheme.ActionScheme);
```

- [ ] **Step 6: Rework `MainWindow` — `BuildComposeBar`**

Replace the whole `BuildComposeBar` method and its `<summary>` with:

```csharp
    /// <summary>
    /// The selector on the first row, then the active mode's own two-line bar with its own action
    /// in the right-anchored slot: Single (From, To, Submit; Amount, rail, key), Burst (From, To,
    /// Send burst; Amount, rail, count, at once) or Fetch (Payment id, Fetch; the result line).
    /// No button changes its caption or meaning with the mode. The captions are what stay at
    /// every width: an unlabelled IBAN read from the back of a room is a run of digits. There is
    /// no currency field and no currency validation — the console always sends EUR.
    /// </summary>
    private void BuildComposeBar(View view)
    {
        // Added in the workspace's literal Tab order: the selector, then each bar's first line,
        // then its second, then the line a key mode renders (EXPERIENCE.md, Accessibility Floor).
        _modeSelector.X = LabelX;
        _modeSelector.Y = SelectorRow;
        view.Add(_modeSelector);
        _modeSelector.ValueChanged += (_, e) =>
        {
            _composeMode = e.Value ?? ComposeMode.Single;
            if (_composeMode == ComposeMode.Fetch)
            {
                PrefillFetchId();
            }

            Repaint();
        };

        _fromCaption = AddField(view, "From", _fromAccount, LabelX, ComposeFirstRow, AccountCaptionWidth, AccountFieldWidth);
        _toCaption = AddField(
            view,
            "→ To",
            _toAccount,
            LabelX + AccountCaptionWidth + AccountFieldWidth + 2,
            ComposeFirstRow,
            AccountCaptionWidth,
            AccountFieldWidth);
        _fetchIdCaption = AddField(view, "Payment id", _fetchId, LabelX, ComposeFirstRow, 10, 1);
        _fetchId.Width = Dim.Fill(CardActionSlotWidth + 1);
        foreach (var action in new[] { _submitButton, _sendBurstButton, _fetchButton })
        {
            action.X = Pos.AnchorEnd(CardActionSlotWidth);
            action.Y = ComposeFirstRow;
            view.Add(action);
        }

        _amountCaption = AddField(view, "Amount", _amount, LabelX, ComposeSecondRow, AmountCaptionWidth, AmountFieldWidth);
        _railButton.X = RailChipX;
        _railButton.Y = ComposeSecondRow;
        _railButton.Width = ChipWidth;
        _idempotencyButton.X = KeyChipX;
        _idempotencyButton.Y = ComposeSecondRow;
        _idempotencyButton.Width = ChipWidth;
        view.Add(_railButton, _idempotencyButton);

        _burstSetupLabel = AddField(view, "Count", _burstCount, BurstCountCaptionX, ComposeSecondRow, 5, BurstFieldWidth);
        _burstConcurrencyLabel = AddField(
            view, "at once", _burstConcurrency, BurstConcurrencyCaptionX, ComposeSecondRow, 7, BurstFieldWidth);

        _fetchStatus.X = LabelX;
        _fetchStatus.Y = ComposeSecondRow;
        _fetchStatus.Height = 1;
        _fetchStatus.Width = Dim.Fill(FetchStampWidth + 1);
        _fetchStamp.X = Pos.AnchorEnd(FetchStampWidth);
        _fetchStamp.Y = ComposeSecondRow;
        _fetchStamp.Height = 1;
        _fetchStamp.Width = FetchStampWidth;
        _fetchStamp.TextAlignment = Alignment.End;
        view.Add(_fetchStatus, _fetchStamp);

        _railButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            _rail = _rail == PaymentRail.Standard ? PaymentRail.Instant : PaymentRail.Standard;
            _railButton.Text = $"Rail ‹ {_rail.ToString().ToLowerInvariant()} ›";
        };
        _idempotencyButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            _idempotencyMode = _idempotencyMode switch
            {
                IdempotencyMode.Generated => IdempotencyMode.Supplied,
                IdempotencyMode.Supplied => IdempotencyMode.Omitted,
                _ => IdempotencyMode.Generated,
            };
            _idempotencyButton.Text = $"Key ‹ {_idempotencyMode} ›";
            // The mode line costs a row only in the mode that needs it, so the whole workspace is
            // redrawn here rather than only the row ladder: the strip's rule and the feed
            // statement move with it and must not be left stating the old layout.
            Repaint();
        };

        // The one mode-specific line beneath Single's bar: the supplied-key field in Supplied
        // mode, the not-retry-safe warning in Omitted mode. No control in use is ever hidden.
        _modeLine = new Label { X = LabelX, Y = ComposeBarRows, Height = 1, Width = 14, Text = "Supplied key" };
        _suppliedKey.X = LabelX + 15;
        _suppliedKey.Y = ComposeBarRows;
        _suppliedKey.Height = 1;
        _suppliedKey.Width = AccountFieldWidth + 6;
        view.Add(_modeLine, _suppliedKey);

        _submitButton.Accepting += (_, e) => { e.Handled = true; Dispatch(SubmitPaymentAsync); };
        _sendBurstButton.Accepting += (_, e) => { e.Handled = true; Dispatch(RunBurstAsync); };
        _fetchButton.Accepting += (_, e) => { e.Handled = true; Dispatch(FetchPaymentStatusAsync); };

        _composeRule.X = LabelX;
        _composeRule.Y = ComposeBarRows;
        _composeRule.Height = 1;
        _composeRule.Width = Dim.Fill(1);
        view.Add(_composeRule);
    }

    /// <summary>
    /// Fills Fetch's field with the card's payment when the field is empty or still holds the
    /// id it was last filled with. An id the operator typed is never overwritten, and with no
    /// payment on the card the field is left as it is.
    /// </summary>
    private void PrefillFetchId()
    {
        if (_focusCard.IsPlaceholder || string.IsNullOrEmpty(_focusCard.TransactionId))
        {
            return;
        }

        var current = _fetchId.Text.ToString() ?? string.Empty;
        if (current.Length == 0 || string.Equals(current, _lastFetchPrefill, StringComparison.Ordinal))
        {
            _fetchId.Text = _focusCard.TransactionId;
            _lastFetchPrefill = _focusCard.TransactionId;
        }
    }

    /// <summary>One call per press; the answer goes to the result line and Evidence, never the announcement.</summary>
    private Task FetchPaymentStatusAsync() =>
        _controller.FetchPaymentStatusAsync(_fetchId.Text.ToString() ?? string.Empty, _sessionCancellation.Token);
```

`Alignment` is already used in this file (`button.TextAlignment = Alignment.Start`). `AddField` is called with a placeholder `fieldWidth: 1` for the Fetch id because it sets the caption and position; the `Dim.Fill` on the next line replaces the width.

- [ ] **Step 7: Rework `MainWindow` — `SubmitPaymentAsync`, `ApplyOperationsRows`, `Render`, hooks**

In `SubmitPaymentAsync`, delete the opening burst branch and its comment:

```csharp
        // Burst mode arms the burst rather than firing it, so Submit is the one action that sends
        // payments: while the burst setup is visible, Submit sends the burst instead of a single
        // payment. There is no separate "Start burst" button duplicating this.
        if (_burstSetupVisible)
        {
            await RunBurstAsync();
            return;
        }

```

In `ApplyOperationsRows`, replace everything from `// The mode line speaks only about Supplied and Omitted mode` down to and including `_composeRule.Y = ruleRow;` with:

```csharp
        ApplyComposeMode();

        // Every mode's bar is the same height, so only Single's key-mode line can grow it.
        var ruleRow = ComposeBarRows + (_modeLine.Visible ? 1 : 0);
        _composeRule.Y = ruleRow;
```

and add this method directly above `ApplyOperationsRows`:

```csharp
    /// <summary>
    /// Shows the active mode's bar and nothing else, and makes its action the one default button,
    /// so Enter in any field fires the bar that field belongs to.
    /// </summary>
    private void ApplyComposeMode()
    {
        var single = _composeMode == ComposeMode.Single;
        var burst = _composeMode == ComposeMode.Burst;
        var fetch = _composeMode == ComposeMode.Fetch;

        foreach (var paymentControl in new View[]
                 {
                     _fromCaption, _fromAccount, _toCaption, _toAccount, _amountCaption, _amount, _railButton,
                 })
        {
            paymentControl.Visible = !fetch;
        }

        _idempotencyButton.Visible = single;
        _submitButton.Visible = single;
        _submitButton.IsDefault = single;

        _burstSetupLabel.Visible = burst;
        _burstCount.Visible = burst;
        _burstConcurrencyLabel.Visible = burst;
        _burstConcurrency.Visible = burst;
        _sendBurstButton.Visible = burst;
        _sendBurstButton.IsDefault = burst;

        _fetchIdCaption.Visible = fetch;
        _fetchId.Visible = fetch;
        _fetchStatus.Visible = fetch;
        _fetchStamp.Visible = fetch;
        _fetchButton.Visible = fetch;
        _fetchButton.IsDefault = fetch;

        // The key-mode line speaks only about Supplied and Omitted mode in Single, so in every
        // other case it is a row of noise and the first one reclaimed.
        var supplied = single && _idempotencyMode == IdempotencyMode.Supplied;
        var omitted = single && _idempotencyMode == IdempotencyMode.Omitted;
        _modeLine.Visible = supplied || omitted;
        _suppliedKey.Visible = supplied;
        _modeLine.Text = supplied
            ? "Supplied key"
            : "Omitted mode: not retry-safe after an ambiguous outcome";
        _modeLine.Width = supplied ? 14 : Dim.Fill(1);
    }
```

In `Render`, replace

```csharp
        _submitButton.Enabled = !model.IsBusy;
        _burstButton.Enabled = !model.IsBusy;
```

with

```csharp
        _submitButton.Enabled = !model.IsBusy;
        _sendBurstButton.Enabled = !model.IsBusy;
        // Read-only, so never behind the single-action lock; disabled only while its own call is out.
        _fetchButton.Enabled = model.CanFetch;
        _fetchButton.Text = model.FetchCaption;
        _fetchStatus.Text = model.FetchLine.Status;
        OperatorTheme.Apply(_fetchStatus, model.FetchLine.Tone switch
        {
            LineTone.Accent => OperatorTheme.LockExemptScheme,
            LineTone.Failure => OperatorTheme.DestructiveScheme,
            _ => OperatorTheme.BaseScheme,
        });
        _fetchStamp.Text = model.FetchLine.Stamp;
```

Search `MainWindow.cs` for any remaining `_burstButton` or `_burstSetupVisible` use (the `BuildOperationsView` `<summary>` mentions "the mode button") and update the wording to the selector; there must be none left in code.

In the test-hook region, replace `internal Button BurstButton => _burstButton;` with:

```csharp
    internal OptionSelector<ComposeMode> ModeSelector => _modeSelector;
    internal Button SendBurstButton => _sendBurstButton;
    internal Button FetchButton => _fetchButton;
    internal TextField FetchIdField => _fetchId;
    internal string FetchStatusText => _fetchStatus.Text;
    internal string FetchStampText => _fetchStamp.Text;
    internal View FetchStatusLabel => _fetchStatus;
    internal void SelectComposeModeForTest(ComposeMode mode) => _modeSelector.Value = mode;
    internal Task TriggerFetchForTestAsync() => FetchPaymentStatusAsync();
```

- [ ] **Step 8: Run the DemoRunner tests**

Run: `export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1 && dotnet test tests/CoreBankDemo.DemoRunner.Tests`
Expected: `Passed!`, 0 failed, coverage gate met. If a drawn-row assertion fails, print the drawn rows (`AsDrawn`) and fix the layout, not the assertion — the assertion states the spec's layout. If `OptionSelector` draws its glyphs differently under the operator theme than the probe saw (`◉`/`○`), assert the three labels and the selected one rather than the glyphs, and ledger it.

- [ ] **Step 9: Commit**

```bash
git add CoreBankDemo.DemoRunner/Terminal/ComposeMode.cs CoreBankDemo.DemoRunner/Terminal/MainWindow.cs tests/CoreBankDemo.DemoRunner.Tests/Terminal/ComposeBarRenderTests.cs tests/CoreBankDemo.DemoRunner.Tests/Terminal/MainWindowTests.cs
git commit -m "feat(demorunner): Single, Burst and Fetch modes replace the burst toggle

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: UX specs and spec status

**Files:**
- Modify: `docs/superpowers/specs/2026-09-03-demorunner-ux-experience-design.md` (rows **Compose bar**, **Burst control**, **Outcome query**, and the **Operations compose bar at the 80×24 floor** row; a new **Payment status fetch** row after **Outcome query**)
- Modify: `docs/superpowers/specs/2026-09-03-demorunner-ux-design.md` (the **Compose bar** component bullet)
- Modify: `docs/superpowers/specs/2026-10-08-demorunner-operations-modes-design.md` (header)

**Interfaces:** none.

- [ ] **Step 1: Experience spec**

In the **Compose bar** row, replace

`Two captioned lines: \`From <iban>\` and \`To <iban>\` with **Submit** anchored at the right edge of the first; \`Amount\`, the rail chip (\`standard\`/\`instant\`) and the idempotency chip on the second; **Burst mode** / **Single mode** anchored at the right edge of a third line of its own, sharing that line with the burst count fields while a burst is armed.`

with

`A horizontal three-way selector — **Single**, **Burst**, **Fetch** — on its first row, then the selected mode's own two-line bar with its own action in the right-anchored slot (spec: demorunner-operations-modes). **Single:** \`From <iban>\` and \`To <iban>\` with **Submit**; \`Amount\`, the rail chip (\`standard\`/\`instant\`) and the idempotency chip. **Burst:** the same accounts with **Send burst**; \`Amount\`, the rail chip, \`Count\` and \`at once\` — no idempotency chip, which a burst never reads. **Fetch:** see **Payment status fetch**. No button changes its caption or meaning with the mode, and every mode is three rows, so switching never moves the card. Single and Burst share the account, amount and rail values.`

and in the same row replace `A third line exists only where a mode needs one` with `A further line exists only beneath Single where a key mode needs one`.

In the **Burst control** row, replace the location cell `Operations workspace, reached from **Burst mode** on the compose bar (which then reads **Single mode**, the way back)` with `Operations workspace, the compose area's **Burst** mode; **Send burst** sends it`.

In the **Outcome query** row, append before the closing ` |`: `. It asks CoreBank; **Payment status fetch** is the PaymentsAPI view, and the two can disagree for as long as the outcome event is in transit (ADR-027)`.

Insert after the **Outcome query** row:

```markdown
| Payment status fetch | Operations workspace, the compose area's **Fetch** mode | A **Payment id** field and **Fetch**, which reads \`GET /api/payments/{transactionId}\` through the allow-listed \`payments.status\` (ADR-015, ADR-027). The field is prefilled from the card's payment when it is empty or still holds the last prefill; a typed id is never overwritten — a deliberate, scoped return of a typed target, so Fetch can reach payments k6 or an \`.http\` file sent. A persistent result line beneath the bar states the answer until the next Fetch: \`~\` while the call is out (Fetch reads \`Fetching — Ns\` and is disabled), \`✓ 200\` with status, amount and time in the teal accent, \`○ 404\` neutral, \`✗\` in the failure token for anything else; its right end is always \`fetched HH:mm:ss · <n> ms\`, so an identical repeat still visibly changes. Read-only and never blocked by the single-action-in-flight rule. Every Fetch is an Evidence record. **A Fetch answer never changes the card or the strip**: they are driven by the outcome feed |
```

In the **Operations compose bar at the 80×24 floor** row, replace `— \`From\`/\`To\` with truncated-but-labelled account identifiers and Submit on the first line, \`Amount\` and both chips on the second, the mode button on its own third — so the chips never compete with it for the second line and no caption is shed.` with `— the selector on its own first line; \`From\`/\`To\` with truncated-but-labelled account identifiers and the mode's action on the second; \`Amount\` and both chips (Single) or the rail chip, \`Count\` and \`at once\` (Burst, narrowed fields ending at column 66) on the third — so no caption is shed.`

- [ ] **Step 2: Design-token spec**

In `2026-09-03-demorunner-ux-design.md`, **Compose bar** bullet, replace `a third line holds the mode button (\`[ Burst mode ]\` / \`[ Single mode ]\`, naming the mode it switches *to*) in that same right-hand column, with the burst count fields beside it while a burst is armed.` with `a horizontal three-way selector (\`Single\` / \`Burst\` / \`Fetch\`) sits above both lines, and each mode carries its own action in the same right-hand column — Submit, Send burst or Fetch, all \`action-slot-primary\` — with Burst's count fields on the second line in place of the Key chip and Fetch's result line in place of the second line (spec: demorunner-operations-modes).`

If either exact sentence is not found verbatim (the files are long and may have been reflowed), locate the passage by its distinctive words (`Burst mode`, `Single mode`, `mode button`) and apply the same meaning; every mention of the toggle must be gone: `grep -n "Burst mode\|Single mode\|mode button" docs/superpowers/specs/2026-09-03-demorunner-ux-*.md` prints nothing.

- [ ] **Step 3: Spec header**

In `2026-10-08-demorunner-operations-modes-design.md`, change `> **Status:** Draft` to `> **Status:** Implemented`.

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-09-03-demorunner-ux-experience-design.md docs/superpowers/specs/2026-09-03-demorunner-ux-design.md docs/superpowers/specs/2026-10-08-demorunner-operations-modes-design.md
git commit -m "docs(demorunner): selector, three bars and payment status fetch in the UX specs

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Full gate

**Files:** none.

- [ ] **Step 1: Unit tier**

Run: `export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1 && dotnet test CoreBankDemo.UnitTests.slnf`
Expected: PASS, coverage gate met.

- [ ] **Step 2: Integration tier**

Run: `dotnet test CoreBankDemo.IntegrationTests.slnf` (Docker required)
Expected: PASS (no DemoRunner code runs here; this proves nothing else broke).

- [ ] **Step 3: Report**

Report the pass/fail counts and coverage from both runs. Do not push or open a PR unless asked.
