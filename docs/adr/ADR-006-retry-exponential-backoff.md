# ADR-006: Retry with exponential backoff using Polly

**Date:** 2026-05-23  
**Status:** Accepted  
**Deciders:** Architecture team  

## Context

HTTP calls from PaymentsAPI to CoreBankAPI fail transiently due to container restarts, network blips, or DevProxy-injected errors (503, 429, 500). Without automatic retries, every transient failure forces the Outbox processor to wait for the next polling cycle (5 seconds) before retrying — adding unnecessary latency to payment processing.

## Decision

Apply Polly retry policies via `Microsoft.Extensions.Http.Resilience` on all outgoing HTTP clients. Retries use exponential backoff with jitter to avoid thundering-herd effects on a recovering downstream service.

## Implementation

- `Microsoft.Extensions.Http.Resilience` NuGet package is referenced in `CoreBankDemo.ServiceDefaults` and `CoreBankDemo.PaymentsAPI`.
- `ServiceDefaults/Extensions.cs` calls `http.AddStandardResilienceHandler()` inside `ConfigureHttpClientDefaults`, which registers a Polly pipeline including retry on all `HttpClient` instances.
- The Standard Resilience Handler retries on 5xx responses and network errors with exponential backoff (default: up to 3 attempts).
- The Outbox/Inbox layer provides a second retry tier: if all HTTP-level retries are exhausted, `RetryCount` is incremented and the message is retried on the next processor cycle (up to `MaxRetryCount` = 5).
- `HttpCoreBankApiClient` in PaymentsAPI benefits automatically — no retry code in the client itself.

## Consequences

### Positive
- Most transient failures resolve within the HTTP retry window, avoiding Outbox-level retry delays.
- Jitter prevents synchronized retry storms across multiple processor instances.

### Negative / Trade-offs
- Retries amplify load on a struggling CoreBankAPI; the circuit breaker (ADR-007) mitigates this.
- Default Polly settings may need tuning for specific SLA requirements.

## Key takeaway

> Retry at the HTTP layer handles seconds-scale blips; the Outbox handles minutes-scale outages — both layers are needed.
