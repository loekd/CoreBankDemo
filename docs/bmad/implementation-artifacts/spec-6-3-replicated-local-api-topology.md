---
title: 'Story 6.3: Replicated local API topology'
type: 'feature'
created: '2026-08-30'
status: 'ready-for-dev'
review_loop_iteration: 0
baseline_commit: '3b17e6b4e55c955e2b600212d60f6f3f85a27cef'
context:
  - '{project-root}/docs/bmad/constraints.md'
  - '{project-root}/docs/bmad/implementation-artifacts/epic-6-context.md'
  - '{project-root}/docs/adr/ADR-014-replicated-local-topology-stable-ingress.md'
warnings: [oversized]
deferred: []
---

<intent-contract>

## Intent

**Problem:** The demo currently runs one process per API, so cross-process partition exclusivity, durable ordering, and race-safe startup are not proven. The load-test AppHost also depends on the regular AppHost instead of owning disposable infrastructure.

**Approach:** Run two replicas of each API in both AppHosts behind stable Aspire ingress, with one Dapr pub/sub adapter per logical API service shared through Aspire's service proxy. Make startup schema creation race-safe, gate load-test processors until reset completes, and prove real PostgreSQL/Redis ordering and concurrency with the existing processors.

## Boundaries & Constraints

**Always:** Keep ports 5294/5295 and all HTTP/CloudEvent contracts unchanged. Replicas share each service database, logical Dapr app id, pub/sub, and Redis lock store. Each logical API service has one Dapr pub/sub adapter: both CoreBank replicas publish through the logical CoreBank adapter, and the Payments adapter delivers through the stable Payments proxy. Partition count is four. The regular AppHost starts processors immediately; the LoadTests AppHost closes them until reset succeeds. Use Redis pub/sub as a broadcast release signal so every replica opens its in-process gate without replica-specific routing. Preserve Dapr for pub/sub only and Kiota HTTP service-to-service calls.

**Block If:** The shared CoreBank adapter cannot accept publishes from both CoreBank replicas, the Payments adapter cannot deliver through the stable Payments proxy, satisfying the topology requires direct replica addressing or a custom gateway, or any frozen port, API shape, CloudEvent type, partition count, lock contract, or retry behavior must change.

**Never:** Add host-local locking as distributed coordination, address a replica directly, make the regular AppHost gate default-closed, build a second load assertion path, or reimplement Payments/CoreBank business processing.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|---------------|----------------------------|----------------|
| Concurrent empty startup | Two replicas share a fresh database | One serialized schema/seed path; both become healthy | No duplicate DDL or partial seed |
| Load setup | Processors closed; `/reset` succeeds | Both databases reset, Redis release broadcasts, all replicas open before k6 | Reset/release failure is non-200 and k6 never starts |
| Repeated setup | `/reset` runs twice | Reset and release remain idempotent | No double-release exception |
| Same partition | Two replica processors contend | One owner; durable enqueue order including tied timestamps | Contender skips without processing |
| Different partitions | Work is available in two partitions | Both replicas make concurrent progress | Neither partition starves |

</intent-contract>

## Code Map

- `CoreBankDemo.AppHost/AppHost.cs` -- existing Postgres/Redis/Dapr graph; add two replicas without changing stable endpoints.
- `CoreBankDemo.LoadTests/AppHost.cs` -- replace the hard-coded regular-AppHost connection strings with disposable Postgres/Redis/Dapr, replicated APIs, LoadTestSupport, and k6 dependency ordering.
- `CoreBankDemo.ServiceDefaults/IProcessorStartGate.cs`, `ProcessorStartGate.cs` -- add the gate port, always-open default, and Redis-backed load-test broadcast implementation.
- `CoreBankDemo.Messaging/InboxProcessorBase.cs`, `OutboxProcessorBase.cs` -- wait once before the first tick; preserve all existing loop semantics.
- `CoreBankDemo.CoreBankAPI/Program.cs`, `CoreBankDemo.PaymentsAPI/Program.cs` -- select the load-test gate from configuration and serialize `EnsureCreatedAsync`/seeding across replicas.
- `CoreBankDemo.LoadTestSupport/Endpoints/ResetEndpoints.cs` -- publish the release only after both database resets commit successfully.
- `CoreBankDemo.CoreBankAPI/appsettings.json` -- change the remaining Inbox partition count from 2 to 4.
- `tests/CoreBankDemo.Messaging.Tests`, `tests/CoreBankDemo.Persistence.IntegrationTests`, `tests/CoreBankDemo.LoadTestSupport.Tests` -- reuse processor harnesses, PostgreSQL assembly fixture, and real Redis lock setup.
- `k6/script.js` -- existing setup already calls health then `/reset`; keep this as the one-shot initializer and ensure failure aborts load.

## Tasks & Acceptance

**Execution:**
- `CoreBankDemo.ServiceDefaults/*ProcessorStartGate*.cs`, `Extensions.cs` -- add an always-open default and a load-test Redis subscriber/publisher with idempotent local release.
- `CoreBankDemo.Messaging/InboxProcessorBase.cs`, `OutboxProcessorBase.cs` plus processor tests -- inject the gate and prove zero ticks before release, normal cancellation, and unchanged default startup.
- API startup files plus persistence tests -- serialize schema creation and CoreBank seeding through a PostgreSQL advisory lock on the same open connection; prove concurrent startup on fresh databases.
- Both AppHost files -- configure two replicas per API, one Dapr pub/sub adapter per logical service, stable ingress, disposable load infrastructure, shared dependencies, and health ordering.
- `ResetEndpoints.cs` plus LoadTestSupport tests -- complete both existing database resets, then broadcast release; never release after either reset fails.
- Persistence/Redis acceptance tests -- run two real processor instances and capture processor identity to prove same-partition exclusivity/order and different-partition concurrent progress.
- Configuration and architecture guards -- enforce partition count four and reject replica-address, obsolete lockstore, or alternate transport wiring.
- Run both AppHosts and verify healthy replicas, shared logical Dapr adapters, stable ports, reset-before-processing, and unchanged contracts.

**Acceptance Criteria:**
- Given either AppHost, when it starts from empty infrastructure, then two healthy replicas of each API share logical dependencies, use one healthy Dapr pub/sub adapter per logical service, and expose one stable Payments endpoint.
- Given transactions processed by both CoreBank replicas, when their outbox events publish, then the shared CoreBank adapter accepts both and the shared Payments adapter delivers through the stable Payments proxy without changing CloudEvent or retry behavior.
- Given the LoadTests AppHost, when k6 setup calls `/reset`, then no processor tick occurred beforehand, both databases reset successfully, every replica receives the release broadcast, and k6 starts only afterward.
- Given two CoreBank processor instances with real PostgreSQL and Redis, when same-partition messages include equal ordering timestamps, then one replica owns the partition and completion follows durable enqueue order.
- Given work in different partitions, when both replicas run, then both instance identities record overlapping progress.
- Given a regular AppHost start, when no load-test configuration is present, then processors start without waiting and existing HTTP and Dapr pub/sub flows remain unchanged.

## Spec Change Log

- 2026-08-30: Human amendment replaced the unsupported per-replica Dapr-sidecar invariant with one pub/sub adapter per logical API service. Retained all application replication, stable ingress, startup, locking, ordering, and load-gate requirements.

- 2026-08-30: Replanned from current code after the course correction. Replaced replica-specific gate-release ambiguity with a Redis broadcast feeding per-process in-memory gates; corrected the stale claim that PaymentsAPI had no hosted processors; retained the human-approved topology and acceptance intent.

## Review Triage Log

## Design Notes

The release channel is control-plane only: subscribers open a local one-way gate and never transport business data. A new replica that starts after the release must determine the current run is open from a Redis generation marker before subscribing, avoiding a missed-publish deadlock. `/reset` advances that marker only after database reset succeeds. PostgreSQL advisory locking is scoped to schema initialization and uses an open application connection so `EnsureCreatedAsync` executes under the same session lock.

## Verification

**Commands:**
- `dotnet test CoreBankDemo.UnitTests.slnf` -- expected: Docker-free gate green at >=90% line coverage.
- `dotnet test CoreBankDemo.IntegrationTests.slnf` -- expected: PostgreSQL/Redis persistence and contention proofs green.
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: combined rebuild gate green.
- Start both AppHosts with the `aspire-launch` skill and inspect via `aspire-mcp` -- expected: 2x2 healthy APIs/sidecars, stable ports, and no pre-release processing in the load graph.
- `git diff --check` -- expected: no whitespace errors.

## Auto Run Result

Status: ready-for-dev
Blocking condition: none

The focused regular-AppHost spike passed with the amended topology:

- `aspire describe` reported two healthy `corebank-api` project instances, two healthy `payments-api` project instances, and exactly one healthy `*-dapr-cli` adapter for each logical API service.
- Both Payments replica descriptions exposed the same logical HTTP proxy endpoint. The Payments Dapr adapter started with that same proxy port as its `--app-port`, proving subscription delivery targets the stable logical service rather than a replica address.
- 80 payments were submitted through the logical Payments proxy, 20 in each partition. Both CoreBank replica resources completed messaging-outbox work: `corebank-api-drrezkye` recorded 108 completed outbox updates and `corebank-api-xcgscpbz` recorded 132, totaling the expected 240 CloudEvents.
- The two Payments replicas stored 157 and 83 delivered events respectively. PostgreSQL confirmed 80 completed CoreBank inbox rows, 240 completed CoreBank outbox rows, and 240 completed Payments inbox rows, with zero pending or failed rows.
- Event counts and retry state were unchanged end to end: 80 `com.corebank.transaction.completed` plus 160 `com.corebank.account.balance.updated`, all with `RetryCount = 0` in both the publishing and receiving stores.

The spike used Aspire's isolated mode because the environment is shared, so host ports were dynamically remapped; the unchanged AppHost endpoint declarations continue to own the documented non-isolated ports. Story 6.3 is unblocked and ready to resume its remaining non-Dapr topology work.
