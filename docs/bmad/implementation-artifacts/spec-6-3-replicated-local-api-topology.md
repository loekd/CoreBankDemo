---
title: 'Story 6.3: Replicated local API topology'
type: 'feature'
created: '2026-08-29'
updated: '2026-08-30'
status: 'done'
review_loop_iteration: 1
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
- `CoreBankDemo.LoadTestInitializer/Program.cs` -- one-shot AppHost resource calls `/reset` and must complete before k6 starts.
- `k6/script.js` -- verifies healthy endpoints after the AppHost-owned initializer completes; it never owns reset ordering.

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.ServiceDefaults/*ProcessorStartGate*.cs`, `Extensions.cs` -- add an always-open default and a load-test Redis subscriber/publisher with idempotent local release.
- [x] `CoreBankDemo.Messaging/InboxProcessorBase.cs`, `OutboxProcessorBase.cs` plus processor tests -- inject the gate and prove zero ticks before release, normal cancellation, and unchanged default startup.
- [x] API startup files plus persistence tests -- serialize schema creation and CoreBank seeding through a PostgreSQL advisory lock on the same open connection; prove concurrent startup on fresh databases.
- [x] Both AppHost files -- configure two replicas per API, one Dapr pub/sub adapter per logical service, stable ingress, disposable load infrastructure, shared dependencies, and health ordering.
- [x] `ResetEndpoints.cs` plus LoadTestSupport tests -- complete both existing database resets, then broadcast release; never release after either reset fails.
- [x] Persistence/Redis acceptance tests -- run two real processor instances and capture processor identity to prove same-partition exclusivity/order and different-partition concurrent progress.
- [x] Configuration and architecture guards -- enforce partition count four and reject replica-address, obsolete lockstore, or alternate transport wiring.
- [x] Run both AppHosts and verify healthy replicas, shared logical Dapr adapters, stable ports, reset-before-processing, and unchanged contracts. Automated startup was blocked by sandbox DCP loopback policy; the user accepted the recorded build/integration evidence and retained the live AppHost run as a human follow-up outside this workflow.

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

- 2026-08-30: Implemented the replicated load graph, AppHost-owned reset initializer, atomic Redis generation/release protocol with exact replica acknowledgements, one-shot reset safety, advisory-locked startup, four-partition guards, and real PostgreSQL/Redis replica evidence. Runtime verification remains blocked by sandbox DCP network policy.

## Review Triage Log

- 2026-08-30: Accepted patch findings added retryable gate registration/marker recovery, bounded acknowledgement retry, rejection of zero-participant publishers, deterministic gate and overlap tests, serialized global-key Redis tests, corrected subscribe-before-marker documentation, and first-class solution inclusion for the initializer.
- 2026-08-30: Deferred four Story 6.5 observability findings surfaced because this story's historical baseline predates 6.5, plus the user-owned live Aspire verification blocked by sandbox DCP policy.
- 2026-08-30: Rejected replica-restart participant cleanup and run-scoped Redis keys because the load AppHost owns disposable Redis and intentionally fails closed when the expected pre-release process set changes; rejected source-text topology limitations because live acceptance remains an explicit human follow-up.

## Design Notes

The release channel is control-plane only: subscribers open a local one-way gate and never transport business data. A new replica subscribes before checking the current Redis generation marker, so a release cannot fall between those operations and cause a missed-publish deadlock. `/reset` advances that marker only after database reset succeeds. PostgreSQL advisory locking is scoped to schema initialization and uses an open application connection so `EnsureCreatedAsync` executes under the same session lock.

## Verification

**Commands:**
- `dotnet test CoreBankDemo.UnitTests.slnf` -- expected: Docker-free gate green at >=90% line coverage.
- `dotnet test CoreBankDemo.IntegrationTests.slnf` -- expected: PostgreSQL/Redis persistence and contention proofs green.
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: combined rebuild gate green.
- Start both AppHosts with the `aspire-launch` skill and inspect via `aspire-mcp` -- expected: 2x2 healthy API processes, one healthy Dapr pub/sub adapter per logical API service, stable ports, and no pre-release processing in the load graph.
- `git diff --check` -- expected: no whitespace errors.

## Auto Run Result

Status: done
Blocking condition: none. Live Aspire verification is a user-owned follow-up because the execution sandbox denies DCP's loopback Kubernetes API traffic.

Implementation verification:

- `dotnet test CoreBankDemo.UnitTests.slnf`: 629 passed, one pre-existing opt-in Redis test skipped; every measured logic project remained above 90% line coverage.
- `dotnet test CoreBankDemo.IntegrationTests.slnf`: 157 passed; total measured line coverage 98.41%.
- `dotnet test CoreBankDemo.Rebuild.slnf`: 786 passed, one pre-existing opt-in Redis test skipped.
- Both AppHost projects build with zero errors. Existing MessagePack vulnerability warnings remain unchanged.
- `git diff --check`: passed.
- `aspire start` was attempted in regular and isolated modes after installing workspace-local tooling. Both attempts reached DCP and failed before resource creation because sandbox network policy denied DCP's loopback API traffic.

The focused regular-AppHost spike passed with the amended topology:

- `aspire describe` reported two healthy `corebank-api` project instances, two healthy `payments-api` project instances, and exactly one healthy `*-dapr-cli` adapter for each logical API service.
- Both Payments replica descriptions exposed the same logical HTTP proxy endpoint. The Payments Dapr adapter started with that same proxy port as its `--app-port`, proving subscription delivery targets the stable logical service rather than a replica address.
- 80 payments were submitted through the logical Payments proxy, 20 in each partition. Both CoreBank replica resources completed messaging-outbox work: `corebank-api-drrezkye` recorded 108 completed outbox updates and `corebank-api-xcgscpbz` recorded 132, totaling the expected 240 CloudEvents.
- The two Payments replicas stored 157 and 83 delivered events respectively. PostgreSQL confirmed 80 completed CoreBank inbox rows, 240 completed CoreBank outbox rows, and 240 completed Payments inbox rows, with zero pending or failed rows.
- Event counts and retry state were unchanged end to end: 80 `com.corebank.transaction.completed` plus 160 `com.corebank.account.balance.updated`, all with `RetryCount = 0` in both the publishing and receiving stores.

The earlier spike used Aspire's isolated mode because the environment was shared, so host ports were dynamically remapped; it unblocked the shared-Dapr design before the remaining Story 6.3 implementation began.

## Suggested Review Order

**Replicated topology**

- Start with the disposable two-replica topology, shared adapters, reset initializer, and k6 dependency chain.
  [`AppHost.cs:40`](../../../CoreBankDemo.LoadTests/AppHost.cs#L40)

**Startup coordination**

- Follow the generation-based Redis barrier, participant registration, acknowledgement, and missed-broadcast recovery.
  [`ProcessorStartGate.cs:78`](../../../CoreBankDemo.ServiceDefaults/ProcessorStartGate.cs#L78)

- See how reset commits both databases before atomically releasing all processor participants.
  [`DatabaseResetCoordinator.cs:55`](../../../CoreBankDemo.LoadTestSupport/DatabaseResetCoordinator.cs#L55)

- Confirm the one-shot initializer invokes reset before k6 can start.
  [`Program.cs:15`](../../../CoreBankDemo.LoadTestInitializer/Program.cs#L15)

- Verify inbox and outbox workers await the shared gate before processing.
  [`OutboxProcessorBase.cs:102`](../../../CoreBankDemo.Messaging/OutboxProcessorBase.cs#L102)
  [`InboxProcessorBase.cs:116`](../../../CoreBankDemo.Messaging/InboxProcessorBase.cs#L116)

**Replica-safe persistence**

- Review advisory locking around CoreBank schema initialization and seeding.
  [`CoreBankDatabaseInitializer.cs:10`](../../../CoreBankDemo.CoreBankAPI/CoreBankDatabaseInitializer.cs#L10)

- Review matching advisory locking around Payments schema initialization.
  [`PaymentsDatabaseInitializer.cs:10`](../../../CoreBankDemo.PaymentsAPI/PaymentsDatabaseInitializer.cs#L10)

**Integration evidence**

- Prove same-partition contention, durable ordering, and cross-partition overlap on real PostgreSQL.
  [`ReplicatedCoreBankOutboxProcessorTests.cs:24`](../../../tests/CoreBankDemo.Persistence.IntegrationTests/CoreBankApi/ReplicatedCoreBankOutboxProcessorTests.cs#L24)

- Prove the Redis broadcast releases four participants and recovers late waiters.
  [`ProcessorStartGateIntegrationTests.cs:13`](../../../tests/CoreBankDemo.Persistence.IntegrationTests/ServiceDefaults/ProcessorStartGateIntegrationTests.cs#L13)

- Prove reset releases exactly four real gates only after both database commits.
  [`LoadTestDatabaseResetterTests.cs:117`](../../../tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/LoadTestDatabaseResetterTests.cs#L117)
