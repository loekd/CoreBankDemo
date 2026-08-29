# ADR-015: Presentation-safe terminal demo console

**Date:** 2026-08-29
**Status:** Accepted
**Deciders:** Architecture team (human scope amendment 2026-08-29, Story 7.4)
**Supersedes:** None

## Context

Running the conference demo asks the speaker to coordinate several fragile surfaces by hand: an Aspire process, health checks, `.http` requests, DevProxy fault configuration, logs, and browser dashboards. A missed prerequisite, a double-clicked action, a stale process, or a hidden assertion failure can derail the narrative even when the banking system itself is correct.

The PRD's broad "no UI" non-goal targets a banking-product UI. The user has explicitly requested a mouse-enabled terminal console for presentation tooling — a narrower, human-approved amendment that does not authorize a banking UI, a new banking endpoint, or a change to the external contract frozen by AD-1/ADR-008. This ADR records the boundary and rules a standalone console must follow so the exception cannot creep into the banking runtime.

## Decision

Add a standalone .NET 10 console project, `CoreBankDemo.DemoRunner`, that operates entirely outside the banking runtime as a local operator tool. It never becomes a prerequisite for development, tests, or the banking services themselves.

### Standalone local-tool boundary

`CoreBankDemo.DemoRunner` references only `System.*`/BCL assemblies and its own UI package. It contains **no project reference** to `CoreBankDemo.CoreBankAPI`, `CoreBankDemo.PaymentsAPI`, `CoreBankDemo.Messaging`, `CoreBankDemo.LoadTestSupport`, or any EF Core `DbContext`. It never opens a Postgres, Redis, Dapr, or container-engine socket. All interaction with the banking system happens through stable local HTTP endpoints (PaymentsAPI, CoreBankAPI, LoadTestSupport's REST surface — reusing the same endpoints as `demo-requests.http`/`payment-idempotency-tests.http`, never new ones) and a narrowly scoped child-process adapter that starts or attaches to a known Aspire AppHost profile. This keeps the project-graph invariant machine-checkable: `dotnet list CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj reference` must show zero banking implementation projects, forever.

### Terminal.Gui as the pinned TUI adapter

Terminal.Gui's stable v2 line is the only UI package, pinned centrally at **2.4.17** in `Directory.Packages.props` (one version for the whole repo, consistent with existing central package management). The package boundary stays thin: `Terminal/` renders immutable presentation-state view models and emits user intents onto the application's command channel; it contains no scenario, process, or HTTP logic. This keeps the state machine, pre-arm logic, and evidence gating unit-testable without a real terminal — Terminal.Gui rendering is exercised only behind a thin, fake-able adapter interface.

### Allow-listed scenario-action model, not arbitrary shell execution

Scenario files (e.g. `mission-critical-talk-v7.json`) are data, never scripts. A cue's `actions` array may only contain a closed set of strongly typed action kinds: `selectTopology`, `waitForHealth`, `sendHttp`, `runAcceptedLoadWorkflow`, `assertHttp`, `openKnownUrl`, `speakerPause`. The scenario loader deserializes into a closed discriminated union and **rejects unknown action kinds or unknown fields** at validation time, before any process starts. No action kind may carry a process path, shell text, database statement, or arbitrary URL — `openKnownUrl` resolves only against a compiled allow-list of known dashboard/link targets (Aspire dashboard, Jaeger, repo/dev-environment links), and `sendHttp`/`assertHttp` resolve only against a compiled allow-list of known local endpoints (PaymentsAPI, CoreBankAPI, LoadTestSupport). Starting a known Aspire profile (`selectTopology`) is application configuration selecting between two known `AppHost` project paths, not a scenario-supplied command.

### Process ownership, attach, cleanup, journaling, redaction

- **Ownership:** the process adapter starts the exact known AppHost project (`CoreBankDemo.AppHost` or `CoreBankDemo.LoadTests`) as a tracked child process tree and records that PID tree as *owned* for the session.
- **Attach:** if the expected ports are already occupied by a healthy, fingerprint-matching topology (same known service set responding on the documented ports/health routes), the runner offers an explicit Attach action instead of starting a second instance. Attached processes are marked *unowned* and are never stopped, restarted, or treated as a healthy match without fingerprint verification; an unknown or partially healthy graph is rejected outright.
- **Cleanup:** on normal exit, cancellation, or Ctrl+C, only owned child trees receive graceful cancellation (SIGINT/close-then-wait) before forced termination; unowned/attached processes are never touched. No broad process-name or port-based kill command is ever issued.
- **Journal:** a local, append-only, gitignored journal records facts only — `session`, `scenarioVersion`, `sourceCommit`, `slideAnchor`, `cue`, `phase`, `state`, `timestamp`, and a bounded evidence summary. It never records secrets, credentials, or unbounded raw response/log bodies; captured output is truncated and redacted (e.g. `Idempotency-Key`/`Authorization` header values are never persisted verbatim). An interrupted `Running` cue recovers as **Ambiguous**, never as `Passed`, on next launch.

### Fail-closed cue gate improves reliability without claiming infallibility

"Cannot fail" is a presentation-safety contract, not a false availability guarantee: the underlying banking system can still fail. The console's job is to catch common problems before the talk (`--doctor`), refuse to advance the narrative on unproven evidence (`Next` stays disabled until the current cue's assertion passes), make failures visible in plain language with bounded diagnostic detail, and preserve the last proven checkpoint. Assertions, health probes, and timeouts are first-class typed results — never inferred from elapsed wall-clock time or by pattern-matching log text — so a truthful failure is always distinguishable from a truthful pass.

### Slide/talk anchors are presentation metadata, not a runtime dependency

Each cue stores a `slideAnchor` (e.g. `42`, `45-52`, `53`) and a short speaker note as descriptive metadata sourced from the author's deck. The scenario schema does not parse, embed, or require the PDF at runtime; the anchor exists purely to align the operator's screen with what the speaker is saying. Future talks add a new validated scenario file rather than forking the runner or coupling it to a specific deck format.

### Rehearsal fallback is labelled evidence, never a substitute live success

If a live cue cannot be recovered safely within the talk window (Retry and topology recovery both fail), the speaker may choose to display the most recent **fully successful rehearsal proof pack** as reference evidence. That evidence is always rendered with a visible `REHEARSAL` label, its timestamp, source commit, and scenario version, and it never recolors, journals, or reports the live cue as `Passed`. Promotion of a proof pack to "last known good" happens only after a complete rehearsal run (all cues, all five load invariants, cleanup) passes end-to-end.

### `.http` files remain the supported fallback and behavioral oracle

`demo-requests.http` and `payment-idempotency-tests.http` are unchanged by this story and remain fully runnable without the runner — they are both the manual fallback path and the reference for what "correct" HTTP behavior looks like when the runner's `sendHttp`/`assertHttp` actions are implemented and tested.

## Implementation

- `CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj` — standalone net10.0 executable; `Terminal.Gui` package reference only, version resolved centrally.
- `CoreBankDemo.DemoRunner/Program.cs` — composition root; `--doctor`/`--show`/`--rehearse`/`--scenario`/`--resume` argument binding only.
- `CoreBankDemo.DemoRunner/Scenarios/mission-critical-talk-v7.json` — checked-in, versioned, schema-validated scenario data.
- `CoreBankDemo.DemoRunner/Application/` — scenario validation, the cue/phase state machine, the Run→Wait→Assert→Investigate load workflow, checkpoint policy, and the session controller behind ports for process, HTTP, health, browser, proof pack, journal, and time.
- `CoreBankDemo.DemoRunner/Infrastructure/` — the owned Aspire child-process adapter, HTTP/LoadTestSupport action executor, health monitor, browser opener, proof-pack writer, bounded/redacted journal.
- `CoreBankDemo.DemoRunner/Terminal/` — Terminal.Gui views/layouts/bindings/theme, no scenario or process logic.
- `tests/CoreBankDemo.DemoRunner.Tests/` — state-machine, validation, pre-arm, idempotency, ownership, recovery, provenance, and redaction tests using fakes/Moq.
- `Directory.Packages.props` pins `Terminal.Gui` at `2.4.17`; `CoreBankDemo.sln`/`CoreBankDemo.Rebuild.slnf` add both projects to the ordinary gate.
- `.gitignore` excludes only the generated local artifacts directory (journals, captured output, rehearsal proof packs).

Story 7.4 owns this implementation, reusing Story 7.1–7.3's accepted LoadTestSupport/k6 workflow and evidence sources for the load cue rather than inventing a parallel assertion path.

## Consequences

### Positive

- The speaker gets one dependable, testable control surface instead of five fragile manual surfaces, without touching the banking contract.
- The allow-listed action model and closed project-graph boundary make "no banking logic in the runner" a static, checkable fact rather than a review convention.
- Fail-closed gating and labelled rehearsal fallback keep the demo honest under real failure, which is a stronger, more credible story for a resilience talk than hiding failures.

### Negative / Trade-offs

- A second console UI package (Terminal.Gui) enters the dependency graph solely for this local tool.
- The scenario action allow-list must be extended (not bypassed) for future talks that need a genuinely new capability, which is slower than ad-hoc shell scripting but is the point.
- Maintaining an owned/attached process distinction and fingerprint verification adds implementation surface beyond a naive "just run it" script.

## Key takeaway

> A presentation console can safely narrow the PRD's "no UI" non-goal only by staying outside the banking runtime, acting through allow-listed actions and a fingerprinted process boundary, and refusing — visibly and truthfully — to advance past unproven evidence.
