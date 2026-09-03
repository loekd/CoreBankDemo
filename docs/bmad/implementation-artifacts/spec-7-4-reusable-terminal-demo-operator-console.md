---
title: 'Story 7.4: Reusable terminal demo operator console'
type: 'feature'
created: '2026-08-29'
updated: '2026-09-03'
status: 'done'
review_loop_iteration: 0
baseline_commit: '8e55a6488619239b533d086995715fa8740b585f'
context:
  - '{project-root}/docs/bmad/constraints.md'
  - '{project-root}/docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-08-21/prd.md'
  - '{project-root}/docs/bmad/planning-artifacts/architecture/architecture-CoreBankDemo-2026-08-21/ARCHITECTURE-SPINE.md'
  - '{project-root}/docs/bmad/planning-artifacts/epics.md'
  - '{project-root}/docs/bmad/planning-artifacts/briefs/brief-demorunner-console-2026-09-03/brief.md'
  - '{project-root}/docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/DESIGN.md'
  - '{project-root}/docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/EXPERIENCE.md'
  - '{project-root}/docs/adr/ADR-015-presentation-safe-terminal-demo-console.md'
  - '{project-root}/docs/adr/ADR-018-instant-payment-rail.md'
  - '{project-root}/docs/bmad/implementation-artifacts/spec-add-instant-payment-rail.md'
  - '{project-root}/docs/bmad/implementation-artifacts/spec-add-instant-rail-load-coverage.md'
  - '{project-root}/README.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** DemoRunner's deck-bound cue queue is too rigid and is not working well as a live demonstration tool. The operator still needs one reliable place to control both Aspire topologies, manipulate resources, submit standard and instant payments, create repeatable failure/recovery situations, run the accepted load proof, and inspect truthful evidence. Those capabilities must remain useful across talks and unplanned audience questions rather than being coupled to one slide sequence.

**Approach:** Refactor `CoreBankDemo.DemoRunner` into a reusable Terminal.Gui operator console with four capability-driven workspaces: **Operations**, **Resources**, **Evidence/Results**, and **Load Test**. Retain the safe standalone-process and allow-listed HTTP/Aspire boundaries, but retire slide anchors, speaker notes, cue navigation, `Next` gating, scenario-driven execution, checkpoint resume, and last-known-good proof-pack presentation. The console continuously rehydrates live state from supported Aspire CLI and HTTP surfaces, operates either the Regular or LoadTests AppHost, supports confirmed resource control, submits standard and instant payments with explicit idempotency modes, runs bounded bursts, and renders the accepted mixed-rail load assertions without inventing a second test path.

## Boundaries & Constraints

**Always:**

- Run locally with one documented command and no IDE extension.
- Keep DemoRunner outside the banking runtime with no project reference to API, Messaging, persistence, LoadTestSupport, Redis, Dapr, or container-engine client assemblies.
- Use only known local HTTP endpoints and supported Aspire CLI commands: `aspire ps --format Json`, `aspire describe --format Json`, and `aspire resource <resource> start|stop|restart`.
- Treat Regular and LoadTests as known topology profiles. Track whole-AppHost ownership independently from resource-command authority.
- Stop or switch away from a whole AppHost only when this DemoRunner session owns it.
- Permit allow-listed resource start/stop/restart on a fingerprint-verified attached AppHost only after confirmation of each disruptive action.
- Poll real Aspire state; never infer resource success from elapsed time or log text. Show commanded transitions immediately, then resolve them only from a fresh Aspire snapshot.
- Keep one mutating action in flight globally. Read-only inspection remains available; the active burst's Cancel action is the sole mutation-lock exception.
- Make standard and instant rails explicit. Standard remains `202 Pending`; instant truthfully shows committed `200 Completed`/`Failed` or durable fallback `202 Pending`.
- Make payment idempotency explicit: Generated (default), Supplied, or Omitted. Reuse generated/supplied keys for resend; never automatically retry an ambiguous omitted-key request.
- Label every evidence record with topology/profile and run generation. Preserve evidence across an in-session topology switch without obscuring its provenance.
- Run load testing only through the accepted Reset → Run → Wait → Assert → Investigate workflow. Reset is Run's first internal phase and is available only for the disposable LoadTests topology.
- Marshal background updates onto the UI loop, provide mouse/keyboard parity, and preserve usable behavior at 80×24.
- On relaunch, re-read live Aspire/HTTP state and start with empty operation history. Never restore a previous Passed state, cue checkpoint, or proof pack.
- Preserve `.http` files as a manual fallback.

**Ask First:** Adding or changing a banking endpoint; exposing reset outside the existing LoadTests workflow; referencing banking implementation assemblies; adding arbitrary shell execution; changing an AppHost contract solely for the TUI; adding resource operations beyond supported Aspire CLI commands; killing an unowned AppHost; storing credentials; adding a web/desktop UI; or making DemoRunner a prerequisite for development or tests.

**Never:** Put banking validation or state transitions in DemoRunner. Connect directly to Postgres, Redis, Dapr, container-engine sockets, or Aspire private dashboard APIs. Edit checked-in source/configuration during a run. Treat `202 Pending` as failure. Infer final payment outcome when evidence is unresolved. Retry an ambiguous keyless request automatically. Use broad process-name/port kill commands. Require mouse input. Delete containers, volumes, databases, or user files outside the accepted disposable-topology reset workflow.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Launch with no topology | Regular and LoadTests stopped | Show both known profiles and preflight state; permit Start only after prerequisites pass | Failed checks name remediation; no process starts implicitly |
| Start owned AppHost | Operator selects Regular or LoadTests | Start exact known project as a tracked child and show elapsed transition until Aspire confirms resources | Timeout/early exit remains Failed with bounded details; no healthy claim |
| Attach existing AppHost | Healthy matching topology already runs | Fingerprint service graph and endpoints, then explicitly Attach as unowned | Reject unknown, conflicting, stale, or partial graph |
| Switch topology | Current profile is runner-owned | Confirm, stop the owned AppHost, start/attach selected profile, increment run generation | Disable Switch for an attached AppHost or conflicting unowned graph |
| Stop whole AppHost | Current profile is runner-owned | Confirm exact target, stop tracked child gracefully, refresh state | Never stop an attached AppHost |
| Inspect resources | Known topology is active | Poll and display every known resource's actual state, health, endpoints, and legal next action | Stale/unparseable snapshot is `Unknown`; failed console transport is `Unreachable`; both disable mutation |
| Control owned resource | Known allow-listed resource | Confirm disruptive command, run supported Aspire command, show transition elapsed time, resolve from Aspire | Unknown resource/command rejected; timeout never implies success |
| Control attached resource | Fingerprint-verified attached topology | Allow the same confirmed, allow-listed resource command while whole-AppHost Stop/Switch remain disabled | Any fingerprint loss disables commands until a fresh match |
| Standard payment | Scheme absent or `standard` | Submit through PaymentsAPI and show truthful `202 Pending` | No instant-only expectation; transport/validation failure remains distinct |
| Instant payment | `scheme=instant` | Show `200 Completed`/`Failed` if committed in budget, otherwise `202 Pending` | Never label `202` failed or infer a final outcome |
| Generated idempotency | Default mode | Generate/display a stable session key and expose Resend same key | Retry/resend reuses the exact key |
| Supplied idempotency | Operator enters key | Validate and use that exact stable key; expose Resend same key | Invalid input blocks submission with field-level reason |
| Omitted idempotency | Operator deliberately selects Omitted | Send no key and warn before submission that an ambiguous outcome is not retry-safe | Disable Resend after ambiguity; permit outcome query or fresh submission only |
| Payment burst | Bounded count/concurrency | Send deterministic unique identities, show live accepted/completed/failed totals, allow Cancel | Enforce bounds; Cancel preserves captured evidence and labels partial completion truthfully |
| Outcome query | Known id/key | Query supported endpoint and append a read-only evidence record | Remains available while another action is in flight |
| Resource fails mid-demo | CoreBankAPI stopped or unhealthy | Topology and resource rows degrade truthfully; payment evidence remains distinct from health | Recovery action stays in the same row slot; no stale-green status |
| Load run | LoadTests active | Confirm destructive Run; execute Reset → Run → Wait → Assert → Investigate | No standalone reset and no alternate assertion path |
| Load result | Accepted harness completes | Show five individual invariants plus inline-instant-settlement observation | Partial/missing evidence is not Passed; Investigate exposes source detail |
| Topology switch with history | Session already has records | Retain records with old topology/generation labels and start new generation | Never present old evidence as current-topology proof |
| Relaunch | Previous session ended/crashed | Rehydrate only live topology/resource state; operation history is empty | Never auto-load journal, checkpoint, or previous Passed state |
| Destructive confirmation | Stop/Restart/Switch/Load Run | Modal names target/command, focuses Cancel, confirms only with `Y`, cancels with Escape | Trap focus and return it to triggering control; no double Enter or hold gesture |
| Rapid duplicate activation | Mutating action already in flight | Dispatch once and globally disable other mutations | Read-only inspection and active burst Cancel remain available |
| Compact terminal | 80×24 or mouse unavailable | Preserve all capabilities via compact rail, focus order, shortcuts, and panning | Below minimum, show warning without crashing or losing session state |

</frozen-after-approval>

## Architecture Alignment

ADR-015 remains the binding local-tool boundary and is amended with this story:

- DemoRunner is a capability-driven operator console, not a talk-scenario runtime.
- Attached whole-AppHost ownership remains unowned: Stop and Switch are forbidden.
- A fresh fingerprint match grants narrowly scoped resource-command authority on an attached AppHost; each disruptive resource command is allow-listed and confirmed.
- Operation history is session-local and exportable, but never restored automatically.
- The accepted LoadTestSupport/k6 workflow remains the only load assertion authority.

ADR-018 is the authority for instant-payment wire/status behavior. DemoRunner presents those semantics; it does not reinterpret them.

## UX Contract

`DESIGN.md` and `EXPERIENCE.md` in `ux-CoreBankDemo-2026-09-03` are normative for layout, interaction, focus, state vocabulary, contrast, compact-terminal behavior, and stage-safe confirmations.

```text
┌ Regular · Attached ─ DevProxy ON ─ payments ● corebank ✕ postgres ● ┐
│ 1 Operations       │ Stop — corebank-api                 [Restart] │
│ 2 Resources        │ aspire resource corebank-api restart          │
│ 3 Evidence/Results │                                               │
│ 4 Load Test        │ Resources and truthful transition evidence    │
│                    │                                               │
│                    │                           [ Restart resource ] │
├────────────────────┴───────────────────────────────────────────────┤
│ Regular · generation 3 · Restart dispatched · Running — 4s         │
└────────────────────────────────────────────────────────────────────┘
```

The named conference journeys are coverage checks only. Navigation is always by capability, and any workspace is one keypress away.

## Code Map

### Retain and adapt

- `CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj` — standalone .NET 10 executable with centrally pinned Terminal.Gui.
- `Application/Ports/IProcessAdapter.cs` and `Infrastructure/AspireProcessAdapter.cs` — retain owned-child lifecycle and verified attach; extend behind typed topology/resource operations rather than scenario actions.
- `Application/Ports/IHealthMonitor.cs` and `Infrastructure/HealthMonitor.cs` — retain known-endpoint probes; project into live resource/topology state.
- `Application/Ports/IHttpActionExecutor.cs`, `Infrastructure/HttpActionExecutor.cs`, and `Infrastructure/EndpointResolver.cs` — retain allow-listed HTTP transport; replace scenario-shaped requests with typed payment/outcome operations.
- `Application/Ports/ILoadWorkflowRunner.cs` and `Infrastructure/LoadWorkflowRunner.cs` — retain accepted load workflow binding; expose Reset → Run → Wait → Assert → Investigate state and mixed-rail evidence.
- `CoreBankDemo.LoadTestSupport/DatabaseResetCoordinator.cs` — preserve the existing `/reset` surface while allowing each accepted workflow rerun to reset again; publish the processor release generation only on the first reset.
- `Infrastructure/BrowserLauncher.cs` — retain known Aspire/Jaeger links only.
- `Application/Ports/JournalRedaction.cs` — reuse redaction for exported session evidence and bounded details.
- `Terminal/` — replace the cue-oriented presentation model/window with the four-workspace shell defined by the UX spines.

### Retire from the product path

- `Scenarios/mission-critical-talk-v7.json`.
- `Application/Scenarios/TalkScenarioDefinition.cs`, cue/slide fields, and scenario-driven navigation.
- `Application/StateMachine/SessionController.cs`, `SessionState.cs`, and `CueRuntimeState.cs` as a linear cue state machine.
- `RehearsalRunner.cs`, `--show`, `--rehearse`, `--scenario`, and `--resume`.
- `IJournal`, `FileJournal`, `IProofPackStore`, and `FileProofPackStore` as checkpoint/resume or last-known-good UI mechanisms.

Migration may leave compatibility code temporarily while tests move, but no retired surface may remain reachable from the final CLI/TUI or be described as the supported workflow.

## Tasks & Acceptance

**Implementation tasks:**

- [x] Preserve the standalone .NET 10 project boundary, central Terminal.Gui `2.4.17` pin, solution/filter inclusion, and ≥90% line-coverage gate for DemoRunner logic.
- [x] Preserve safe owned-AppHost start/stop, verified attach, known HTTP endpoint resolution, health monitoring, browser links, and accepted load workflow ports as migration inputs.
- [x] Amend ADR-015 and planning artifacts to replace the deck/cue runtime with the reusable operator-console contract and attached-resource authority.
- [x] Tests first: replace cue/session tests with application-state tests covering topology ownership, fingerprint freshness, resource transition polling, global mutation lock, burst cancellation exception, topology generation, relaunch recovery, evidence provenance, and truthful unknown/unreachable states.
- [x] Introduce typed capability/application state for the four workspaces, live topology snapshot, owned/attached status, active mutation, current run generation, operation history, and selected evidence.
- [x] Implement an Aspire state/resource adapter over only `ps --format Json`, `describe --format Json`, and `resource <name> start|stop|restart`; validate profile/resource allow-lists and debounce externally observed state changes.
- [x] Refactor process lifecycle for explicit Regular/LoadTests Start, Attach, Stop, and Switch while preserving graceful owned-child cleanup and forbidding whole-AppHost mutation when attached.
- [x] Implement typed standard/instant payment submission and outcome query over existing allow-listed endpoints, with Generated/Supplied/Omitted idempotency modes and exact ADR-018 status semantics.
- [x] Implement bounded payment bursts with deterministic unique identities, configured count/concurrency limits, live counters, cancellation, and partial-result evidence.
- [x] Implement session-local evidence records with redacted bounded detail, exact request/response metadata, topology/profile and run-generation provenance, raw unwrapped/pannable views, optional view-only wrapping, and explicit export.
- [x] Adapt `ILoadWorkflowRunner` to the Load Test workspace's single Reset → Run → Wait → Assert → Investigate flow; expose all five invariants and inline-instant-settlement evidence independently.
- [x] Replace the three-pane cue UI with the normative four-workspace Terminal.Gui shell, persistent topology bar/navigation rail/evidence strip, modal confirmation behavior, compact 80×24 layout, projector-safe theme, and complete keyboard/mouse parity.
- [x] Remove the scenario/cue/rehearsal/resume product path and update CLI/help/README to one reusable operator-console workflow; retain `.http` fallback documentation.
- [x] Run unit gates, live Regular and LoadTests topology rehearsals, failure injection at resource/payment/load boundaries, and a timed keyboard-only presentation-terminal dress rehearsal before declaring the story done.

**Acceptance Criteria:**

- Given DemoRunner starts, when no topology is active, then it opens the capability-driven shell with Operations, Resources, Evidence/Results, and Load Test available in one keypress and shows truthful preflight/topology state without loading a talk scenario.
- Given a known Regular or LoadTests AppHost, when the operator Starts, Attaches, Stops, or Switches, then the console uses the exact known profile, distinguishes Owned from Attached, confirms destructive actions, and never stops or switches away from an attached whole AppHost.
- Given a fingerprint-verified attached AppHost, when the operator confirms an allow-listed resource Start/Stop/Restart, then DemoRunner invokes the supported Aspire resource command and resolves the transition from fresh Aspire state; loss of fingerprint freshness disables further mutation.
- Given an Aspire snapshot, when state changes externally, then the topology bar and Resources workspace debounce the observation; when this console dispatched the command, then they show `Running` immediately and show elapsed transition time until Aspire confirms resolution.
- Given the Operations workspace, when the operator submits a standard payment, then absent/`standard` scheme shows `202 Pending`; when an instant payment commits in budget, then `200 Completed` or `200 Failed` is shown; when it does not, then truthful `202 Pending` is shown without calling it a failure.
- Given Generated, Supplied, or Omitted idempotency mode, when a request is resent or becomes ambiguous, then generated/supplied keys remain stable and reusable while omitted-key ambiguity disables automatic resend and is labeled `Ambiguous — not yet reconciled`.
- Given a bounded burst, when it runs, then accepted/completed/failed totals update live, the burst's own Cancel remains enabled under the global mutation lock, completed evidence is preserved, and partial completion is never labeled Passed.
- Given a topology switch, when existing evidence remains visible, then every record visibly retains its original profile and generation; no pre-switch record can be mistaken for current-topology proof.
- Given DemoRunner relaunches, when prior session artifacts exist, then only live Aspire/HTTP state is rehydrated and operation history begins empty; no cue checkpoint, prior Passed state, or proof pack is restored.
- Given the LoadTests topology, when Run is confirmed, then Reset executes only as Run's first internal phase, the accepted k6 workflow runs, and five invariant results plus inline-instant-settlement evidence are rendered individually from the existing authority.
- Given any mutation is in flight, when another mutation is attempted, then it is disabled; read-only outcome/evidence inspection and only the active burst's Cancel remain available.
- Given a destructive action, when its modal opens, then Cancel has focus, `Y` alone confirms, Escape cancels, focus is trapped and returned to the trigger, and the inverse recovery action occupies the same screen location.
- Given an 80×24 terminal or no mouse, when the operator executes both named demo journeys, then all operations remain reachable by documented keys, state never relies on color alone, and no evidence/state is lost on resize.
- Given the project graph and command adapters are inspected, then DemoRunner has no banking implementation dependency, direct store/container integration, unrestricted URL, arbitrary command, scenario-supplied shell, or private Aspire dashboard API.
- Given DemoRunner is unavailable, when the existing `.http` files are used, then manual demo behavior remains supported and unchanged.

## Verification

- `dotnet test tests/CoreBankDemo.DemoRunner.Tests` — all DemoRunner tests pass with the repository's ≥90% line-coverage gate.
- `dotnet build CoreBankDemo.Rebuild.slnf` — the rebuild gate remains clean.
- `dotnet list CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj reference` — no banking implementation project reference.
- Launch the Regular AppHost through DemoRunner; attach to a separately launched Regular AppHost; exercise resource stop/restart in both ownership modes; verify whole-AppHost Stop/Switch remains disabled when attached.
- Submit standard and instant payments in all three idempotency modes; exercise stable resend, keyless ambiguity, outcome query, and a cancellable bounded burst.
- Launch LoadTests and complete Reset → Run → Wait → Assert → Investigate; verify all five invariants and inline-instant-settlement evidence are sourced from the accepted harness.
- Restart DemoRunner after operations and during an intentional outage; verify live state rehydrates and history is empty.
- Repeat at 80×24, keyboard-only, with rapid activation, Ctrl+C, resource timeout, malformed Aspire output, and topology loss.
- `git diff --check`.

## Spec Change Log

- 2026-08-29: Initial deck-bound presentation-safe cue-runner story created and implemented behind standalone ports.
- 2026-08-31: Added durable Inbox inspection and hardened topology switching while retaining the cue model.
- 2026-09-03: Human-directed product correction. Replaced the slide/cue/scenario experience with a reusable capability-driven operator console; added standard/instant payment operations, explicit idempotency modes, bursts, real Aspire resource control, evidence provenance, mixed-rail load proof, live-state-only recovery, and the attached-resource authority amendment. Existing cue-era completion claims were converted into explicit retain/retire/migration tasks.
- 2026-09-03: Implemented the reusable operator console, retired the cue/rehearsal/resume path, added typed Aspire/payment/load/evidence capabilities and matrix coverage, and completed automated plus live Regular/LoadTests verification.
- 2026-09-03: Review hardening made repeated LoadTests reset real instead of cached, honored authoritative `allPassed`, removed false ordering substitution, required fresh k6 execution identity and topology authority, hardened PID ownership/command cancellation, made dashboard URLs live-state-derived, and added regression coverage. The pre-existing missing per-key-ordering assertion was deferred and is displayed as unproven rather than green.

## Suggested Review Order

**Application state and authority**

- Entry point for ownership, mutation locking, generations, and evidence provenance.
  [`OperatorConsoleController.cs:12`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L12)

- Fresh Aspire authority gates every allow-listed resource mutation.
  [`OperatorConsoleController.cs:352`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L352)

- Payment semantics centralize stable keys, ambiguity, resend, and truthful rail outcomes.
  [`OperatorConsoleController.cs:400`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L400)

- Bursts enforce bounds, deterministic identities, cancellation, and partial evidence.
  [`OperatorConsoleController.cs:474`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L474)

**Aspire and load-test boundaries**

- Aspire JSON becomes typed, fingerprinted resource state with replica and command checks.
  [`AspireJsonParser.cs:7`](../../../CoreBankDemo.DemoRunner/Application/AspireJsonParser.cs#L7)

- Supported Aspire commands target only verified concrete resource instances.
  [`AspireCliAdapter.cs:92`](../../../CoreBankDemo.DemoRunner/Infrastructure/AspireCliAdapter.cs#L92)

- Load workflow preserves Reset → Run → Wait → Assert → Investigate authority.
  [`LoadWorkflowRunner.cs:25`](../../../CoreBankDemo.DemoRunner/Infrastructure/LoadWorkflowRunner.cs#L25)

- Repeated accepted runs now perform a real reset while releasing processors once.
  [`DatabaseResetCoordinator.cs:60`](../../../CoreBankDemo.LoadTestSupport/DatabaseResetCoordinator.cs#L60)

**Payment transport and presentation**

- Known HTTP transport maps standard, instant, malformed, and ambiguous outcomes.
  [`HttpPaymentGateway.cs:12`](../../../CoreBankDemo.DemoRunner/Infrastructure/HttpPaymentGateway.cs#L12)

- Presentation state keeps text/symbol truth independent of Terminal.Gui rendering.
  [`PresentationModel.cs:42`](../../../CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs#L42)

- Four workspaces, persistent chrome, confirmations, and compact layout live here.
  [`MainWindow.cs:12`](../../../CoreBankDemo.DemoRunner/Terminal/MainWindow.cs#L12)

- Confirmation requires uppercase Y and defaults focus to Cancel.
  [`MainWindow.cs:525`](../../../CoreBankDemo.DemoRunner/Terminal/MainWindow.cs#L525)

**Verification**

- State-machine regression tests cover ownership, locks, payments, bursts, and provenance.
  [`OperatorConsoleControllerTests.cs:9`](../../../tests/CoreBankDemo.DemoRunner.Tests/Application/OperatorConsoleControllerTests.cs#L9)

- Mutation-lock and read-only exception behavior is explicit.
  [`OperatorConsoleControllerTests.cs:396`](../../../tests/CoreBankDemo.DemoRunner.Tests/Application/OperatorConsoleControllerTests.cs#L396)

- Burst cancellation preserves partial evidence without unlocking other mutations.
  [`OperatorConsoleControllerTests.cs:458`](../../../tests/CoreBankDemo.DemoRunner.Tests/Application/OperatorConsoleControllerTests.cs#L458)
