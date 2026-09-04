---
title: 'Fix Dapr integration for replicated Aspire services'
type: 'bugfix'
created: '2026-08-30'
status: 'done'
review_loop_iteration: 0
baseline_commit: '79b1e51d44bbef65643d593e0a612d79e74adf84'
context:
  - '{project-root}/docs/bmad/constraints.md'
  - '{project-root}/docs/adr/ADR-014-replicated-local-topology-stable-ingress.md'
  - '{project-root}/docs/bmad/implementation-artifacts/epic-6-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Story 6.3 incorrectly requires one Dapr sidecar per API replica, but CommunityToolkit Aspire Hosting Dapr attaches its CLI process to the logical `ProjectResource`; Aspire replicas are runtime instances rather than child resources. The unsupported requirement blocks native Aspire replication even though Dapr now handles only pub/sub.

**Approach:** Amend the topology contract to one Dapr pub/sub adapter per logical API service, shared through Aspire's stable service proxy by both replicas, then run a focused topology spike proving both CoreBank replicas can publish and the Payments adapter can deliver CloudEvents through the logical Payments endpoint.

## Boundaries & Constraints

**Always:** Keep two API replicas, native Aspire load-balanced endpoints, ports 5294/5295, logical Dapr app ids, existing CloudEvent shapes/topic/retry behavior, PostgreSQL databases, and renewable Redis partition locks. Dapr remains pub/sub only: CoreBank replicas publish through the logical CoreBank adapter, and the Payments adapter delivers to the stable Payments proxy. Propagate the amended invariant through ADR-014, PRD FR-21, AD-13, epics, Epic 6 context, and Story 6.3.

**Ask First:** The shared adapter cannot accept publishes from both CoreBank replicas, cannot deliver through the stable Payments proxy, requires direct replica addressing, or changes observable CloudEvent/retry behavior.

**Never:** Add a custom gateway, fake per-replica child resources, fork/patch Aspire or CommunityToolkit for an invariant the demo no longer needs, restore Dapr locking/service invocation, or claim infrastructure high availability. This remains a concurrency and application-resilience demo.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|---------------|----------------------------|----------------|
| CoreBank publish | Transactions are processed by both replicas | Both publish through one logical Dapr adapter | Publish failure follows the existing outbox retry path |
| Payments delivery | Shared adapter receives transaction events | Events route through the stable Payments proxy to a replica | No replica address or alternate endpoint |
| Replica contention | Two app processes compete for partitions | PostgreSQL/Redis evidence identifies both processes | Dapr adapter count is irrelevant to lock proof |

</frozen-after-approval>

## Code Map

- `docs/adr/ADR-014-replicated-local-topology-stable-ingress.md` -- amend the accepted topology decision and trade-off explicitly.
- `docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-08-21/prd.md` -- update FR-21 from per-replica sidecars to per-logical-service pub/sub adapters.
- `docs/bmad/planning-artifacts/architecture/architecture-CoreBankDemo-2026-08-21/ARCHITECTURE-SPINE.md` -- update AD-13's runtime invariant.
- `docs/bmad/planning-artifacts/epics.md`, `docs/bmad/implementation-artifacts/epic-6-context.md` -- align Story 6.1/6.3 forward-looking wording.
- `docs/bmad/implementation-artifacts/spec-6-3-replicated-local-api-topology.md` -- apply the human amendment, remove the obsolete block, and return the story to `ready-for-dev` after the spike.
- `CoreBankDemo.AppHost/AppHost.cs` -- add two replicas per API using the supported shared logical Dapr resources; preserve native proxies.
- `CoreBankDemo.AppHost.Tests` or existing architecture-test project -- assert the generated topology contract if the Aspire model is testable without runtime startup.

## Tasks & Acceptance

**Execution:**
- [x] Amend ADR-014 and every forward-looking planning artifact to define one Dapr pub/sub adapter per logical API service.
- [x] Update Story 6.3's frozen contract and acceptance criteria exactly as authorized; retain every non-Dapr topology requirement.
- [x] Add `.WithReplicas(2)` for both regular-AppHost APIs and a focused topology assertion where the public Aspire model permits it.
- [x] Start the regular AppHost and submit enough partitioned payments to show both CoreBank replica identities publish through the shared adapter and transaction events reach PaymentsAPI through its stable proxy.
- [x] Record spike evidence in Story 6.3 and unblock it only if CloudEvent and retry behavior remain unchanged.

**Acceptance Criteria:**
- Given the regular AppHost, when its topology starts, then two healthy replicas of each API use one healthy Dapr pub/sub adapter per logical service and no Dapr lock or invocation path exists.
- Given transactions processed by both CoreBank replicas, when their outbox events publish, then the shared CoreBank adapter accepts both and the shared Payments adapter delivers through the stable Payments proxy.
- Given the amended artifacts, when Story 6.3 resumes, then no forward-looking requirement demands replica-unique Dapr sidecars or ports.

## Spec Change Log

- 2026-08-30: Implemented the shared logical Dapr adapter amendment, proved the 2x2 application topology and CloudEvent round trip, and returned Story 6.3 to ready-for-dev.
- 2026-08-30: Review hardened the amendment with an executable AppHost architecture guard, explicit ADR amendment history/adapter terminology, corrected Story 6.3 provenance and resource-count wording, and existing retry-test evidence.

## Verification

**Commands:**
- `dotnet build CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj` -- expected: replicated shared-adapter graph compiles.
- Start with `aspire-launch`, inspect with `aspire-mcp`, and execute the focused CloudEvent round trip -- expected: 2x2 API processes, two logical Dapr adapters total, both CoreBank replica identities represented, stable Payments delivery.
- `dotnet test CoreBankDemo.UnitTests.slnf` -- expected: Docker-free gate remains green.
- `git diff --check` -- expected: no whitespace errors.

**Results:**
- AppHost build passed.
- Aspire reported two healthy replicas of each API and one healthy Dapr CLI adapter per logical service.
- 80 partition-balanced payments produced 240 completed CloudEvents. CoreBank replica completion evidence split 108/132; Payments delivery split 157/83.
- PostgreSQL confirmed all 80 transactions and all 240 published/received events completed with zero retries, pending rows, or failures.
- `dotnet test CoreBankDemo.UnitTests.slnf --no-restore` passed after review: 538 tests passed, one pre-existing real-Redis test skipped by the Docker-free tier, and every measured project remained above 90% line coverage.
- Existing `DaprOutboxDeliveryStrategyTests` and `OutboxProcessorBaseTests` remained green, preserving publish-failure propagation into the kernel retry path.
- `git diff --check` passed.

## Suggested Review Order

**Runtime topology**

- Native Aspire replication is enabled without manufacturing per-replica Dapr resources.
  [`AppHost.cs:70`](../../../CoreBankDemo.AppHost/AppHost.cs#L70)

- Payments replication uses the same shared logical adapter and proxy model.
  [`AppHost.cs:110`](../../../CoreBankDemo.AppHost/AppHost.cs#L110)

**Architecture contract**

- The ADR defines shared adapters and explicitly rejects an infrastructure-HA claim.
  [`ADR-014-replicated-local-topology-stable-ingress.md:16`](../../adr/ADR-014-replicated-local-topology-stable-ingress.md#L16)

- AD-13 carries the amended invariant into the architecture spine.
  [`ARCHITECTURE-SPINE.md:111`](../planning-artifacts/architecture/architecture-CoreBankDemo-2026-08-21/ARCHITECTURE-SPINE.md#L111)

- FR-21 states the user-visible orchestration contract in product terms.
  [`prd.md:84`](../planning-artifacts/prds/prd-CoreBankDemo-2026-08-21/prd.md#L84)

**Story continuity and evidence**

- Epic stories now require logical adapters rather than replica-unique sidecars.
  [`epics.md:496`](../planning-artifacts/epics.md#L496)

- Epic context preserves the amended invariant for downstream story work.
  [`epic-6-context.md:21`](epic-6-context.md#L21)

- Story 6.3 records the successful spike and resumes ready-for-dev.
  [`spec-6-3-replicated-local-api-topology.md:102`](spec-6-3-replicated-local-api-topology.md#L102)

**Executable guard**

- Source-level assertions prevent replica, adapter, and stable-port declarations from regressing.
  [`NoDaprServiceInvocationArchitectureTests.cs:122`](../../../tests/CoreBankDemo.PaymentsAPI.Tests/NoDaprServiceInvocationArchitectureTests.cs#L122)
