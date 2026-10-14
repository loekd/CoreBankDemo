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
