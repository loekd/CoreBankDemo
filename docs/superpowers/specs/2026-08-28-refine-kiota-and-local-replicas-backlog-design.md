# Refine Kiota and local replicas backlog stories

> **Status:** Implemented
> **Kind:** story spec
> **Original date:** 2026-08-28
> **Migrated from:** `docs/bmad/implementation-artifacts/spec-refine-kiota-and-local-replicas-backlog.md` on 2026-09-10
> **Related:** PR #2



## Intent

**Problem:** The backlog still describes a hand-written CoreBank HTTP client and single-instance local orchestration, so it does not capture the desired contract-driven integration or prove that partition ordering remains safe across competing service instances.

**Approach:** Replace Story 5.3 with a Kiota-specific client story and insert a separate Epic 6 story for two local replicas of each API. Update the PRD, architecture spine, epic coverage, numbering, and sprint tracking so the two additions are coherent and independently implementable.

## Boundaries & Constraints

**Always:** Keep a checked-in OpenAPI document as the Kiota input; generate the C# client during build without committing generated sources; cover every public CoreBankAPI operation; rely on generated-client compilation and tests rather than a CI live-contract diff. Run two PaymentsAPI and two CoreBankAPI replicas by default in both regular and load-test AppHosts; expose PaymentsAPI through Aspire's stable proxied endpoint; require proof that competing instances cannot process one partition concurrently and cannot reorder its messages.

**Ask First:** Changing frozen HTTP shapes, replacing Aspire's proxy with a gateway, changing the replica count, or changing the four-partition locking model.

**Never:** Expose the generated Kiota client directly as application logic, retain a parallel hand-written HTTP implementation, commit generated client sources, require lock-expiry takeover testing in the replica story, or weaken parallelism across different partitions.


## Code Map

- `docs/superpowers/specs/2026-08-21-corebank-rebuild-prd-design.md` -- FR-8, FR-21, FR-23, FR-24, FR-29 and NFR-1/NFR-5 define the affected product behavior and proof.
- `docs/superpowers/specs/2026-08-21-architecture-spine-design.md` -- AD-3, AD-6, AD-7, AD-9, and AD-12 govern the client port, generated contract boundary, locks, and test tiers.
- `docs/superpowers/specs/2026-08-21-epics-and-stories-design.md` -- Story 5.3, Epic 6 numbering, acceptance criteria, and FR coverage map are the backlog source.
- sprint-status.yaml (removed in the 2026-09-10 migration; see git history) -- must mirror renamed and inserted backlog story keys.
- `CoreBankDemo.PaymentsAPI/Outbox/ICoreBankApiClient.cs` -- existing application port to preserve while replacing its manual adapter.
- `CoreBankDemo.PaymentsAPI/Outbox/HttpCoreBankApiClient.cs` -- current manual paths, JSON mapping, and trace propagation that Kiota supersedes.
- `CoreBankDemo.CoreBankAPI/Controllers/AccountsController.cs` -- public account operations required in the checked-in contract.
- `CoreBankDemo.CoreBankAPI/Controllers/TransactionsController.cs` -- public transaction operations required in the checked-in contract.
- `CoreBankDemo.AppHost/AppHost.cs` -- regular topology, Dapr sidecars, proxy endpoints, and both API resources.
- `CoreBankDemo.LoadTests/AppHost.cs` -- currently targets a fixed external PaymentsAPI port and must join the replicated topology.
- `tests/CoreBankDemo.CoreBankAPI.Tests/{InboxProcessorTests,MessagingOutboxProcessorTests}.cs` -- existing lock seams and partition assertions to reference in replica acceptance criteria.

## Tasks & Acceptance

**Execution:**
- [x] `docs/superpowers/specs/2026-08-21-corebank-rebuild-prd-design.md` -- add contract-generated client and replicated local-processing requirements without changing frozen wire semantics.
- [x] `docs/superpowers/specs/2026-08-21-architecture-spine-design.md` -- define checked-in OpenAPI ownership, build-only Kiota generation behind `ICoreBankApiClient`, Aspire proxy ingress, and cross-instance partition-lock invariants.
- [x] `docs/superpowers/specs/2026-08-21-epics-and-stories-design.md` -- replace Story 5.3, insert replica Story 6.2, renumber chaos to 6.3, and update requirement coverage.
- [x] sprint-status.yaml (removed in the 2026-09-10 migration; see git history) -- replace the old 5.3/6.2 keys and add 6.3, all remaining backlog.

**Acceptance Criteria:**
- Given Epic 5 is inspected, when Story 5.3 is read, then it requires a checked-in OpenAPI contract covering all public CoreBankAPI endpoints, build-time Kiota generation, uncommitted generated sources, an adapter behind `ICoreBankApiClient`, trace propagation, response classification, and compilation/adapter tests.
- Given Epic 6 is inspected, when the replica story is read, then it requires two default replicas of both APIs in both AppHosts, stable Aspire-proxied PaymentsAPI ingress, healthy Dapr sidecars, and tests proving per-partition exclusivity and ordering across competing instances while allowing different partitions to run concurrently.
- Given planning and sprint artifacts are compared, when story identifiers and FR mappings are checked, then Story 5.3 and Stories 6.1-6.3 are uniquely and consistently represented with no stale superseded keys.

## Spec Change Log

## Design Notes

Kiota remains an outbound adapter behind the hexagonal port; generated transport models must not leak into handlers. The replica story belongs after the base Aspire graph and before chaos smoke because it hardens topology before faults are injected. Lock takeover after expiry remains covered by kernel failure-path work rather than this story.

## Verification

**Commands:**
- `git diff --check` -- expected: planning edits contain no whitespace errors.
- `test "$(grep -c '^### Story 5\.3:' docs/superpowers/specs/2026-08-21-epics-and-stories-design.md)" -eq 1 && test "$(grep -c '^### Story 6\.[123]:' docs/superpowers/specs/2026-08-21-epics-and-stories-design.md)" -eq 3` -- expected: revised story numbers are unique.
- `grep -q '^  5-3-contract-generated-kiota-corebank-client: backlog$' sprint-status.yaml (removed in the 2026-09-10 migration; see git history) && grep -q '^  6-2-replicated-local-api-topology: backlog$' sprint-status.yaml (removed in the 2026-09-10 migration; see git history) && grep -q '^  6-3-chaos-opt-in-and-demo-smoke: backlog$' sprint-status.yaml (removed in the 2026-09-10 migration; see git history)` -- expected: sprint keys match the backlog.
- `! grep -qE '^  5-3-corebank-http-client:|^  6-2-chaos-opt-in-and-demo-smoke:' sprint-status.yaml (removed in the 2026-09-10 migration; see git history)` -- expected: superseded sprint keys are absent.

## Suggested Review Order

**Contract-generated CoreBank client**

- Start with the implementable Kiota story and its complete transport-boundary acceptance criteria.
  [`epics.md:387`](2026-08-21-epics-and-stories-design.md)

- Review contract ownership, intermediate generation, adapter isolation, tracing, and response classification.
  [`ARCHITECTURE-SPINE.md:64`](2026-08-21-architecture-spine-design.md)

- Confirm the product requirement covers generation, all public operations, and test expectations.
  [`prd.md:53`](2026-08-21-corebank-rebuild-prd-design.md)

**Replicated local topology**

- Review the new independently implementable replica story before the renumbered chaos story.
  [`epics.md:466`](2026-08-21-epics-and-stories-design.md)

- Inspect the stable-ingress topology decision and unchanged four-partition model.
  [`ARCHITECTURE-SPINE.md:107`](2026-08-21-architecture-spine-design.md)

- Confirm cross-instance exclusivity, ordering, and parallel-partition proof requirements.
  [`ARCHITECTURE-SPINE.md:83`](2026-08-21-architecture-spine-design.md)

- Verify regular and load-test topology requirements share one stable PaymentsAPI ingress.
  [`prd.md:84`](2026-08-21-corebank-rebuild-prd-design.md)

**Coverage and tracking**

- Check FR coverage moved chaos to 6.3 and added replica ownership.
  [`epics.md:78`](2026-08-21-epics-and-stories-design.md)

- Confirm both architecture decisions are queued for permanent ADRs.
  [`epics.md:559`](2026-08-21-epics-and-stories-design.md)

- Finish with unique backlog keys for renamed and inserted stories.
  sprint-status.yaml (removed in the 2026-09-10 migration; see git history)
