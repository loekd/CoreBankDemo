---
title: 'Story 7.4: Presentation-safe terminal demo console'
type: 'feature'
created: '2026-08-29'
status: 'in-progress'
review_loop_iteration: 0
baseline_commit: '8e55a6488619239b533d086995715fa8740b585f'
context:
  - '{project-root}/docs/bmad/constraints.md'
  - '{project-root}/docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-08-21/prd.md'
  - '{project-root}/docs/bmad/planning-artifacts/architecture/architecture-CoreBankDemo-2026-08-21/ARCHITECTURE-SPINE.md'
  - '{project-root}/docs/bmad/planning-artifacts/epics.md'
  - '{project-root}/README.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Running the conference demo currently asks the speaker to coordinate several fragile surfaces: an Aspire process, health checks, `.http` requests, fault configuration, logs, and browser dashboards. A missed prerequisite, double-clicked action, stale process, or hidden assertion failure can derail the narrative even when the banking system is correct. The requested UI is not a banking application; it is a local operator tool for rehearsing and presenting demos reliably.

**Approach:** Add a standalone .NET console project, `CoreBankDemo.DemoRunner`, with a responsive Terminal.Gui interface, a reusable talk-scenario model, and a checked-in `MissionCriticalTalk-v7` scenario derived from the author's 55-slide deck. The first scenario pre-arms the actual live moments: “Inbox at work” at slide 42, “Proving everything works” and the Aspire/k6/DevProxy flow at slides 45–52, and the development-environment hand-off at slide 53. The runner launches or explicitly attaches to the required known Aspire profile, performs preflight and readiness checks, executes only allow-listed HTTP/process/browser actions, and gates every transition on visible evidence. Show mode remains speaker-paced; Rehearsal mode runs actionable cues without narration pauses and captures a clearly timestamped proof pack. The TUI is mouse-friendly in the style of the deck's cockpit/control-room visual, with complete keyboard parity.

## Boundaries & Constraints

**Always:** Run locally with one documented command and no IDE extension. Keep the runner outside the banking runtime and reference no API, Messaging, or persistence implementation project. Interact through stable local HTTP/LoadTestSupport endpoints and a narrowly defined child-process adapter; the supported load-test reset is allowed only in the explicitly selected rehearsal/load-test profile and is never reimplemented through direct store access. Pin Terminal.Gui `2.4.17` centrally. Validate the entire talk scenario before starting a process. Use a closed set of strongly typed action kinds and known topology profiles; reject unknown actions and fields. Give every mutating request a run-scoped deterministic idempotency key so Retry is safe. Treat assertions, readiness probes, and timeouts as first-class results. Keep a single action in flight, debounce mouse activation, and make Next unavailable until the current cue passes. Pre-arm the next live cue without automatically firing it. Provide keyboard equivalents for every mouse action. Marshal all background updates onto the UI loop. Stop only child processes started by the current runner session, with graceful cancellation before forced termination. Keep journals, bounded redacted output, and rehearsal proof packs in a gitignored local artifacts directory. Preserve the `.http` files as a no-runner fallback.

**Ask First:** Adding or changing a banking endpoint; exposing a destructive reset from a production API; referencing banking implementation assemblies; executing arbitrary scenario-provided commands; changing the regular AppHost contract solely for the TUI; automatically killing an unowned process; storing demo credentials; adding a web/desktop UI; or making the runner a prerequisite for normal development, tests, or the banking services.

**Never:** Put banking validation or state-transition logic in the runner. Connect directly to Postgres, Redis, Dapr, or container-engine sockets. Edit checked-in JSON, source, or environment files during a run. Infer success from elapsed time or log text when a health/assertion endpoint exists. Auto-skip a failed cue, label a partial result Passed, or continue after ownership/readiness is uncertain. Use broad process-name or port-based kill commands. Require mouse input. Delete containers, volumes, databases, or user files as recovery behavior.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|---------------|----------------------------|----------------|
| First launch | Valid scenario; AppHost is stopped | Doctor checks SDK, Aspire CLI, container runtime, scenario, ports, and endpoints; Start becomes available only when preflight passes | Failed check stays visible with a concrete remediation; no process starts |
| Runner-owned start | Preflight passes and speaker clicks Start | Start the exact known Aspire profile required by the selected talk cue as a tracked child, stream bounded output, and wait for required health probes before arming it | Timeout or early exit leaves the session at Setup with Retry and Details; no cue runs |
| Existing topology | Expected ports are occupied by a healthy matching graph | Offer explicit Attach; verify the expected service fingerprint and mark the process unowned | Never stop or restart attached processes; reject an unknown or partially healthy graph |
| Cue succeeds | Speaker clicks Run or presses Enter | Disable duplicate activation, execute once with a deterministic run/cue key, show evidence, mark Passed, and enable Next | Journal the passed checkpoint atomically |
| Double activation | Rapid double-click or repeated Enter while a cue runs | Exactly one action is dispatched; controls show Running until it completes | Ignore/debounce the duplicate without creating a second request |
| Cue fails | HTTP error, timeout, unhealthy dependency, or failed assertion | Keep the cue selected and Next disabled; show plain-language summary plus Retry, Details, and allowed recovery actions | Never auto-advance or display success colors; preserve diagnostic evidence |
| Retry after uncertain response | Request may have reached the server before timeout | Reuse the same run/cue idempotency key and reconcile through a read/assert action | If outcome remains ambiguous, block progression and offer topology recovery |
| Topology becomes unhealthy | A required health probe changes after startup | Freeze mutating controls, mark affected resources, and guide the speaker to retry health or restart from the last checkpoint | Do not continue on stale health; do not kill unowned processes |
| Show mode before a live cue | Speaker is still presenting earlier slides | Pre-arm dependencies and display Ready without sending the business request; retain speaker-controlled timing | A failed pre-arm is visible early and offers recovery without firing the cue |
| Inbox cue (slide 42) | Speaker activates “Inbox at work” | Send the same deterministic command twice, show two accepted responses, and prove one Inbox identity/one execution through supported inspection endpoints | Any missing/duplicate evidence blocks completion and preserves request/trace details |
| Load proof (slides 45–52) | Speaker starts the resilience proof | Reuse the accepted load workflow and render Run → Wait → Assert; enable Investigate on failure with links/details from the same evidence sources | Never invent a second assertion path; all five invariant results remain individually visible |
| Live cue cannot recover | Safe Retry/restart still fails within the talk window | Mark the live cue Failed and offer the latest successful rehearsal proof pack with timestamp, source commit, scenario version, and explicit REHEARSAL label | Never recolor or journal the live cue as Passed; speaker chooses whether to show fallback evidence |
| Development-environment hand-off (slide 53) | Speaker reaches Dev Containers/Codespace | Show the prepared talk card and only known, validated open-link actions for repo/dev-environment material | Broken links fail locally and do not affect banking or load-test state |
| Small/no-mouse terminal | Mouse events unavailable or terminal is resized | Full operation remains possible with Tab/arrows/Enter and shortcut keys; panes collapse into a single focused view | Show a non-blocking size hint; never crash or lose session state |
| Runner interruption | Ctrl+C, terminal close, or runner crash | Flush the journal, attempt graceful shutdown of owned children, and make the last proven checkpoint discoverable at next launch | Attached processes remain untouched; incomplete cue is not marked Passed |
| Rehearsal | `--rehearse` or Rehearse button | Run all actionable cues with narration pauses skipped and produce a proof pack containing cue results, five load invariants, scenario version, source commit, and timestamps | Non-zero exit on any failed preflight, cue, cleanup, or assertion; failed run never replaces the latest known-good proof pack |

</frozen-after-approval>

## Architecture Decision Required

ADR-015 must be accepted before runner implementation begins. The user has explicitly requested a console GUI for presentation tooling, which narrows the PRD's broad “UI” non-goal but does not authorize a banking-product UI or a banking contract change. The ADR must record:

- the standalone local-tool boundary and absence of banking implementation project references;
- Terminal.Gui as the mouse-and-keyboard TUI adapter, centrally pinned to `2.4.17`;
- the allow-listed scenario-action model instead of arbitrary shell execution;
- child-process ownership, attach behavior, cleanup, journaling, and redaction rules;
- why a truthful fail-closed cue gate improves demo reliability without claiming the underlying system can never fail;
- how talk/slide anchors remain presentation metadata rather than a runtime dependency on the PDF;
- why a failed live cue may offer clearly labelled, timestamped rehearsal evidence but must never present that evidence as a live success;
- why the existing `.http` requests remain the supported fallback and behavioral oracle.

Until ADR-015 is accepted, the first execution task is architecture alignment; later implementation tasks must not begin.

## UX Blueprint

```text
┌ MissionCriticalTalk v7 ─ SHOW ─ Cue 1/3 ───────────── System ready 7/7 ┐
│ TALK CUES               │ CURRENT CUE                  │ CONFIDENCE     │
│ ✓ Pre-show doctor       │ Slide 42 · Inbox at work     │ ● Payments API│
│ ▶ S42 · Inbox at work   │                              │ ● CoreBank API│
│ ○ S45–52 · Prove it     │ 1. Submit TALK-V7-INBOX-A    │ ● Postgres    │
│ ○ S53 · Dev environment │ 2. Retry the same command    │ ● Redis       │
│                         │                              │ ● Dapr pub/sub│
│ Next live cue: slide 45 │ Evidence                     │ ● Jaeger      │
│                         │ Accepted: 2 · Inbox: 1       │ ◐ Load harness│
│ [Rehearse all]          │ Executions: 1 · PRE-ARMED    │               │
│                         │ [ Run cue ] [ Retry ] [ Next ]│ [Aspire][Trace]│
│                         │                    (disabled) │ [Proof pack]  │
├─────────────────────────┴──────────────────────────────┴────────────────┤
│ Run → Wait → Assert → Investigate · Enter Run · Ctrl+R Retry · Q Quit  │
└─────────────────────────────────────────────────────────────────────────┘
```

Interaction rules:

- Left pane lists only live talk cues, anchored to slide number/title; it does not try to replace the slide deck. Passed, Current, Available, and Locked are visually distinct and never color-only.
- Center pane contains one calm cue at a time: speaker note, action, live evidence, and the smallest safe choice set.
- Right pane is continuously refreshed system confidence; clicking a resource opens details, not an unsafe mutation. The Load proof switches the center into the deck's Run → Wait → Assert → Investigate phase strip.
- The primary action stays in one stable location. Next is visible but disabled until evidence passes, preventing accidental narrative drift.
- Red is reserved for a proven failure. In-progress and awaiting-speaker states use neutral/amber treatments so the screen remains stage-friendly.
- A last-known-good rehearsal is reachable as a clearly labelled proof pack, never as a substitute Passed state for a failed live run.
- The palette uses the deck's restrained teal/navy language where terminal color permits, while text and symbols carry every semantic state.
- A compact layout replaces the three panes below the preferred width; it uses tabs rather than truncating critical evidence.

## Code Map

- `CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj` -- standalone .NET 10 executable; no project reference to the banking services, Messaging, or their stores.
- `CoreBankDemo.DemoRunner/Program.cs` -- composition root and `--doctor`, `--show`, `--rehearse`, `--scenario`, and `--resume` argument binding only.
- `CoreBankDemo.DemoRunner/Scenarios/mission-critical-talk-v7.json` -- checked-in cues anchored to slides 42, 45–52, and 53 using a versioned schema and allow-listed action kinds; contains speaker notes and identifiers, never commands or secrets.
- `CoreBankDemo.DemoRunner/Application/` -- scenario validation, pre-arm/cue state machine, Run/Wait/Assert/Investigate load workflow, checkpoint policy, and session controller behind ports for process, HTTP, browser launch, proof pack, journal, and time.
- `CoreBankDemo.DemoRunner/Infrastructure/` -- owned Aspire child-process adapter, HTTP/LoadTestSupport action executor, health monitor, browser opener, proof-pack writer, and bounded/redacted journal implementation.
- `CoreBankDemo.DemoRunner/Terminal/` -- Terminal.Gui views, responsive layouts, mouse/keyboard bindings, theme, and UI-thread dispatch; contains no scenario or process logic.
- `tests/CoreBankDemo.DemoRunner.Tests/` -- fast state-machine, slide/cue validation, pre-arm safety, idempotency, ownership, recovery, proof provenance, redaction, and presentation-model tests using fakes/Moq; Terminal.Gui rendering is kept behind a thin adapter.
- `Directory.Packages.props`, `CoreBankDemo.sln`, and `CoreBankDemo.Rebuild.slnf` -- central package pin plus project/test inclusion in the ordinary gate.
- `.gitignore` -- exclude local journals, captured output, and rehearsal proof packs without hiding checked-in scenarios.
- `demo-requests.http` and `payment-idempotency-tests.http` -- remain unchanged as fallback flows and expected-behavior references.
- `README.md` -- document the one-command presenter path, shortcuts, rehearsal, recovery, and the manual fallback after the runner is proven.

## Tasks & Acceptance

**Execution:**

- [x] Architecture first: write and accept ADR-015, then align the architecture spine/epic context with the local presentation-tool exception; do not start code first.
- [x] Tests first: prove scenario/slide-anchor validation, legal cue and load-phase transitions, pre-arm-without-fire behavior, fail-closed Next gating, single in-flight dispatch, deterministic retry identity, timeout/cancellation behavior, child ownership, attach safety, checkpoint resume, proof-pack provenance, redaction, and bounded retention.
- [x] Add the runner and test projects to the solution/filter; centrally pin Terminal.Gui `2.4.17`; keep the executable independent of banking implementation projects; gitignore only its generated local run artifacts.
- [x] Implement the versioned talk-scenario model and application state machine with explicit ports for process, HTTP/LoadTestSupport, health, browser, proof pack, journal, and time.
- [x] Reuse Story 7.3's accepted load-test sequence and evidence sources for Run → Wait → Assert → Investigate; do not create parallel reset, drain, invariant, or trace semantics for the TUI.
- [ ] Implement the owned Aspire-profile process adapter, verified attach mode, health monitor, allow-listed HTTP actions, browser launch, and graceful cleanup. No generic command action is permitted.
- [ ] Build the responsive three-pane Terminal.Gui shell, compact layout, accessible status text, mouse bindings, keyboard shortcuts, and UI-thread-safe progress updates.
- [ ] Encode `MissionCriticalTalk-v7`: Inbox at work (slide 42), Proving everything works/Aspire load test/AI analysis (slides 45–52), and the Dev Containers/Codespace hand-off (slide 53), including speaker notes, deterministic identity, evidence checks, and known Aspire/Jaeger links; preserve the `.http` fallback.
- [ ] Add `--doctor`, `--show`, `--rehearse`, and `--resume`; rehearse repeatedly from a healthy local environment, produce a timestamped proof pack, and inject failures at every cue/phase boundary to prove truthful recovery.
- [ ] Document the presenter workflow and run a timed dress rehearsal on the actual presentation terminal before declaring the story done.

**Acceptance Criteria:**

- Given a machine with the documented local prerequisites, when `dotnet run --project CoreBankDemo.DemoRunner -- --show --scenario mission-critical-talk-v7` starts, then the validated control room opens without manual config edits and either starts the required known Aspire profile or offers a verified explicit Attach.
- Given mouse input, when the speaker selects talk cues, resource details, and action buttons, then every supported operation works; given no mouse, the same workflow is complete using only documented keys.
- Given Show mode before slide 42, when the Inbox cue is pre-armed, then no payment/command has been sent; when the speaker fires it, then two accepted deliveries with one deterministic identity are reconciled to one Inbox identity and one execution before Next becomes available.
- Given the slide 45–52 load cue, when it runs, then the TUI exposes the existing Run → Wait → Assert workflow, displays all five invariant results, and enables Investigate with trace/log evidence on failure; it does not implement different acceptance rules.
- Given any cue is Running, Failed, Cancelled, or Ambiguous, when the speaker attempts to advance, then Next remains unavailable and no subsequent mutating action is dispatched.
- Given a cue is retried after a timeout, when the prior request may have completed, then the same deterministic idempotency key is reused and a read/assert action reconciles the result without duplicating business work.
- Given the runner owns the AppHost, when the session exits normally or is cancelled, then only that tracked child tree is stopped gracefully; given Attach mode, no external process is stopped or restarted.
- Given a failed probe or action, when the UI reports it, then the summary is stage-readable, Details preserve bounded redacted evidence, the last Passed checkpoint is unchanged, and safe Retry/recovery remains available.
- Given a live cue cannot be recovered safely, when the speaker chooses fallback evidence, then only a prior fully successful rehearsal proof pack is available and it is visibly labelled with REHEARSAL, timestamp, scenario version, and source commit; the live cue remains Failed.
- Given `--rehearse`, when all non-narration actions execute, then the command exits zero only after every prerequisite, cue assertion, all five load invariants, and cleanup pass; any failure produces non-zero exit, names the first unproven cue/phase, and does not replace the last known-good proof pack.
- Given the project graph is inspected, then DemoRunner contains no reference to API, Messaging, DbContext, Redis, Dapr, or container-engine client assemblies and no scenario can express an arbitrary executable or shell command.
- Given the original `.http` files, when the runner is unavailable, then the smoke-tested demo remains runnable manually with unchanged banking behavior.

## Design Notes

“Cannot fail” is implemented as presentation safety, not as a false availability guarantee. Dependencies can still fail; the tool's contract is that it discovers common problems before the talk, prevents unsafe progression, makes failures obvious, preserves the last proven point, and offers a rehearsed recovery path.

The author-supplied MissionCriticalTalk v7 deck is the design input for the first scenario, not a runtime asset. Its live narrative is materially different from the brownfield README's Stage 0–4 outline: the explicit Inbox demo is at slide 42, the cockpit-themed proof/load sequence spans slides 45–52, and the Dev Containers/Codespace hand-off is at slide 53. The scenario stores those anchors and short speaker cues, but the PDF itself is neither parsed nor required during a show. Later talks add another validated scenario instead of forking the runner.

Terminal.Gui is selected because its stable v2 line provides a cross-platform .NET TUI with first-class mouse and keyboard input. The package boundary must stay thin: the scenario/session controller owns behavior, and the Terminal.Gui layer only renders immutable presentation state and emits intents. This keeps the critical reliability logic testable without a real terminal.

Scenario files are data, not scripts. They may choose from compiled action kinds such as `selectTopology`, `waitForHealth`, `sendHttp`, `runAcceptedLoadWorkflow`, `assertHttp`, `openKnownUrl`, and `speakerPause`; they may never supply a process path, shell text, database statement, or arbitrary URL outside the validated local allow-list. Starting a known Aspire profile is application configuration, not a scenario command.

The load cue is a presentation adapter over Story 7.3, not another test harness. Its four visible phases deliberately match slide 52: Run launches the accepted k6 workflow, Wait follows health and eventual-consistency drain, Assert renders the five LoadTestSupport invariants, and Investigate exposes the same trace/log sources only when more diagnosis is useful.

The recovery journal records facts (`session`, `scenarioVersion`, `sourceCommit`, `slideAnchor`, `cue`, `phase`, `state`, `timestamp`, evidence summary), not secrets or unbounded raw logs. An interrupted Running cue is recovered as Ambiguous and must be reconciled; it is never upgraded to Passed from the journal alone. A proof pack is promoted to “last known good” only after the complete rehearsal and cleanup pass.

Package/design references for implementation review:

- Terminal.Gui project and getting started: <https://github.com/tui-cs/Terminal.Gui>
- Terminal.Gui mouse bindings: <https://github.com/tui-cs/Terminal.Gui/blob/develop/docfx/docs/mouse.md>

## Verification

**Commands and checks:**

- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: all gates green, including ≥90% line coverage for DemoRunner application logic.
- `dotnet run --project CoreBankDemo.DemoRunner -- --doctor --scenario mission-critical-talk-v7` -- expected: deterministic prerequisite/talk report; no AppHost process or business request is started.
- `dotnet run --project CoreBankDemo.DemoRunner -- --rehearse --scenario mission-critical-talk-v7` -- expected: slide 42's Inbox proof, slides 45–52's five-invariant load proof, and slide 53's hand-off checks pass; a provenance-labelled proof pack is produced and the command exits zero.
- Inject failure separately in Run, Wait, Assert, and Investigate setup -- expected: the named phase fails visibly, Next stays disabled, the command exits non-zero, and no failed run replaces the last known-good rehearsal.
- Repeat the interactive run with mouse, keyboard-only input, terminal resize, rapid double activation, Ctrl+C, AppHost early exit, health loss, and runner restart -- expected: the I/O matrix holds and no unowned process is terminated.
- Inspect the project graph and scenario schema -- expected: no banking implementation project/store dependency and no generic executable, shell, database, or unrestricted URL action.
- Run `demo-requests.http` without DemoRunner -- expected: the fallback remains valid and behavior is unchanged.
- `git diff --check` -- expected: no whitespace errors.

## Spec Change Log

- 2026-08-29: Initial story created from the user's request for a mouse-enabled, presentation-safe .NET console demo tool.
- 2026-08-29: Renumbered to Story 7.4 and aligned the first scenario to MissionCriticalTalk v7's actual live cues and Run → Wait → Assert → Investigate narrative.
- 2026-08-29: ADR-015 accepted; `CoreBankDemo.DemoRunner` implemented end-to-end (scenario model/validator, state machine, ports, Infrastructure adapters, Terminal.Gui shell, `--doctor`/`--show`/`--rehearse`/`--resume`) with 118 tests at 100% line / 91.76% branch coverage on the covered Application/presentation-model surface. `--doctor` verified live. `--rehearse` verified live against a real spawned `CoreBankDemo.AppHost` process: it failed closed when Payments/CoreBank/Jaeger did not become healthy in time (expected — several Epic 4–6 stories the AppHost depends on are still backlog/in-progress on this branch), produced no proof pack, and left no orphaned process or container behind. Tasks 127–128 (a fully healthy rehearsal producing a saved proof pack, and a timed dress rehearsal on the real presentation terminal) remain open until the upstream rebuild stories are done and a real terminal session is available.
- 2026-08-29: Hardened the implementation review findings: owned AppHost output is now drained into a bounded redacted buffer, Unix shutdown sends SIGINT before forced termination, every normal TUI exit cleans up owned children, run identities are unique while `--resume` resolves the latest matching journal, cancelled cues become explicitly Cancelled, preflight gates Show/Rehearsal startup, and rehearsal proof packs are promoted only after cleanup succeeds. Story remains in progress: explicit attach UX, full compact/resource-detail TUI behavior, slide-42 durable Inbox/execution evidence, a healthy proof-pack rehearsal, and the real-terminal dress rehearsal are still unproven.
