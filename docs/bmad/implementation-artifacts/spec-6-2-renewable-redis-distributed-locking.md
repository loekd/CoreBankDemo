---
title: 'Story 6.2: Renewable Redis distributed locking'
type: 'feature'
created: '2026-08-29'
status: 'ready-for-dev'
review_loop_iteration: 0
baseline_commit: '1bc3c56e4f63af22fc336cf838b636d91b3ec383'
context:
  - '{project-root}/docs/bmad/constraints.md'
  - '{project-root}/docs/bmad/planning-artifacts/architecture/architecture-CoreBankDemo-2026-08-21/ARCHITECTURE-SPINE.md'
  - '{project-root}/docs/bmad/implementation-artifacts/spec-3-2-distributed-lock-port-and-dapr-implementation.md'
  - '{project-root}/docs/adr/ADR-004-leader-election-partitioning.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The current Dapr lock adapter has no renewal path. It deliberately cancels work at 5/6 of a fixed lock expiry, so a slow but healthy batch must stop early and any work that fails to observe cancellation can outlive the lease. Dapr's lock component is also unnecessary indirection for this demo because Aspire already starts the Redis instance used by the application graph.

**Approach:** Replace only the Dapr-backed `IDistributedLockService` adapter with a .NET Redis adapter built on `DistributedLock.Redis`, using the existing Aspire-managed `redis` resource and its injected `IConnectionMultiplexer`. Keep the public lock port and every Messaging call site unchanged. Acquire without waiting, let the library automatically extend a held lock, pass the workload a token linked to both caller cancellation and the acquired handle's lock-loss token, and dispose the handle to release it. Keep Dapr for CloudEvent pub/sub.

## Boundaries & Constraints

**Always:** Preserve the exact `IDistributedLockService.ExecuteWithLockAsync(string, int, Func<CancellationToken, Task>, CancellationToken)` signature and its `true`/`false` meanings. Use `TryAcquireAsync(TimeSpan.Zero, cancellationToken)` so a busy partition is skipped rather than queued. Treat `lockExpirySeconds` as the Redis lease/extension duration; automatic renewal must not require a new processing option. Link `IDistributedSynchronizationHandle.HandleLostToken` with the ambient token for the workload. Dispose every acquired handle with `await using`. Catch, structure-log, and return `false` for acquisition, lock-loss, workload, Redis, and release failures, preserving the current never-throw boundary. Prefix Redis lock keys with an application namespace while preserving the existing per-store/per-partition lock-name identity. Pin all packages centrally. Keep one local Aspire Redis resource and make both processing APIs reference and wait for it.

**Ask First:** Changing the public lock interface; changing partition count, lock-name prefixes, or processor call sites; changing contention from immediate skip to waiting; adding a second Redis instance; changing the current never-throw result contract; or changing Dapr pub/sub.

**Never:** Use `Mutex`, `SemaphoreSlim`, or another process/host-local primitive as the runtime adapter—the replicated demo must coordinate separate API processes and containers. Remove `Dapr.Client` or the Dapr sidecars while `DaprEventPublisher` and subscriptions still use them. Add `LockRenewIntervalSeconds`; renewal cadence remains an adapter/library concern, not application configuration. Keep the Dapr `lockstore` component or obsolete 5/6 cooperative-cancellation code after the Redis adapter is active. Commit credentials or hard-code a Redis connection string.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|---------------|----------------------------|----------------|
| Lock acquired | Key is free and workload finishes normally | Workload runs once under the lock; handle is disposed; returns `true` | N/A |
| Contended partition | Another process holds the same key | Immediate non-blocking miss; workload does not run; returns `false` | Debug-level contention log, no throw |
| Work exceeds initial expiry | Workload remains healthy beyond `lockExpirySeconds` | Redis lease is extended and a second contender still cannot acquire | No 5/6 timeout cancellation |
| Caller cancellation | Ambient token is cancelled during acquire or work | Acquisition/work stops cooperatively; handle is released if acquired; returns `false` | Cancellation is logged below Error severity |
| Lock ownership lost | Redis handle signals `HandleLostToken` | Workload receives cancellation promptly and stops; returns `false` | Warning with lock name, no throw |
| Redis unavailable | Connect/acquire/extend/release fails | Workload is not started, or is cancelled if ownership was already lost; returns `false` | Structured Error log, no exception escapes |
| Workload throws | Workload faults while the lock is held | Handle is still disposed; returns `false` | Structured Error log, no throw |
| Local startup | `aspire run` starts the regular demo | Redis, both APIs, and Dapr pub/sub become healthy without a Dapr lock component | Connection supplied by Aspire, no manual Redis setup |

</frozen-after-approval>

## Architecture Decision Required

ADR-011 is the accepted record for this story's explicit human renegotiation of AD-7 and the locking portion of ADR-004. It supersedes those parts and records:

- direct Redis locking through `IDistributedLockService`, with Dapr retained only for pub/sub;
- automatic renewal while the handle is healthy, plus workload cancellation on `HandleLostToken`;
- why a host-local mutex is insufficient for replicated processes/containers;
- why renewal cadence is deliberately not exposed as an application option;
- the local-demo trade-off: Redis remains required, but Aspire provisions and connects it with one command.

Historical completed Story 3.2 and its frozen context remain unchanged; the new ADR and this story are the superseding record.

## Code Map

- `CoreBankDemo.ServiceDefaults/IDistributedLockService.cs` -- preserve the signature; update stale Dapr/no-renewal documentation only.
- `CoreBankDemo.ServiceDefaults/DaprDistributedLockService.cs` and `CooperativeLockCancellation.cs` -- remove after replacement.
- `CoreBankDemo.ServiceDefaults/RedisDistributedLockService.cs` -- add the direct Redis adapter; isolate lock construction behind a small internal factory only if needed for deterministic unit tests.
- `CoreBankDemo.ServiceDefaults/Extensions.cs` -- resolve the Redis adapter when `IConnectionMultiplexer` is registered; retain `NoOpDistributedLockService` for lock-free hosts and retain Dapr event-publisher wiring.
- `CoreBankDemo.ServiceDefaults/CoreBankDemo.ServiceDefaults.csproj` and `Directory.Packages.props` -- add centrally pinned `Aspire.StackExchange.Redis` `13.4.0` and `DistributedLock.Redis` `1.1.1`; retain `Dapr.Client` for event publishing.
- `CoreBankDemo.CoreBankAPI/Program.cs` and `CoreBankDemo.PaymentsAPI/Program.cs` -- register the Aspire Redis client named `redis` before lock resolution.
- `CoreBankDemo.AppHost/AppHost.cs` -- add `.WithReference(redis).WaitFor(redis)` to both processing APIs; remove the Dapr `lockstore` resource and sidecar references while keeping `pubsub`.
- `dapr/components/lockstore-redis.yaml` and `dapr/components-loadtest/lockstore-redis.yaml` -- remove once no AppHost or documentation references them.
- `tests/CoreBankDemo.ServiceDefaults.Tests/DistributedLock/` and `tests/CoreBankDemo.ServiceDefaults.Tests/Extensions/AddServiceDefaultsTests.cs` -- replace Dapr/cutoff tests with Redis adapter, handle-loss, failure, disposal, and DI tests; preserve interface and NoOp tests.
- `docs/adr/`, `ARCHITECTURE.md`, the architecture spine, and Epic 6 acceptance text -- record the superseding decision and remove claims that locks are Dapr-backed or never renewed.

## Tasks & Acceptance

**Execution:**

- [x] Architecture first: ADR-011 supersedes the old locking decision and current architecture/planning text is aligned without rewriting frozen completed-story history.
- [ ] Tests first: replace Dapr adapter tests with deterministic tests for immediate acquisition, contention, workload execution, caller cancellation, handle-loss cancellation, handle disposal, logging, exception-to-`false` behavior, and Redis/NoOp DI selection.
- [ ] Add the centrally pinned Redis client and distributed-lock packages; implement `RedisDistributedLockService` while preserving the existing public port and all Messaging call sites.
- [ ] Register the named Aspire Redis client in both APIs and inject the AppHost Redis reference into both resources.
- [ ] Remove the Dapr lock adapter, cooperative cutoff helper/tests, lockstore resource, sidecar references, component YAML, and stale lockstore documentation; retain all Dapr pub/sub code and components.
- [ ] Add one real-Redis integration proof that holds a lock beyond its initial expiry and verifies a second contender cannot acquire until the first handle is released; keep infrastructure-dependent coverage clearly separated from the ordinary unit gate.
- [ ] Run the regular AppHost from a clean local state and verify both APIs become healthy with Redis and Dapr pub/sub but without a Dapr lock component.

**Acceptance Criteria:**

- Given existing Inbox/Outbox processors, when the adapter is replaced, then `CoreBankDemo.Messaging` compiles with zero source changes and the exact `IDistributedLockService` reflection guard remains green.
- Given a free Redis lock key, when `ExecuteWithLockAsync` is called, then it acquires immediately, runs the workload exactly once, disposes the handle, and returns `true`.
- Given the same key is held elsewhere, when another processor calls `ExecuteWithLockAsync`, then it returns `false` promptly without running the workload or waiting for the holder.
- Given a workload runs longer than the initial expiry, when the holder and a second real Redis client are observed, then automatic extension preserves exclusive ownership until the holder releases; the old 5/6 cancellation does not fire.
- Given the handle reports lost ownership or the caller cancels, when the workload observes its token, then it is cancelled promptly, the handle is disposed, and no exception escapes the lock-service boundary.
- Given `aspire run`, when the local application graph starts, then Redis is provisioned once, both processing APIs receive the `redis` connection, and Dapr pub/sub works without any `lockstore` component.
- Given the repository is searched after implementation, then no production or active-orchestration reference to `DaprDistributedLockService`, `CooperativeLockCancellation`, `lockstore-redis.yaml`, or Dapr's lock API remains; `DaprEventPublisher` and pub/sub references remain.

## Design Notes

`DistributedLock.Redis` is preferred over a custom Redlock/renewal loop because its handle already extends ownership and exposes a lock-loss token. The adapter should configure the existing `lockExpirySeconds` as the library expiry and otherwise use library defaults unless the new ADR records a measured reason to override extension cadence. Lock names must remain stable across replicas and isolated across stores; an application prefix may be added once in the adapter, not recomputed differently by each caller.

Package/documentation references for implementation review:

- `DistributedLock.Redis`: <https://github.com/madelson/DistributedLock/blob/master/docs/DistributedLock.Redis.md>
- Aspire Redis client integration: <https://learn.microsoft.com/dotnet/aspire/database/redis-integration>

## Verification

**Commands:**

- `dotnet build CoreBankDemo.Messaging/CoreBankDemo.Messaging.csproj` -- expected: green with no Messaging source changes.
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: all unit projects green and every applicable logic project remains at or above 90% line coverage.
- Run the Redis integration proof against an Aspire-started or disposable local Redis instance -- expected: ownership survives beyond the initial expiry and transfers only after release/loss.
- `aspire run --project CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj` -- expected: one-command local startup; Redis, both APIs, Jaeger, and Dapr pub/sub healthy; no Dapr lockstore resource.
- `rg -n "DaprDistributedLockService|CooperativeLockCancellation|lockstore-redis|daprClient\\.(Lock|Unlock)" --glob '!docs/bmad/implementation-artifacts/spec-3-2-*' --glob '!docs/bmad/implementation-artifacts/epic-3-*'` -- expected: no active production/orchestration references; historical frozen artifacts may still describe Story 3.2.
- `git diff --check` -- expected: no whitespace errors.

## Spec Change Log
