# ADR-017: Propagate complete W3C trace context through Dapr pub/sub

**Date:** 2026-08-31
**Status:** Accepted
**Deciders:** Architecture team
**Supersedes:** The traceparent-only `IEventPublisher` signature from Story 3.3

## Context

CoreBank persists both W3C `TraceParent` and `TraceState` on Messaging Outbox rows. The Dapr publisher port accepted only `TraceParent`, so vendor state was discarded at the CoreBank-to-Payments hop even though the durable message retained it. This prevented a full two-hop trace-continuity proof.

## Decision

`IEventPublisher.PublishAsync` accepts nullable `traceParent` and `traceState` values. `DaprEventPublisher` maps non-blank values to `cloudevent.traceparent` and `cloudevent.tracestate` metadata. `DaprOutboxDeliveryStrategy` passes both values directly from the persisted outbox row.

No processing, retry, envelope identity, or banking behavior changes. Blank values are omitted rather than emitted as empty CloudEvent metadata.

## Consequences

- Distributed traces retain complete W3C context across the HTTP and Dapr hops.
- The publisher port has a deliberate breaking signature change for internal callers and tests.
- Existing consumers continue to receive standard Dapr CloudEvents; only trace metadata is additive.
