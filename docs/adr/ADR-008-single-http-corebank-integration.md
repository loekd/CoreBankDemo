# ADR-008: Single HTTP integration from PaymentsAPI to CoreBankAPI

**Date:** 2026-08-29
**Status:** Accepted
**Deciders:** Architecture team
**Supersedes:** The Dapr-routing-switch statements in ADR-005; Dapr pub/sub remains accepted

## Context

The brownfield application exposed a `Features:UseDapr` switch for the PaymentsAPI-to-CoreBankAPI path, but no usable Dapr CoreBank client existed. Keeping two selectable transports would create two contracts, two retry paths, and a configuration branch that could not be tested honestly. DevProxy also needs this hop to remain ordinary HTTP so faults can be injected without changing application behavior.

## Decision

PaymentsAPI always calls CoreBankAPI over HTTP through the application-owned `ICoreBankApiClient` port. There is exactly one production adapter: the Kiota-backed client generated from CoreBankAPI's checked-in OpenAPI document.

The adapter resolves Aspire's logical `corebank-api` service endpoint, propagates `traceparent` and `tracestate`, and returns application-owned outcomes. Generated Kiota models do not cross the port. `Features:UseDapr`, `Features__UseDapr`, a Dapr CoreBank client, and parallel hand-written HTTP clients are not permitted.

Dapr remains in the architecture for the CoreBankAPI-to-PaymentsAPI CloudEvent pub/sub hop. This ADR changes only the forward command path.

## Implementation

- `CoreBankDemo.PaymentsAPI/Outbox/ICoreBankApiClient.cs` is the sole application port.
- `CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs` is the sole production adapter.
- `CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs` registers the logical `corebank-api` client through Aspire service discovery and the standard resilience pipeline.
- `CoreBankDemo.PaymentsAPI/Program.cs` registers that adapter without a transport feature branch.
- `CoreBankDemo.AppHost/AppHost.cs` must remove the remaining `Features__UseDapr` environment overrides while retaining Dapr sidecars for pub/sub.
- Adapter tests use an in-memory HTTP handler and cover all generated operations, trace headers, response classification, cancellation, and failures.

## Consequences

### Positive
- One testable transport contract and one production adapter.
- DevProxy can intercept the real application path.
- Dapr usage is explicit: pub/sub, not service invocation.

### Negative / Trade-offs
- HTTP resilience and service discovery remain application concerns rather than Dapr service-invocation concerns.
- Any future transport change requires a new ADR instead of a runtime flag.

## Key takeaway

> PaymentsAPI reaches CoreBankAPI through one Kiota-backed HTTP port; Dapr is reserved for the event hop.
