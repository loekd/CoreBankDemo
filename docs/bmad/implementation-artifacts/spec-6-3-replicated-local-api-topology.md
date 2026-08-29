---
title: 'Story 6.3: Replicated local API topology'
type: 'feature'
created: '2026-08-29'
status: 'draft'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/constraints.md'
  - '{project-root}/docs/bmad/planning-artifacts/architecture/architecture-CoreBankDemo-2026-08-21/ARCHITECTURE-SPINE.md'
  - '{project-root}/docs/bmad/implementation-artifacts/epic-6-context.md'
  - '{project-root}/docs/adr/ADR-014-replicated-local-topology-stable-ingress.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Every API today runs as one process, so partition locking, exclusive ownership, and durable enqueue order have never been proven under real inter-process contention — only within a single process.

**Approach:** Run two replicas of both PaymentsAPI and CoreBankAPI, in both the regular AppHost and the LoadTests AppHost (rebuilt from its current pre-baseline stub), sharing each service's database, Redis lock store, and Dapr app id while sidecar/runtime ports stay unique. Add a load-test-only processor start gate plus a one-shot reset/release initializer for the LoadTests AppHost. Prove cross-replica exclusivity and durable order against real Postgres+Redis using CoreBankAPI's existing Inbox/Outbox processors — PaymentsAPI gets replicated and lock-ready but has no hosted processor yet (stories 5.4–5.6), so it is not part of the exclusivity proof.

## Boundaries & Constraints

**Always:** Two replicas of PaymentsAPI and two of CoreBankAPI in both AppHosts; replicas of one service share its Postgres database, the shared `redis` resource, and one Dapr app id, while sidecar/runtime ports remain replica-unique. PartitionCount stays 4 everywhere and existing external HTTP shapes (ports 5294/5295, request/response contracts) are unchanged. Clients and k6 reach PaymentsAPI only through Aspire's stable proxied endpoint, never a replica address; PaymentsAPI resolves CoreBankAPI through Aspire's logical `corebank-api` endpoint. Both replicas of a service must start reliably against an empty database without racing schema creation. In the LoadTests AppHost, every hosted Inbox/Outbox processor waits behind a start gate that is open by default everywhere except there; a one-shot initializer runs after API + LoadTestSupport health, resets the databases, and releases every gate before k6 starts. The cross-replica exclusivity/ordering proof runs against real Postgres and the real Redis lock adapter (story 6.2) using CoreBankAPI's existing `InboxProcessor`/`MessagingOutboxProcessor`.

**Ask First:** Any change to a frozen external port, request/response shape, or CloudEvent type; any change to `PartitionCount`; adding a gateway/reverse proxy of our own in front of PaymentsAPI; if `WithReplicas` + `WithDaprSidecar` together don't produce one sidecar per replica process cleanly in this Aspire/CommunityToolkit version, stop and ask before working around it.

**Never:** Build PaymentsAPI's forwarding/event-handling processors (stories 5.4–5.6 own that) — PaymentsAPI replication here is wiring-only readiness, not new business logic. Reprove lock-expiry takeover (story 2.6 owns it). Make the load-test processor gate the default-closed state anywhere outside the LoadTests AppHost.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|---------------|----------------------------|----------------|
| Two replicas start against empty DB | Fresh Postgres, both replicas boot concurrently | Schema is created exactly once; both replicas reach healthy | No duplicate-relation / DDL race errors |
| LoadTests gate default state | Hosted processor starts under LoadTests config | Processor waits, ticks zero times | No tick observed before release |
| Regular AppHost gate default state | Hosted processor starts under regular config | Processor ticks immediately, unaffected by the gate | N/A |
| One-shot initializer runs twice | Reset/release called again after first release | Databases reset again; gate release is idempotent (already-open stays open) | No exception from re-releasing an open gate |
| Two replicas contend the same partition | Both CoreBankAPI replicas race to claim the same lock name | Exactly one proceeds; the other observes it busy and skips | Matches story 6.2's non-blocking `false` contract |
| Two replicas, different partitions | Each replica claims a different partition's lock | Both make progress concurrently | N/A |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.AppHost/AppHost.cs:64,103` -- add `.WithReplicas(2)` to `coreBankApi` and `paymentsApi`; verify `WithDaprSidecar` attaches one sidecar per replica process (open question from investigation — confirm during implementation, not assumed).
- `CoreBankDemo.LoadTests/AppHost.cs` (currently a 44-line stub with no Dapr/Redis/own Postgres, hardcoded connection string to the regular AppHost's DB) -- rebuild with its own `postgres`/`redis`/Dapr `pubsub` resources and both APIs at `.WithReplicas(2)`, mirroring the regular AppHost's resource shape; keep `loadtest-support` (5181) and `k6` as-is.
- `CoreBankDemo.ServiceDefaults/IProcessorStartGate.cs` (new) -- small port: `Task WaitUntilOpenAsync(CancellationToken)` plus a release method; a no-op always-open implementation is the default everywhere.
- `CoreBankDemo.Messaging/InboxProcessorBase.cs`, `OutboxProcessorBase.cs` -- inject `IProcessorStartGate`; `await gate.WaitUntilOpenAsync(stoppingToken)` at the top of `ExecuteAsync`'s loop, before the first tick.
- `CoreBankDemo.CoreBankAPI/Program.cs`, `CoreBankDemo.PaymentsAPI/Program.cs` -- register the real closed-by-default gate only under a LoadTests-only config switch (else the no-op); guard `EnsureCreatedAsync` against concurrent replicas (e.g. a short-lived Postgres advisory lock or `IDistributedLockService`-guarded section) so two replicas racing an empty database don't both attempt schema DDL.
- `CoreBankDemo.LoadTestSupport/Endpoints/ResetEndpoints.cs` -- extend (or add a sibling endpoint) so the one-shot initializer resets both databases and calls each API's gate-release, running after API + LoadTestSupport health and before k6.
- `tests/CoreBankDemo.Messaging.Tests` -- unit tests proving the gate blocks/releases `ExecuteAsync` ticks deterministically (fake gate).
- New acceptance-tier tests (real Postgres + real Redis, `[Trait("Category","Integration")]` pattern from `RedisDistributedLockServiceRealRedisTests.cs`) -- two concurrently-running `InboxProcessor`/`MessagingOutboxProcessor` instances proving same-partition exclusivity, durable order (including equal timestamps), and concurrent progress on different partitions.
- `CoreBankDemo.CoreBankAPI/appsettings.json:10,15` -- reconcile the existing `PartitionCount: 2` vs `4` inconsistency flagged during investigation; confirm 4 everywhere per this story's own invariant.

## Tasks & Acceptance

**Execution:**

- [ ] Add the `IProcessorStartGate` port and a no-op default implementation; wire it into `InboxProcessorBase`/`OutboxProcessorBase` with unit tests proving blocked-then-released tick behavior.
- [ ] Guard `EnsureCreatedAsync` in both `Program.cs` files against concurrent-replica schema races.
- [ ] Replicate `coreBankApi` and `paymentsApi` to 2 instances each in `CoreBankDemo.AppHost/AppHost.cs`; confirm Dapr sidecar and Redis/DB references still resolve correctly per replica.
- [ ] Rebuild `CoreBankDemo.LoadTests/AppHost.cs` with its own Postgres/Redis/Dapr pubsub and both APIs replicated, registering the real (closed-by-default) processor gate there only.
- [ ] Add the one-shot reset+release initializer to `CoreBankDemo.LoadTestSupport`, ordered after API/LoadTestSupport health and before k6 starts; add a focused test proving no processor tick occurs before release.
- [ ] Add the real-Postgres/real-Redis acceptance-tier proof of cross-replica exclusivity, durable order, and concurrent-different-partition progress using CoreBankAPI's existing processors.
- [ ] Reconcile `PartitionCount` to 4 everywhere in `CoreBankAPI/appsettings.json`.
- [ ] Run both AppHosts from a clean local state and verify healthy replicas, unchanged external ports, and unchanged HTTP contracts.

**Acceptance Criteria:**

- Given either AppHost, when its default topology starts, then two replicas each of PaymentsAPI and CoreBankAPI come up healthy with unique sidecar/runtime ports, sharing their service's database, Dapr app id, and the Redis lock store, against an empty database without a schema race.
- Given demo clients or k6, when they resolve PaymentsAPI, then they reach the one stable Aspire-proxied endpoint (5294 regular, 5295 load test) and PaymentsAPI resolves CoreBankAPI via Aspire's logical endpoint — never a replica address, never a new gateway.
- Given two CoreBankAPI replicas processing messages under real Postgres and the real Redis lock adapter, then at most one owns a given partition at a time, messages complete in durable enqueue order including ties, and both replicas demonstrably make progress on different partitions concurrently.
- Given the LoadTests AppHost preparing a run, when the one-shot initializer executes after health checks pass, then it resets both databases and releases every processor gate before k6 starts, and a focused test proves zero processor ticks occurred beforehand.

## Design Notes

The gate is an in-process signal (e.g. `TaskCompletionSource`-backed), not external state — the one-shot initializer releases it via a call into each API process (an internal endpoint or a shared release mechanism reachable from `CoreBankDemo.LoadTestSupport`), not a poll loop. Whether `WithReplicas` needs explicit per-replica port math given both APIs' fixed `launchSettings.json` ports (5294/5032) is an implementation-time verification, not a design decision — Aspire's DCP normally isolates each replica's actual bind address regardless of the declared launch profile port; confirm rather than assume.

## Verification

**Commands:**

- `dotnet build CoreBankDemo.Rebuild.slnf` -- expected: green.
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: all unit projects green, ≥90% line coverage maintained.
- Run the new acceptance-tier tests against real Postgres + Redis -- expected: exclusivity, ordering, and concurrent-progress assertions all pass.
- `aspire run --project CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj` and the LoadTests AppHost -- expected: both replicas of both services healthy, unchanged external contracts. (Note: this sandbox could not run `aspire run` end-to-end for story 6.2 due to a DCP-orchestrator startup failure unrelated to app code — expect the same limitation here; verify by static review plus the acceptance-tier tests if so, and flag for re-verification in a working environment.)

