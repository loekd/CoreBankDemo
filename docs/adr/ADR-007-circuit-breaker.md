# ADR-007: Circuit breaker to prevent cascading failures

**Date:** 2026-05-23  
**Status:** Accepted  
**Deciders:** Architecture team  

## Context

When CoreBankAPI is down or unhealthy, continued retries from PaymentsAPI waste resources, increase latency, and can exhaust connection pools. This turns a single service failure into a system-wide outage. We need a mechanism to "fail fast" when a downstream dependency is known to be unavailable, giving it time to recover.

## Decision

Apply a Polly circuit breaker policy via `Microsoft.Extensions.Http.Resilience`. After a threshold of consecutive failures, the circuit opens and immediately rejects requests for a cooldown period — no network call is attempted. After the cooldown, a probe request tests recovery.

## Implementation

- The Standard Resilience Handler registered in `ServiceDefaults/Extensions.cs` via `AddStandardResilienceHandler()` includes a circuit breaker as part of the Polly pipeline (built on Polly v8).
- The circuit breaker monitors the failure-to-success ratio; when the failure rate exceeds the threshold within a sampling window, it transitions to Open state.
- In Open state, calls to CoreBankAPI throw `BrokenCircuitException` immediately — the Outbox processor catches this and increments `RetryCount` without waiting for a timeout.
- After the break duration elapses, the circuit moves to Half-Open and allows a single probe request through; success closes the circuit.
- This applies to all `HttpClient` instances registered via DI, including `HttpCoreBankApiClient`.

## Consequences

### Positive
- Prevents PaymentsAPI from overwhelming a recovering CoreBankAPI with retry traffic.
- Fail-fast behaviour keeps Outbox processor threads available for other partitions.

### Negative / Trade-offs
- Legitimate requests are rejected during the open window — acceptable because the Outbox retries later.
- Circuit breaker state is per-process; multiple PaymentsAPI instances each maintain independent circuits.

## Key takeaway

> The circuit breaker protects both the caller and the callee — fail fast, let the downstream recover, and retry from the Outbox later.
