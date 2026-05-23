# ADR-003: Distributed tracing and observability with OpenTelemetry

**Date:** 2026-05-23  
**Status:** Accepted  
**Deciders:** Architecture team  

## Context

With asynchronous Inbox/Outbox processing and Dapr pub/sub between services, a payment travels through multiple processes over several seconds. A green health check endpoint means nothing if the Outbox processor has silently stalled. Without end-to-end traces, diagnosing lost or delayed payments requires log correlation by hand across services.

## Decision

Instrument all services with OpenTelemetry (traces, metrics, structured logs) and collect them in Jaeger, launched automatically by the Aspire AppHost.

## Implementation

- `ServiceDefaults/Extensions.cs` registers OpenTelemetry with OTLP export for every service via `AddServiceDefaults`.
- Each processor (`InboxProcessorBase`, `OutboxProcessorBase`, `MessagingOutboxProcessor`) creates an `ActivitySource` and starts spans with `TraceParent` propagation from stored messages.
- `HttpCoreBankApiClient` manually propagates `traceparent`/`tracestate` headers on outgoing HTTP calls.
- The AppHost adds a Jaeger container with OTLP gRPC on port 4317; services receive the endpoint via `JAEGER_OTLP_ENDPOINT`.
- Custom span tags include `queue_duration_ms`, `idempotency.key`, `payment.amount`, and `outcome`.

## Consequences

### Positive
- A single trace shows the full payment lifecycle: API → Outbox → CoreBank Inbox → domain events → Payments Inbox.
- Stalled processors are visible as missing downstream spans.

### Negative / Trade-offs
- Jaeger is a development-time dependency; production would require a managed backend.
- Trace context must be stored in every message row, adding columns to the schema.

## Key takeaway

> Every asynchronous hop must propagate and restore trace context so the full payment journey is visible in one trace.
