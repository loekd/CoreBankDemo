# ADR-011: Renewable distributed locking through Aspire-managed Redis

**Date:** 2026-08-29
**Status:** Accepted
**Deciders:** Architecture team
**Supersedes:** ADR-004's Dapr lock adapter and fixed-expiry lifetime behavior; ADR-004's partitioning decision remains accepted

## Context

The Dapr distributed-lock API used by the rebuild exposes acquisition and expiry but no application renewal operation. The existing adapter therefore cancels work at 5/6 of the configured expiry. A slow but healthy batch must stop early, while a workload that does not observe cancellation can still outlive the lease. Aspire already provisions Redis for the local demo, so routing locks through a Dapr component adds indirection without providing renewal.

A named `Mutex`, `SemaphoreSlim`, or other host-local primitive cannot safely coordinate the separate processes and containers required by the replicated demo topology.

## Decision

`IDistributedLockService` remains the only application-visible locking port and keeps its existing signature and non-throwing `bool` result contract. Its production adapter uses `DistributedLock.Redis` over the `IConnectionMultiplexer` supplied by Aspire's named `redis` resource.

Acquisition is non-blocking: a busy partition is skipped immediately. While a handle is healthy, the library automatically extends the finite Redis lease. The workload receives a token linked to caller cancellation and the handle's `HandleLostToken`; loss of ownership therefore stops cooperative work promptly. Disposing the handle releases the lock.

`lockExpirySeconds` configures the lease duration. No `LockRenewIntervalSeconds` option is introduced: renewal cadence is an adapter/library concern and must not become dead application configuration. Dapr remains responsible for CloudEvent pub/sub only; the Dapr `lockstore` component is removed.

## Implementation

- Story 6.2, `spec-6-2-renewable-redis-distributed-locking.md`, owns the migration and its real-Redis renewal proof.
- `CoreBankDemo.ServiceDefaults/RedisDistributedLockService.cs` replaces `DaprDistributedLockService.cs` and `CooperativeLockCancellation.cs`.
- `CoreBankDemo.ServiceDefaults/IDistributedLockService.cs` retains its current public signature.
- `CoreBankDemo.AppHost/AppHost.cs` passes the shared Redis reference to both APIs and removes Dapr lockstore resources and sidecar references.
- `Aspire.StackExchange.Redis` and `DistributedLock.Redis` are pinned in `Directory.Packages.props`.
- Unit tests cover contention, cancellation, lock loss, disposal, logging, and the never-throw boundary; a real-Redis test proves ownership beyond the initial expiry.

## Consequences

### Positive
- Healthy long-running work keeps exclusive ownership without an arbitrary 5/6 cutoff.
- Lock loss is observable through a cancellation token.
- Local startup remains one command because Aspire owns Redis provisioning and connection injection.

### Negative / Trade-offs
- Redis remains a required coordination dependency.
- Renewal cannot guarantee safety during a Redis/network outage; workloads must honor the lock-loss token.
- The application takes a direct StackExchange.Redis dependency at the adapter boundary.

## Key takeaway

> Partition locks use finite Redis leases that renew while healthy and cancel work immediately when ownership is lost.
