---
title: 'Story 6.2: Renewable Redis distributed locking'
type: 'feature'
created: '2026-08-29'
status: 'done'
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
- [x] Tests first: replace Dapr adapter tests with deterministic tests for immediate acquisition, contention, workload execution, caller cancellation, handle-loss cancellation, handle disposal, logging, exception-to-`false` behavior, and Redis/NoOp DI selection.
- [x] Add the centrally pinned Redis client and distributed-lock packages; implement `RedisDistributedLockService` while preserving the existing public port and all Messaging call sites.
- [x] Register the named Aspire Redis client in both APIs and inject the AppHost Redis reference into both resources.
- [x] Remove the Dapr lock adapter, cooperative cutoff helper/tests, lockstore resource, sidecar references, component YAML, and stale lockstore documentation; retain all Dapr pub/sub code and components.
- [x] Add one real-Redis integration proof that holds a lock beyond its initial expiry and verifies a second contender cannot acquire until the first handle is released; keep infrastructure-dependent coverage clearly separated from the ordinary unit gate.
- [x] Run the regular AppHost from a clean local state and verify both APIs become healthy with Redis and Dapr pub/sub but without a Dapr lock component. Completed on 2026-08-30 through `aspire stop`, `aspire start`, and `aspire wait`: the graph contained exactly one healthy `redis`, healthy `corebank-api` and `payments-api` projects, and healthy `corebank-api-dapr-cli` and `payments-api-dapr-cli` sidecars, with no `lockstore` resource. Both APIs logged `RedisDistributedLockService` acquiring and releasing their partition locks. A unique payment (`story-6-2-runtime-20260830T093621Z-28656`) returned `202 Accepted`; its Payments outbox, CoreBank inbox, CoreBank `transaction.completed` messaging-outbox event, and Payments `transaction.completed` inbox event each contained exactly one `Completed` row with `RetryCount = 0` and no `LastError`. The two expected per-account `balance.updated` events also completed exactly once end to end, proving Dapr pub/sub remained active.

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

- 2026-08-30: Completed the previously blocked regular-AppHost runtime acceptance in a working devcontainer. Started the regular AppHost with the optional Dev Proxy disabled, waited through Aspire for Redis, both APIs, and both Dapr sidecars, and inspected the live graph: exactly one Redis resource was healthy and no lockstore resource existed. Runtime logs from both APIs showed `RedisDistributedLockService` acquiring and releasing all relevant Inbox/Outbox partition locks. Submitted unique payment `story-6-2-runtime-20260830T093621Z-28656`; PostgreSQL showed one completed, zero-retry, error-free Payments outbox row and CoreBank inbox row, plus exactly one completed `transaction.completed` event through both the CoreBank messaging outbox and Payments inbox. Both expected per-account `balance.updated` events also completed exactly once through those event stores. This closes the last unchecked task and supplies live proof of Redis locking plus Dapr pub/sub on the production composition.

- 2026-08-29: Resumed after an interrupted session. The core adapter (`RedisDistributedLockService.cs`), package pins, `IDistributedLockService` doc updates, AppHost/Program.cs wiring, and old-adapter/lockstore removal were already correct and are unchanged. Found and fixed four gaps before the story could be called done:
  1. `tests/CoreBankDemo.ServiceDefaults.Tests/Extensions/AddServiceDefaultsTests.cs` still referenced the deleted `DaprDistributedLockService` type and asserted the old DaprClient-gated DI selection — this was a **compile error**, not just a stale reference. Replaced the two tests with `IConnectionMultiplexer`-gated equivalents asserting resolution to `RedisDistributedLockService` / `NoOpDistributedLockService`, matching `Extensions.cs`'s actual registration logic.
  2. `CoreBankDemo.ServiceDefaults.csproj` granted `InternalsVisibleTo` to the test project only, not to `DynamicProxyGenAssembly2`. Moq/Castle DynamicProxy cannot mock the internal `IRedisDistributedLockFactory` seam without it — every mocked test in `RedisDistributedLockServiceTests.cs` failed at runtime with `ArgumentException: Can not create proxy for type ... because it is not accessible`. Added the same `InternalsVisibleTo` entry already used by `CoreBankDemo.CoreBankAPI`/`CoreBankDemo.PaymentsAPI` for their own internal mocks.
  3. `AppHost.cs`, `Extensions.cs`, and `CoreBankDemo.ServiceDefaults.csproj` had been rewritten with CRLF line endings on every line (rest of the repo is LF-only), which `git diff --check` reported as trailing-whitespace errors across dozens of lines. Normalized all three files back to LF; also removed one pre-existing trailing-whitespace line in `AppHost.cs` (`.WithReference(coreBankApi)` had trailing spaces before this story touched that hunk).
  4. Stale prose references to the removed Dapr lock adapter/lockstore in currently-active (non-historical) docs: `README.md`'s Security Notes still listed the deleted `dapr/components*/lockstore-redis.yaml` files; `ARCHITECTURE.md`'s directory listing still showed `DaprDistributedLockService.cs`; `DaprEventPublisher.cs`'s XML doc comments still `cref`'d the deleted type. Updated all three. Left untouched: mentions inside frozen historical spec/ADR/retrospective docs (spec-2-6, spec-3-1/3-3/3-4, ADR-004, epic-2-retrospective, deferred-work.md) and inside `CoreBankDemo.Messaging.Tests` comments explaining story-2.6's historical test design — these describe what was true at the time and match the spec's own "historical frozen artifacts may still describe Story 3.2" caveat.
  Also removed the now-dead `Microsoft.Extensions.TimeProvider.Testing` package reference (and its stale story-3.2 comment) from `CoreBankDemo.ServiceDefaults.Tests.csproj` — nothing in that project uses it after the Dapr cooperative-cancellation tests were deleted.
  **Deviation left open:** the "run the regular AppHost from a clean local state" task/acceptance step could not be executed. A follow-up session installed the `aspire` and `dapr` CLIs (neither was present), added the bracketed `[::1]` IPv6 loopback literal to `NO_PROXY` (a known DCP/proxy interaction — DCP addresses its local API via `http://[::1]:<port>` and the sandbox's default `NO_PROXY` only covered unbracketed `::1`), and trusted the local ASP.NET Core dev cert via `SSL_CERT_DIR`. `docker.io` pulls, initially flaky, succeeded on retry (`dapr init` provisioned its placement/redis/zipkin/scheduler containers cleanly). Despite all of that, `aspire start` still fails: DCP's Kubernetes-style resource watch API times out after 20s on every resource type and DCP itself never logs a successful start, before or after any of the fixes above. This looks like a sandbox-level restriction on whatever DCP needs at the OS/container level, not a code, dependency, or proxy-config gap. Verified by static review only (see task list); an environment where `aspire run` is confirmed to work should still run it once before treating this story as fully proven end-to-end.

- 2026-08-29: Step-04 review (blind-hunter, edge-case-hunter, verification-gap layers) against the diff since baseline, scoped to this story's actual files (excluding a concurrently in-progress, unrelated story's uncommitted changes to shared files). Two real defects confirmed and patched; three findings rejected as out of scope (already-committed content from before this story's implementation, or a since-verified non-issue); the rest were pre-existing/out-of-scope or matched the frozen intent and were logged to `deferred-work.md` instead.
  1. **Patch (correctness, edge-case-hunter):** `RedisDistributedLockService.ExecuteWithLockAsync` could return `true` for a workload that completed without throwing even though `handle.HandleLostToken` had already fired concurrently (a workload that doesn't check its token, or that finishes right at the race boundary) — falsely reporting success under a lock that was no longer exclusively held. Fixed by checking `handle.HandleLostToken.IsCancellationRequested` immediately after a successful workload and returning `false` if ownership was lost, before the outer `return true`.
  2. **Patch (verification gap):** no test proved `Program.cs`'s actual `builder.AddRedisClient("redis")` call (in both `CoreBankAPI` and `PaymentsAPI`) resolves `IDistributedLockService` to the Redis adapter rather than silently falling back to `NoOpDistributedLockService` on a resource-name mismatch with `AppHost.cs`'s `redis` resource — `Program.cs` is excluded from the coverage gate, so a drift here would ship undetected as a total Inbox/Outbox processing outage. Added `RedisLockWiringTests.cs` to both `CoreBankDemo.CoreBankAPI.Tests` and `CoreBankDemo.PaymentsAPI.Tests`, each driving the real Aspire `AddRedisClient("redis")` call (with `abortConnect=false` so no live Redis is required) through the same sequence `Program.cs` uses.
  3. **Patch (cleanup, blind-hunter):** removed the unused `RedisDistributedLockFactoryForTests` nested class from `RedisDistributedLockServiceRealRedisTests.cs` — the test actually instantiates the production `RedisDistributedLockFactory` directly.
  4. **Rejected:** a claim that `CoreBankDemo.LoadTests/AppHost.cs` might still reference the deleted `lockstore-redis.yaml` — verified false; that file has no Dapr/Redis/lockstore wiring at all currently (out of this story's and this epic's scope).
  5. **Rejected:** claims about `epics.md` referencing an unwritten `ADR-015` and bundling new Story 6.5/7.4 planning content — verified these are uncommitted edits from a different, concurrently in-progress agent's work on an unrelated part of the shared `epics.md` file, pulled in only because this review's diff was scoped to the whole file rather than this story's specific hunks.
  6. **Rejected (matches frozen intent):** findings that the design has no backstop for a workload that never observes cancellation, and no documented lock-loss-to-`HandleLostToken` latency bound — the spec's own Boundaries & Constraints explicitly required removing the old 5/6 cutoff backstop and explicitly forbids making renewal cadence application-configurable ("renewal cadence remains an adapter/library concern"), so both are deliberate, not gaps.
  All three patches verified: `dotnet test CoreBankDemo.Rebuild.slnf` green (Messaging 154/154, PaymentsAPI 75/75 @ 100% line, CoreBankAPI 113/113 @ 98.57% line, ServiceDefaults 108/108 @ 98.22% line — all above the 90% gate), the real-Redis integration test passed against a local `redis-server`, and `git diff --check` clean on every touched file.

## Suggested Review Order

**The Redis adapter itself**

- Entry point: the never-throw `ExecuteWithLockAsync` contract, now backed by a real Redis lease instead of a fixed Dapr expiry.
  [`RedisDistributedLockService.cs:44`](../../CoreBankDemo.ServiceDefaults/RedisDistributedLockService.cs#L44)

- The lock-loss race this review's patch closed: a workload that finishes without observing `HandleLostToken` no longer reports false success.
  [`RedisDistributedLockService.cs:80`](../../CoreBankDemo.ServiceDefaults/RedisDistributedLockService.cs#L80)

- `IRedisDistributedLockFactory`/`RedisDistributedLockFactory`: the internal seam isolating real Redis construction so unit tests substitute a fake handle.
  [`RedisDistributedLockService.cs:124`](../../CoreBankDemo.ServiceDefaults/RedisDistributedLockService.cs#L124)

**DI selection and production wiring**

- `AddServiceDefaults` resolves `IDistributedLockService` to the Redis adapter only when `IConnectionMultiplexer` is already registered, else falls back to the no-op.
  [`Extensions.cs:59`](../../CoreBankDemo.ServiceDefaults/Extensions.cs#L59)

- `CoreBankAPI`'s `AddRedisClient("redis")` must precede `AddServiceDefaults` and must name-match `AppHost.cs`'s `redis` resource.
  [`CoreBankDemo.CoreBankAPI/Program.cs:19`](../../CoreBankDemo.CoreBankAPI/Program.cs#L19)

- Same registration in `PaymentsAPI`, the second half of the wiring this story's own review found unverified in production.
  [`CoreBankDemo.PaymentsAPI/Program.cs:7`](../../CoreBankDemo.PaymentsAPI/Program.cs#L7)

- Updated public-port documentation: renewal, not a fixed cutoff, and what ownership loss now means for callers.
  [`IDistributedLockService.cs:8`](../../CoreBankDemo.ServiceDefaults/IDistributedLockService.cs#L8)

**AppHost topology**

- Both processing APIs get the shared `redis` reference and wait for it; the Dapr `lockstore` resource is gone from this file.
  [`AppHost.cs:38`](../../CoreBankDemo.AppHost/AppHost.cs#L38)

**Removed: the superseded Dapr adapter**

- `DaprDistributedLockService.cs` and `CooperativeLockCancellation.cs` (the 5/6-of-expiry cutoff mechanism) are deleted outright, along with `dapr/components/lockstore-redis.yaml` and its load-test counterpart — nothing to click through, confirm via `git diff --stat` that they're gone.

**Tests**

- The mocked unit-gate coverage for every I/O & Edge-Case Matrix row: acquisition, contention, cancellation, lock-loss, disposal, logging.
  [`RedisDistributedLockServiceTests.cs`](../../tests/CoreBankDemo.ServiceDefaults.Tests/DistributedLock/RedisDistributedLockServiceTests.cs)

- The real-Redis proof that a lease survives past its initial expiry and a contender is blocked until release — dynamically skips without a reachable Redis.
  [`RedisDistributedLockServiceRealRedisTests.cs`](../../tests/CoreBankDemo.ServiceDefaults.Tests/DistributedLock/RedisDistributedLockServiceRealRedisTests.cs)

- DI-selection proof against a manually mocked `IConnectionMultiplexer` — the synthetic-builder half of the coverage.
  [`AddServiceDefaultsTests.cs:126`](../../tests/CoreBankDemo.ServiceDefaults.Tests/Extensions/AddServiceDefaultsTests.cs#L126)

- This review's added coverage: drives the real `Program.cs` registration sequence per API, closing the "resource-name drift ships silently" gap.
  [`CoreBankDemo.CoreBankAPI.Tests/RedisLockWiringTests.cs:27`](../../tests/CoreBankDemo.CoreBankAPI.Tests/RedisLockWiringTests.cs#L27)

**Peripherals**

- Centrally pinned `Aspire.StackExchange.Redis` and `DistributedLock.Redis` versions.
  [`Directory.Packages.props`](../../Directory.Packages.props)
