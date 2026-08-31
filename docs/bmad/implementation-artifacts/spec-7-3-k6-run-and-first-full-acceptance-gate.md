---
title: 'Story 7.3: k6 run and first full acceptance gate'
type: 'feature'
created: '2026-08-31'
status: 'done'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/constraints.md'
  - '{project-root}/docs/adr/ADR-014-replicated-local-topology-stable-ingress.md'
baseline_commit: '303ac0f9e8dce0b13adfc7d859d22f877f49c3d1'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The rebuilt disposable topology has no green end-to-end proof. k6 checks do not affect exit status, terminal checks cover only CoreBank Inbox, ordering/exclusivity lacks acceptance evidence, and two-hop trace continuity is unproven under replicated load.

**Approach:** Harden reset -> k6 -> four-store drain -> assertions, then retain one reproducible green run. The final verdict combines fail-closed state checks with PostgreSQL/Redis ordering tests and live spans; classify every failure as code defect or harness mismatch.

## Boundaries & Constraints

**Always:** Enter after Stories 7.1/7.2 and ADR-014 topology tests pass. Run only `CoreBankDemo.LoadTests`. Preserve JSON fields and REST/MCP parity. Add all-store status counts, N/N/3N/3N cardinality, and exact-account checks. Any reset, setup, drain, endpoint, or state failure must exit non-zero. Combine exact-window traces with replicated Tier-2 Inbox/Outbox tests for ordering/exclusivity.

**Ask First:** Any weakening of the user-approved ordering proof (replicated Tier-2 tests plus live traces), or any trace-contract change beyond propagating the already-persisted W3C `TraceState` through the Dapr publisher port.

**Never:** Relax invariants; infer success from health/time/logs; start the regular AppHost; give k6 database access; alter banking behavior for stale harness assumptions; wire DevProxy/Story 6.4 or DemoRunner/Story 7.4; fix unrelated port drift.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|---------------|---------------------------|----------------|
| Green run | N unique plus floor(10%) duplicate submissions | k6 exits 0 after four-store drain; counts are N/N/3N/3N | Preserve structured evidence |
| Hidden terminal failure | Any store contains one `Failed` row | `noFailedMessages` and `allPassed` are false; k6 exits non-zero | Report the store/count |
| Ordering/exclusivity violation | Replicated Tier-2 ordering test fails or live same-store/partition spans overlap | Final story verdict fails even if k6 state checks pass | Include store, partition, message, and replica evidence |
| Drain/assert endpoint error | Timeout, non-2xx, or malformed JSON | Run fails closed | Keep diagnostics; never return success early |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.LoadTests/AppHost.cs:10-125` -- canonical single disposable graph; initializer completion gates automatic k6 startup.
- `k6/script.js:40-50,149-273` -- add fail-closed thresholds/teardown checks and consume every invariant result; current early returns can produce a zero exit.
- `CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs:17-108,134-223,264-354` -- add `MessageStoreSummary` values for Payments Outbox/CoreBank Inbox/CoreBank Outbox/Payments Inbox and `StageCardinality`/`CanonicalAccountSet` checks without renaming existing fields.
- `CoreBankDemo.LoadTestInitializer/Program.cs` and new `ResetResponseValidator.cs` -- reject semantically incomplete reset results before k6 starts.
- `CoreBankDemo.Messaging/{InboxProcessorBase,OutboxProcessorBase}.cs:256-280,431-455` -- add store/message span tags; do not change processing.
- `docs/adr/ADR-017-dapr-w3c-trace-context.md` (new), `CoreBankDemo.ServiceDefaults/IEventPublisher.cs`, `DaprEventPublisher.cs`, `CoreBankDemo.CoreBankAPI/Outbox/DaprOutboxDeliveryStrategy.cs` -- supersede the traceparent-only signature and propagate persisted `TraceState` as `cloudevent.tracestate`.
- `tests/CoreBankDemo.Persistence.IntegrationTests/CoreBankApi/{ReplicatedCoreBankOutboxProcessorTests,ReplicatedCoreBankInboxProcessorTests}.cs` -- real PostgreSQL/Redis replica evidence for both shared processor paths.
- `.claude/skills/load-test/SKILL.md`, `.claude/commands/run-load-tests.md`, `CoreBankDemo.LoadTests/README.md` -- align the executable workflow to the single LoadTests AppHost and fail-closed evidence sequence.

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs`, `tests/CoreBankDemo.LoadTestSupport.Tests/LoadTestAssertionServiceTests.cs`, and `tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/{AssertEndpointsIntegrationTests,AssertEndpointsHttpIntegrationTests}.cs` -- implement and prove four-store summaries, terminal checks, stage cardinality, exact accounts, and REST/MCP-compatible JSON.
- [x] `k6/script.js` and `tests/CoreBankDemo.LoadTestSupport.Tests/K6ScriptContractTests.cs` (new) -- threshold every critical check and cover all early-return paths so a state-gate failure exits non-zero.
- [x] `CoreBankDemo.LoadTestInitializer/{Program,ResetResponseValidator}.cs`, `tests/CoreBankDemo.LoadTestInitializer.Tests/*` (new), solution/filter files -- validate reset response counts/balances before k6 and cover malformed/incomplete results.
- [x] `docs/adr/ADR-017-dapr-w3c-trace-context.md`, publisher/strategy files, `tests/CoreBankDemo.ServiceDefaults.Tests/EventPublisher/{IEventPublisherSignatureTests,DaprEventPublisherTests}.cs`, and `tests/CoreBankDemo.CoreBankAPI.Tests/DaprOutboxDeliveryStrategyTests.cs` -- record and implement full W3C context propagation.
- [x] `CoreBankDemo.Messaging/{InboxProcessorBase,OutboxProcessorBase}.cs`, existing unit tests, and new `tests/CoreBankDemo.Persistence.IntegrationTests/CoreBankApi/ReplicatedCoreBankInboxProcessorTests.cs` -- tag spans and prove replicated Inbox ordering/exclusivity alongside the retained replicated Outbox test.
- [x] `.claude/skills/load-test/SKILL.md`, `.claude/commands/run-load-tests.md`, and `CoreBankDemo.LoadTests/README.md` -- document the single-AppHost run and separate k6-state and final trace/order verdicts.
- [x] `docs/bmad/implementation-artifacts/7-3-acceptance-evidence.md` (new) -- record configuration, timestamps, resource/k6 outcomes, final REST/MCP JSON, Tier-2 ordering results, representative trace IDs, and failure classifications.

**Acceptance Criteria:**
- Given the default disposable LoadTests topology and 100 unique payments with 10 deliberate duplicate submissions, when the full run completes, then k6 exits 0 and the four stores contain 100/100/300/300 completed rows with no non-terminal or failed rows.
- Given reset semantics are invalid, when initialization runs, then the initializer exits non-zero and k6 never starts.
- Given any setup, submission, drain, assertion-endpoint, or state check fails, when k6 terminates, then its resource exits non-zero and names the failed check.
- Given the final REST and MCP assertion calls target the same run, when compared, then they are field-for-field identical and report all four stores, stage cardinality, exact accounts, dedupe, and balance checks as passing.
- Given the replicated Tier-2 ordering tests pass and the exact run window is analyzed, when the final verdict is produced, then traces retain `traceparent`/`tracestate` across both hops, identify both replicas, and show no overlapping same-store/partition processing.
- Given `dotnet test CoreBankDemo.Rebuild.slnf`, when the hardened harness changes are verified, then both tiers pass their coverage gates before the distributed run is accepted.

## Spec Change Log

- 2026-08-31: User selected replicated Tier-2 tests plus live traces for ordering/exclusivity proof, avoiding new persistence schema, and approved a new ADR to propagate `tracestate` across Dapr.
- 2026-08-31: `host.docker.internal` proved to be an unreachable dead end for the k6 container in this execution sandbox (nested Docker daemon, no Docker-Desktop host-loopback bridging) — proven with a throwaway `0.0.0.0`-bound probe server the container could never reach regardless of target bind address. User approved, in order: (1) a new `loadtest` launch profile (`0.0.0.0` binding, same ports) added to `CoreBankDemo.PaymentsAPI`, `CoreBankDemo.CoreBankAPI`, and `CoreBankDemo.LoadTestSupport` `Properties/launchSettings.json`, selected only by `CoreBankDemo.LoadTests/AppHost.cs` via `launchProfileName: "loadtest"` — the existing `http`/`https` profiles (including PaymentsAPI's `127.0.0.1` binding that DevProxy's chaos plugin and `NoDaprServiceInvocationArchitectureTests` depend on) are untouched; (2) rewiring the `k6` container resource in `CoreBankDemo.LoadTests/AppHost.cs` to reference `paymentsApi.GetEndpoint("http")` / `loadTestSupport.GetEndpoint("load-test")` via `WithEnvironment(...)` instead of hardcoded `http://host.docker.internal:*` strings, which activates Aspire 13.3+'s built-in container tunnel — the documented, cross-platform replacement for `host.docker.internal`, portable to native-Linux Docker hosts generally, not just this sandbox. Neither change touches banking behavior, message-store semantics, or the regular `CoreBankDemo.AppHost` topology. See `docs/bmad/implementation-artifacts/7-3-acceptance-evidence.md` for the full diagnostic trail.

## Design Notes

k6 proves state, not the whole story. Ordering requires real PostgreSQL/Redis replica tests plus non-overlapping tagged live spans; a shared trace ID alone is insufficient.

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: all unit and PostgreSQL integration tests pass with coverage gates.
- `aspire start --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --non-interactive` -- expected: initializer and k6 complete in the single disposable graph.
- `git diff --check` -- expected: no whitespace errors.

**Manual checks:**
- Run load-test and trace-analysis skills over the recorded window; expect a green state gate, intact two-hop traces, and no same-partition overlap.

## Suggested Review Order

**Four-store assertion completeness (the story's core deliverable)**

- Entry point: computes all-store summaries, cardinality, and canonical-account checks.
  [`LoadTestAssertionService.cs:297`](../../CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs#L297)

- Replaced single-store `CompletedCount`/`FailedCount` with four-store status queries.
  [`LoadTestAssertionService.cs:202`](../../CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs#L202)

- New `StageCardinalityCheck`: exact N/N/3N/3N expected-vs-actual per store.
  [`LoadTestAssertionService.cs:371`](../../CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs#L371)

- New `CanonicalAccountSetCheck`: exact-set match, not substring filtering alone.
  [`LoadTestAssertionService.cs:388`](../../CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs#L388)

**k6 fail-closed hardening**

- New blanket threshold makes every named check gate the run's exit code.
  [`script.js:48`](../../k6/script.js#L48)

- `teardown()` now wraps every `JSON.parse` and records a failed check before any early return.
  [`script.js:152`](../../k6/script.js#L152)

**Reset validation gate (blocks k6 on a bad reset)**

- Semantic validation of reset response before the initializer reports success.
  [`ResetResponseValidator.cs:15`](../../CoreBankDemo.LoadTestInitializer/ResetResponseValidator.cs#L15)

- Initializer now calls the validator and lets a thrown exception fail the process.
  [`Program.cs:28`](../../CoreBankDemo.LoadTestInitializer/Program.cs#L28)

**W3C trace context propagation across Dapr (ADR-017)**

- Decision record: propagate persisted `TraceState` alongside `TraceParent`.
  [`ADR-017-dapr-w3c-trace-context.md:12`](../../docs/adr/ADR-017-dapr-w3c-trace-context.md#L12)

- Port signature extended with `traceState`; superseded the frozen Story 3.3 remark.
  [`IEventPublisher.cs:38`](../../CoreBankDemo.ServiceDefaults/IEventPublisher.cs#L38)

- Maps `traceState` to `cloudevent.tracestate` metadata, mirroring the existing traceparent path.
  [`DaprEventPublisher.cs:51`](../../CoreBankDemo.ServiceDefaults/DaprEventPublisher.cs#L51)

- Only production caller: passes the persisted outbox row's `TraceState` through.
  [`DaprOutboxDeliveryStrategy.cs:43`](../../CoreBankDemo.CoreBankAPI/Outbox/DaprOutboxDeliveryStrategy.cs#L43)

**Ordering/exclusivity live-span evidence**

- Store/partition/message tags added for live overlap analysis, processing unchanged.
  [`InboxProcessorBase.cs:451`](../../CoreBankDemo.Messaging/InboxProcessorBase.cs#L451)

- Same tagging pattern applied to the outbox side.
  [`OutboxProcessorBase.cs:451`](../../CoreBankDemo.Messaging/OutboxProcessorBase.cs#L451)

**Sandbox container-to-host reachability (environment-scoped, logged in Spec Change Log)**

- k6 now references host-project endpoints instead of hardcoded `host.docker.internal`, activating Aspire's built-in container tunnel.
  [`AppHost.cs:120`](../../CoreBankDemo.LoadTests/AppHost.cs#L120)

- Three resources opt into a new `0.0.0.0`-bound `loadtest` launch profile; existing profiles (DevProxy's `127.0.0.1` dependency) are untouched.
  [`AppHost.cs:40`](../../CoreBankDemo.LoadTests/AppHost.cs#L40)

**Peripherals: tests and documentation**

- New replicated-topology ordering/exclusivity proof for CoreBank Inbox, mirroring the existing Outbox test.
  [`ReplicatedCoreBankInboxProcessorTests.cs`](../../tests/CoreBankDemo.Persistence.IntegrationTests/CoreBankApi/ReplicatedCoreBankInboxProcessorTests.cs)

- New unit coverage for the four-store assertion logic.
  [`LoadTestAssertionServiceTests.cs`](../../tests/CoreBankDemo.LoadTestSupport.Tests/LoadTestAssertionServiceTests.cs)

- New string-contract coverage for the k6 fail-closed thresholds (known limitation: doesn't execute the script).
  [`K6ScriptContractTests.cs`](../../tests/CoreBankDemo.LoadTestSupport.Tests/K6ScriptContractTests.cs)

- New reset-validator unit tests covering all rejection paths.
  [`tests/CoreBankDemo.LoadTestInitializer.Tests/`](../../tests/CoreBankDemo.LoadTestInitializer.Tests/)

- Full acceptance evidence: configuration, timestamps, REST/MCP JSON, trace analysis, final verdict.
  [`7-3-acceptance-evidence.md`](../../docs/bmad/implementation-artifacts/7-3-acceptance-evidence.md)
