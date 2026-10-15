# A circuit breaker, and only a circuit breaker, returns to the `corebank-api` client

> **Status:** Implemented
> **Kind:** design spec
> **Original date:** 2026-10-14
> **Related:** [ADR-029](../../adr/ADR-029-corebank-client-circuit-breaker.md) (new); [ADR-007](../../adr/ADR-007-circuit-breaker.md); [ADR-023](../../adr/ADR-023-corebank-sole-outcome-source.md); [ADR-024](../../adr/ADR-024-instant-rail-retry-policy.md); [ADR-020](../../adr/ADR-020-instant-rail-timeout-cancellation.md); [instant rail Retry-After](2026-09-23-instant-rail-retry-after-design.md); [story 5.3](2026-08-29-story-5-3-contract-generated-kiota-corebank-client-design.md); [constraints](../../constraints.md)

## Intent

**Problem:** The talk promises a circuit breaker on the PaymentsAPI → CoreBankAPI hop. README,
`ARCHITECTURE.md`, ADR-007 and ADR-023 describe one, and the console brief's Journey A has the speaker
stop CoreBank and "narrate retry and the breaker". There is none. The `corebank-api` `HttpClient` has
had `RemoveAllResilienceHandlers()` since the rail's first commit, because the standard handler's
*retry* strategy would double AD-11's retry classification in the outbox layer. The breaker went with
it, and ADR-024 records that "for CoreBank calls that has never been true". Today, with CoreBank down
or Dev Proxy answering errors, every poll tick and every instant attempt goes to the wire and fails on
its own.

**Approach:** Put a breaker back on the `corebank-api` client and nothing else: one named resilience
handler containing one Polly circuit-breaker strategy, with thresholds bound from configuration and
enabled by default. Retry stays where ADR-024 put it (the outbox kernel and the instant loop); no
timeout, rate limiter or retry strategy returns. The client learns to classify Polly's
`BrokenCircuitException` as a distinct retry reason, `CircuitOpen`. The instant rail uses that reason
for the one thing a breaker makes provable: an attempt the circuit rejected never left PaymentsAPI, so
if no attempt did, the payment is cancelled locally and the caller gets `504 Cancelled` in
milliseconds, with no network call. Standard payments are unaffected at acceptance and simply drain
once the circuit closes. Thresholds are chosen so the Regular Dev Proxy profile (5 % errors) and a
10 % step keep the circuit closed, and the 25 % step opens it.

## Boundaries & Constraints

**Always:**
- Add exactly one strategy, the circuit breaker, to the `corebank-api` pipeline. Keep
  `RemoveAllResilienceHandlers()` in place before it so the standard handler never returns.
- Keep Polly's HTTP transient predicate unchanged: `5xx`, `408`, `429`, `HttpRequestException` and
  `TimeoutRejectedException` count as failures. A `429` counts (see Design Notes).
- Bind thresholds from `Payments:CoreBankCircuitBreaker` with DataAnnotations, cross-field `Validate`
  and `ValidateOnStart` (the Story 3.1 options pattern). `Enabled` defaults to `true`.
- Classify `BrokenCircuitException` in `KiotaCoreBankApiClient` ahead of the generic catch, as
  `Retry(CoreBankRetryReason.CircuitOpen)` with no status code and no `Retry-After`.
- Keep every existing `CoreBankRetryException` message byte-identical. The new reason yields a new
  message only for the new case (`Transaction submission failed: CircuitOpen.`).
- In the instant loop, treat a `CircuitOpen` attempt as proof that *this attempt* never reached
  CoreBank. Cancel locally only when **every** attempt so far was `CircuitOpen`; a timeout or a
  transport exception on an earlier attempt means the command may be at CoreBank, and the ADR-020 cancel
  phase decides as today.
- Never wait after a `CircuitOpen` attempt: either cancel locally or go straight to the cancel phase.
- Leave the background outbox's retry rule exactly as ADR-023 states it: a failed row returns to
  `Pending` and is retried on the next poll tick, without limit.
- Log the three circuit transitions (opened, half-opened, closed) with structured fields so Loki shows
  the circuit's life cycle. Add Polly's meter to the ServiceDefaults metrics pipeline so its resilience
  event counter reaches Prometheus.
- Keep the metric contract's closed attribute sets closed: no new `BusinessMetrics` instrument or
  attribute value. The breaker's own telemetry is Polly's.
- Coverage ≥ 90 % per tier; TDD; DI-container inspection for wiring; a fake forwarder for the handler.

**Ask First:** Any second strategy in the pipeline; a per-endpoint breaker (`SelectPipelineBy`); a
change to the instant rail's budget, attempt timeout, cancel allowance or `MaxAttempts`; mapping
`Retry-After` onto the background outbox (a separate backlog item); any DemoRunner change; a Grafana
panel beyond what the plan verifies Polly actually emits.

**Never:** Retry inside the HTTP pipeline. Count a caller-cancelled attempt (the rail's own per-attempt
timer) as a breaker failure. Let an open circuit refuse a standard payment at `POST /api/payments` (it
cannot: acceptance never calls CoreBank, and this spec keeps it that way). Answer `504` for a payment
that may have reached CoreBank. Mark a row terminally `Failed`.

## Behaviour

```
breaker (per PaymentsAPI process, one state for every call through "corebank-api"):
    closed      → calls go to the wire; outcomes sampled over SamplingDuration
    opens when  → ≥ MinimumThroughput calls in the window and failures/calls ≥ FailureRatio
    open        → every SendAsync throws BrokenCircuitException at once, for BreakDuration
    half-open   → one probe call goes through; success closes, failure re-opens

instant loop (inside ADR-024's window; unchanged otherwise):
    on CircuitOpen from attempt n:
        if every attempt 1..n was CircuitOpen:   cancel locally, reason CircuitOpenCancelReason → 504
        else:                                    no wait; cancel phase (ADR-020) → usually 202

background outbox (unchanged code):
    tick → DeliverAsync → CircuitOpen → row back to Pending, RetryCount+1, LastError "…CircuitOpen."
```

Defaults, and what they mean per PaymentsAPI replica:

| Option | Default | Effect |
|---|---|---|
| `Enabled` | `true` | `false` registers no resilience handler at all |
| `FailureRatio` | `0.2` | 5 % and 10 % Dev Proxy steps stay closed; 25 % opens |
| `MinimumThroughput` | `10` | a console burst of 50 at concurrency 10 (≈ 25 per replica) is enough; a single manual request never trips it |
| `SamplingDurationSeconds` | `30` | the window the ratio is computed over |
| `BreakDurationSeconds` | `10` | long enough to send a request against the open circuit on stage |

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Clean traffic | no failures | circuit stays closed; every call unchanged; no new log lines | — |
| Regular profile | 5 % errors (503/429/500), 20–200 ms latency | ratio ≈ 0.05 < 0.2; closed | — |
| 25 % step, burst of 50 | ≈ 25 calls per replica, ≈ 6 failures | ratio ≈ 0.24 ≥ 0.2 → `OnCircuitOpened` logged at Warning with break duration | — |
| Instant payment, circuit already open | attempt 1 → `BrokenCircuitException` | client returns `Retry(CircuitOpen)`; loop cancels the claimed row locally (`CancelLocallyAsync` on the repository, reservation released), answers `504 Cancelled`, reason `CircuitOpenCancelReason`; no `instant_rail.retry_wait` event; no cancel call to CoreBank | persistence failure → `Deferred` (202), as today |
| Instant payment, circuit opens mid-loop | attempt 1 → `500` on the wire, attempt 2 → `CircuitOpen` | no wait after attempt 2; straight to the cancel phase; cancel hits the open circuit → `CancelAsync` returns null → claim released → `202` | — |
| Instant payment is the half-open probe | break elapsed, attempt goes through | success → `200`, circuit closes (`OnCircuitClosed` at Information); failure → re-opens, loop continues per ADR-024 | — |
| Background row, circuit open | poll tick | `DeliverAsync` throws `CoreBankRetryException(CircuitOpen)`; `MarkAsFailedWithRetryAsync`; `LastError = "Transaction submission failed: CircuitOpen."`; next tick retries | — |
| Rail's per-attempt timer fires | 2.5 s elapsed, request cancelled by the caller's token | Polly sees `OperationCanceledException`; not a handled failure; not counted | — |
| LoadTests latency profile | 9.5–12 s latency, no error responses | every attempt is caller-cancelled; circuit never opens; gate unchanged | — |
| `Enabled: false` | configuration | handler chain for `corebank-api` contains no resilience handler; behaviour identical to today | — |
| Options: `FailureRatio` 0 or > 1, `MinimumThroughput` < 2, durations < 0.5 s | configuration | startup failure naming the field (Polly's own ranges, mirrored) | — |
| Caller disconnects during a `CircuitOpen` conclusion | `cancellationToken` fires | `OperationCanceledException` rethrown; row left `Processing`; reclaimed when stale | as today |
| Dead `catch (TimeoutRejectedException)` | no timeout strategy in the pipeline | branch stays, comment corrected: it is reachable only if a timeout strategy is ever added | — |

## Code Map

- `CoreBankDemo.PaymentsAPI/Models/CoreBankCircuitBreakerOptions.cs` (new) — `SectionName =
  "Payments:CoreBankCircuitBreaker"`; `Enabled`, `FailureRatio` (`[Range(0.0, 1.0)]`, exclusive zero via
  `Validate`), `MinimumThroughput` (`[Range(2, int.MaxValue)]`), `SamplingDurationSeconds` and
  `BreakDurationSeconds` (`[Range(0.5, 86400)]`), with the defaults above.
- `CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs` — `AddCoreBankApiClient(this
  IServiceCollection, IConfiguration)` binds and validates the options, then on the named builder:
  `AddHttpMessageHandler(LastResponseStatusHandler)` → `RemoveAllResilienceHandlers()` → when enabled,
  `AddResilienceHandler("corebank-api-circuit-breaker", (pipeline, context) => pipeline.AddCircuitBreaker(
  new HttpCircuitBreakerStrategyOptions { … }))` reading the options from `context.ServiceProvider`, with
  `OnOpened`/`OnHalfOpened`/`OnClosed` logging through `ILogger` → `AddServiceDiscovery()`. The AD-11
  comment is rewritten to say what is and is not in the pipeline.
- `CoreBankDemo.PaymentsAPI/Program.cs` — passes `builder.Configuration` to `AddCoreBankApiClient`.
- `CoreBankDemo.PaymentsAPI/Outbox/CoreBankApiContracts.cs` — `CoreBankRetryReason.CircuitOpen` with
  XML doc "the client's circuit breaker rejected the call; nothing was sent".
- `CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs` — new `catch (BrokenCircuitException)`
  before `catch (Exception)`, returning `Retry(CircuitOpen)`; the `TimeoutRejectedException` comment
  corrected.
- `CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs` — in `ForwardUnderPartitionLockAsync`:
  a local `anyAttemptReachedWire` flag set by every outcome other than `CircuitOpen`; in the
  `CoreBankRetryException` catch, when `RetryReason == CircuitOpen`: if the flag is false, build the
  cancelled `TransactionSubmission` and set `claimed.ResponsePayload` exactly as the private
  `CancelLocallyAsync` does, then call `ConcludeCancelledAsync(payment, claimed, submission, startedAt,
  CircuitOpenCancelReason, neverForwarded: true, …)` on the row the loop already holds (not
  `CancelLocallyAsync` itself, which re-claims by id and would lose the loop's own claim); otherwise
  `break` to the cancel phase without a wait. New constant
  `CircuitOpenCancelReason = "Instant rail: CoreBank circuit open; the command never left PaymentsAPI"`.
  The existing `FailedPaymentTags` flow carries the reason onto the trace.
- `CoreBankDemo.PaymentsAPI/appsettings.json` — the `Payments:CoreBankCircuitBreaker` section with the
  defaults, so both AppHosts inherit it.
- `CoreBankDemo.ServiceDefaults/Extensions.cs` — `.AddMeter("Polly")` next to `BusinessMetrics.MeterName`.
  The plan verifies the meter and instrument names against the pinned `Polly.Core` before this line is
  written; if the names differ, the verified ones are used.
- `docs/adr/ADR-029-corebank-client-circuit-breaker.md` (new) — the decision: breaker only; thresholds;
  `429` counts; the instant rail's local cancel on a never-sent command; per-process state. Header lines:
  supersedes ADR-007's Implementation section; supersedes the sentence in ADR-024's Decision that
  "neither Kiota's `RetryHandler` nor the standard resilience handler is enabled" (now: no *retry*
  middleware is enabled); corrects ADR-023's statements that the resilience handler backs off (it never
  did, and still does not).
- `docs/adr/ADR-007-circuit-breaker.md`, `ADR-023`, `ADR-024` — a one-line "Superseded in part by
  ADR-029" / "Corrected by ADR-029" header each; bodies untouched.
- `README.md:18`, `ARCHITECTURE.md:23`, `:93`, `:180`, `:579-582` — "retry, circuit breaker, timeout via
  `AddStandardResilienceHandler()`" becomes "circuit breaker on the CoreBank client (ADR-029); retry in
  the outbox kernel and the instant loop (ADR-023, ADR-024); the standard handler on every other client".
- `demo-requests.http` Stage 1 header — names the breaker's thresholds and the 25 % step.
- `docs/backlog.md` — the "dead branch and stale comment" item under *instant rail Retry-After* is
  reworded (comment fixed; branch kept as documented); the background-outbox `Retry-After` item gains a
  sentence that the breaker now bounds hammering during a sustained `429` window.
- Tests: `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankCircuitBreakerOptionsTests.cs` (new, validation
  matrix); `…/CoreBankClientServiceCollectionExtensionsTests.cs` (new, handler-chain inspection enabled /
  disabled, mirroring `AddServiceDefaultsTests.WalkDelegatingHandlerChain`); `…/CoreBankApiClientTests.cs`
  (a `BrokenCircuitException` thrown by the fake handler → `CircuitOpen`, no status, no `RetryAfter`);
  `…/InstantPaymentForwardingHandlerTests.cs` (the two instant scenarios above, with `FakeTimeProvider` and
  the fake lock service proving no wait and no cancel call).

## Acceptance

- Given a fresh host with default configuration, when `IHttpMessageHandlerFactory` creates the
  `corebank-api` handler, then the chain contains exactly one resilience handler and no retry, timeout or
  rate-limiter strategy; with `Payments:CoreBankCircuitBreaker:Enabled=false` it contains none.
- Given the fake handler throws `BrokenCircuitException`, when any client operation runs, then the
  result is `Retry` with reason `CircuitOpen`, `StatusCode` null and `RetryAfter` null, and the
  exception the forwarder throws reads `Transaction submission failed: CircuitOpen.`.
- Given an instant payment whose first attempt is `CircuitOpen`, when the handler runs, then the caller
  receives `Cancelled` with `CircuitOpenCancelReason`, the fake forwarder's cancel was never called, no
  `instant_rail.retry_wait` event was recorded, and the lock was released only at the conclusion.
- Given an instant payment whose first attempt is a `500` and second is `CircuitOpen`, when the handler
  runs, then no wait occurs after the second attempt and the cancel phase is entered once.
- Given the Regular AppHost with the console's error rate at 10 %, when a burst of 100 runs, then no
  `OnCircuitOpened` log line appears; at 25 % one appears per replica within the burst, an instant
  payment sent during the break answers `504` with the new reason, and `OnCircuitClosed` follows within
  about ten seconds of the burst ending.
- The k6 acceptance gate (`CoreBankDemo.LoadTests`) passes unchanged; the processing-lanes verdict is
  `PASS`.

## Design Notes

**Why a breaker is compatible with AD-11 and ADR-024 when retry was not.** The handler was removed
because its retry strategy would re-send inside one `SendAsync`, under a loop that already decides when
to retry. A breaker sends nothing extra; it only refuses to send. Each refusal is one more failed
attempt to the layer above, which classifies and schedules it exactly as it does a connection refusal.
ADR-024's four reasons for keeping retry out of the handler are all about retrying; none applies to
refusing.

**Why `429` counts.** Microsoft's standard handler counts it, and the Azure Well-Architected throttling
guidance (TP-5) pairs "respect `Retry-After`" with "use the circuit breaker pattern to fail fast; don't
send traffic to a dependency that already signaled ongoing throttling". Retry handles one throttled
request; the breaker handles a sustained throttle window. Here that matters twice: the background outbox
does not yet honour `Retry-After` (backlog), so the breaker is the only back-pressure it has; and the
Dev Proxy errors file returns `503`, `429` and `500` with equal probability, so excluding `429` would
move the on-stage trip point from the 25 % step to the 40 % step. The instant rail still honours
`Retry-After` for a single throttled attempt (ADR-024).

**Why the thresholds.** Opening at 20 % with the slider at 25 % leaves a margin against sampling noise
on a 25-request window, and keeps the Regular 5 % preset and the 10 % step closed. `MinimumThroughput`
10 is low enough for a console burst split over two replicas and high enough that one manual request
can never open the circuit on its own. The 10 s break is a stage choice: a `504` can be shown against
the open circuit, and the circuit closes while the audience is still watching. These are configuration,
not code; the talk can run with other values without a rebuild.

**Why the instant rail may cancel locally.** ADR-020 reserves the local cancel for commands that
provably never left PaymentsAPI. A `BrokenCircuitException` is thrown by the handler before any socket
is touched, so an attempt that ends that way is exactly such a command. The guard is per payment, not
per attempt: once any attempt has gone to the wire, the command may be at CoreBank and only CoreBank can
say, so the ADR-020 cancel phase runs. In that case the cancel usually meets the same open circuit and
the honest answer is the residual `202`, as it is today for any failed cancel.

**What changes for the background outbox, and why it is left alone.** During a break each 200 ms tick
fails in microseconds instead of after a round trip, so `RetryCount` climbs roughly five times per
second per partition and the `retry_scheduled` panel spikes. ADR-023 has no retry cap and never writes
`Failed`, so nothing is lost and nothing becomes poison; the rows drain once the circuit closes.
Mapping `Retry-After` or the break duration onto `HoldUntil` remains the separate backlog decision.

**Per-process state.** Two PaymentsAPI replicas keep two circuits (ADR-014). A burst split evenly
trips both within the same window; a small one may trip one. The acceptance criterion says "per
replica" for that reason.

**Why no DemoRunner change.** The console consumes the system as it is. The breaker is visible through
the `504` with its new reason in the console's own payment list, through Loki (transition logs), Tempo
(the reason on the failed-payment tags) and Prometheus (Polly's event counter). A circuit-state chip is
not part of this change.

## Verification

- `dotnet test CoreBankDemo.UnitTests.slnf` green with the new tests; coverage gate met on
  `CoreBankDemo.PaymentsAPI` and `CoreBankDemo.ServiceDefaults`.
- `dotnet test CoreBankDemo.IntegrationTests.slnf` green (no schema change; no new integration test).
- One `aspire run` of `CoreBankDemo.AppHost` with faults armed: the 10 % / 25 % acceptance scenario above,
  observed in Loki and in the console.
- One opt-in run of `CoreBankDemo.LoadTests` with the latency profile: no `OnCircuitOpened` log line,
  confirming caller-cancelled attempts are not counted.
- The k6 acceptance harness (`load-test` skill) passes with the default profile.
