# ADR-004: Leader election and partitioning for scalable message processing

**Date:** 2026-05-23  
**Status:** Accepted  
**Deciders:** Architecture team  

## Context

A single Outbox/Inbox processor is both a bottleneck and a single point of failure. Running multiple processors naively causes duplicate deliveries or out-of-order processing. We need horizontal scalability while preserving per-key ordering and exactly-once semantics.

## Decision

Partition messages using a consistent hash of the idempotency key. Each processor instance acquires a distributed lock per partition via Dapr's Redis lock store, ensuring only one processor handles a given partition at any time.

## Implementation

- `PartitionHelper.GetPartitionId` computes an FNV-1a hash of the idempotency key modulo the configured partition count.
- `OutboxProcessorBase` and `InboxProcessorBase` iterate all partitions in parallel, acquiring a lock named `{prefix}-partition-{id}` before processing.
- `DaprDistributedLockService` wraps Dapr's Redis-backed lock with automatic timeout cancellation (5/6 of lock expiry) to prevent work from exceeding the lock TTL.
- Partition count and lock expiry are configurable via `OutboxProcessingOptions` / `InboxProcessingOptions` (bound from `appsettings.json`).
- If a lock cannot be acquired (another instance holds it), the partition is skipped — no contention, no duplicate work.

## Consequences

### Positive
- Horizontal scalability: add more instances and they divide the partition space automatically.
- Per-partition ordering is preserved because each partition has a single owner at any time.

### Negative / Trade-offs
- Redis becomes a required dependency for coordination (provided by Dapr lock store component).
- Lock expiry must be tuned: too short causes premature release; too long delays failover.

## Key takeaway

> Partitioning by idempotency key + distributed locking gives you ordered, exactly-once processing that scales horizontally.
