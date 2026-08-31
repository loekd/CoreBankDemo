# Epic 7 Context: E6 — Load Harness Realignment

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

LoadTestSupport and the k6 acceptance harness must be brought back into conformance with the rebuilt CoreBankAPI/PaymentsAPI schemas so the five system invariants (exactly-once processing, zero message loss, balance conservation, terminal-state completeness, per-key ordering) can be machine-verified end-to-end against the new code, and that same proven reset/drain/assert workflow is then exposed through a standalone, presentation-safe terminal console the speaker uses to run live demo cues without touching raw requests, terminals, or dashboards. The harness conforms to the code, not the other way around — it must never force a code or assertion-semantics compromise to pass.

## Stories

- Story 7.1: Assertion API realignment
- Story 7.2: MCP server tools
- Story 7.3: k6 run and first full acceptance gate
- Story 7.4: Presentation-safe terminal demo console

## Requirements & Constraints

- The five invariants (constraints.md §1) are the non-negotiable acceptance bar: no idempotency key processed twice; every accepted payment reaches a terminal state (submitted == processed); the sum of the 10 load-test account balances stays constant (10 × €10,000,000); zero `Failed`/`Pending`/`Processing` rows after drain; same-partition messages process in order, one worker at a time.
- Reset truncates the message stores and reseeds only the 10 `NL..LOAD` accounts — LoadTestSupport is their sole owner (seed + reset); it must not touch the 3 demo accounts owned by CoreBankAPI startup seeding.
- The load run uses the stable PaymentsAPI proxy endpoint against the default two-replica-per-API topology, disposable infrastructure, configurable transaction count/VUs, and a ~10% deliberate duplicate-key ratio (parameters carry over from `main` unless realignment forces a change).
- LoadTestSupport must expose reset, drain-polling, and assertion HTTP endpoints plus equivalent MCP tools (`reset_database`, `poll_until_drained`, `get_assertion_results`, inbox/outbox inspection) with identical semantics between the two surfaces.
- If harness expectations and rebuilt code conflict, the harness adapts — unless an invariant is genuinely violated, in which case it's a real bug, not a harness mismatch.
- Success bar: a full run is green with the 10% duplicate ratio and zero assertion relaxations made without an accompanying ADR.

## Technical Decisions

- **Delivery/status semantics (AD-11):** message `Status` is a transport state only. A business rejection (bad account, insufficient funds) completes successfully with a cached failure payload and a `TransactionFailed` event — it is never `Failed` and never retried; `Failed` means the transport exhausted retries. Assertions must respect this distinction.
- **Test tiers (AD-9/ADR-016):** pure assertion logic (drain/invariant calculations) is Tier 1 unit-tested with no Docker; persistence queries against seeded data are Tier 2, integration-tested against real PostgreSQL via Testcontainers (never SQLite/InMemory).
- **Replicated topology (AD-13):** both AppHosts run two replicas each of PaymentsAPI and CoreBankAPI behind Aspire's stable proxy (ports 5294 regular / 5295 load-test for PaymentsAPI; CoreBankAPI 5032; LoadTestSupport 5181). In the load-test graph, APIs run schema init while their processors wait on a load-test-only start gate; after API and LoadTestSupport health checks pass, a one-shot initializer resets the databases, releases every processor gate, and completes before k6 starts.
- **Dependency direction:** `LoadTestSupport` is the one project allowed to reference both APIs' `DbContext`s (assertion side-car exemption); it must not become a peer service the APIs depend on.
- **Presentation console (AD-14, ADR-015):** `CoreBankDemo.DemoRunner` is a standalone net10.0 console (Terminal.Gui, pinned centrally at 2.4.17) with no project reference to any banking implementation project, `DbContext`, or Redis/Dapr/container-engine client. It talks only through stable local HTTP endpoints and a fingerprinted, ownership-tracked Aspire child-process adapter, driven by a closed set of allow-listed action kinds (`selectTopology`, `waitForHealth`, `sendHttp`, `runAcceptedLoadWorkflow`, `assertHttp`, `openKnownUrl`, `speakerPause`). It reuses the 7.1–7.3 LoadTestSupport/k6 workflow for its load cue rather than inventing a parallel assertion path, gates narrative advancement on proven evidence (never elapsed time or log text), and never becomes a prerequisite for development, tests, or the banking services themselves. `demo-requests.http`/`payment-idempotency-tests.http` remain the unchanged manual fallback.

## UX & Interaction Patterns

The presentation console is explicitly not a banking UI and must stay outside the banking services' scope — it is a narrow, ADR-recorded exception to the product's broader "no UI" non-goal, existing solely as a local operator tool for running live demo cues.

## Cross-Story Dependencies

- Story 7.2's MCP tools are thin wrappers over Story 7.1's HTTP endpoints and must preserve identical semantics and structured outputs.
- Story 7.3 exercises Stories 7.1 and 7.2 together in a full Aspire + k6 acceptance run and is the first point all five invariants are proven against the rebuilt system.
- Story 7.4 has an approved dependency-gated sequencing: its scenario model, state machine, process ownership, adapters, and TUI may be implemented and tested behind stable ports/fakes in parallel with Stories 7.1–7.3. However, binding to the live LoadTestSupport endpoints, producing a successful five-invariant rehearsal proof pack, and marking Story 7.4 itself complete are all blocked until Stories 7.1–7.3 are done — this keeps the console from introducing a second, divergent acceptance workflow.
- Epic 7 depends on the replicated Aspire topology from Epic 6 (AD-13) and on the finalized DbContexts/schemas produced by the CoreBankAPI and PaymentsAPI epics.
