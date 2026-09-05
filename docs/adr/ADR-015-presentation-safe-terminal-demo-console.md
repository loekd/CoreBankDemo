# ADR-015: Reusable terminal demo operator console

**Date:** 2026-08-29
**Status:** Accepted
**Deciders:** Architecture team (human scope amendments 2026-08-29 and 2026-09-03, Story 7.4)
**Supersedes:** None

## Context

Running a conference demo asks the operator to coordinate several fragile surfaces by hand: Aspire processes, health checks, `.http` requests, standard and instant payments, DevProxy fault configuration, load testing, logs, and browser dashboards. A missed prerequisite, duplicate action, stale process, or hidden assertion failure can derail the demonstration even when the banking system itself is correct.

The first Story 7.4 implementation interpreted this need as a scenario-driven cue player tied to one slide deck. Operational use showed that model was too rigid: the same safe capabilities must support multiple talks and unplanned audience questions. The user explicitly replaced the cue queue, slide anchors, `Next` gating, checkpoint resume, and rehearsal-proof fallback with a reusable capability-driven operator console.

The PRD's broad "no UI" non-goal targets a banking-product UI. The mouse-enabled terminal console remains a narrower, human-approved presentation-tool exception that does not authorize a banking UI, a new banking endpoint, or a change to the external contract frozen by AD-1/ADR-008.

## Decision

Maintain the standalone .NET 10 `CoreBankDemo.DemoRunner`, but organize it as a reusable operator console rather than a talk-scenario runtime. Its primary information architecture is **Operations**, **Resources**, **Evidence/Results**, and **Load Test**, with a persistent topology bar and evidence strip. Named talks validate capability coverage but never define navigation.

DemoRunner operates entirely outside the banking runtime and never becomes a prerequisite for development, tests, or the banking services themselves.

### Standalone local-tool boundary

`CoreBankDemo.DemoRunner` references only `System.*`/BCL assemblies and its own UI package. It contains **no project reference** to `CoreBankDemo.CoreBankAPI`, `CoreBankDemo.PaymentsAPI`, `CoreBankDemo.Messaging`, `CoreBankDemo.LoadTestSupport`, or any EF Core `DbContext`. It never opens a Postgres, Redis, Dapr, or container-engine socket. All interaction with the banking system happens through stable local HTTP endpoints (PaymentsAPI, CoreBankAPI, LoadTestSupport's REST surface — reusing the same endpoints as `demo-requests.http`/`payment-idempotency-tests.http`, never new ones) and a narrowly scoped child-process adapter that starts or attaches to a known Aspire AppHost profile. This keeps the project-graph invariant machine-checkable: `dotnet list CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj reference` must show zero banking implementation projects, forever.

### Terminal.Gui as the pinned TUI adapter

Terminal.Gui's stable v2 line is the only UI package, pinned centrally at **2.4.17** in `Directory.Packages.props` (one version for the whole repo, consistent with existing central package management). The package boundary stays thin: `Terminal/` renders immutable presentation-state view models and emits user intents onto the application's command channel; it contains no process or HTTP logic. This keeps application state, authority rules, and evidence handling unit-testable without a real terminal — Terminal.Gui rendering is exercised only behind a thin, fake-able adapter interface.

### Typed capability model, not scenarios or arbitrary commands

The application accepts typed operator intents for a closed set of capabilities: select/start/attach/stop/switch a known AppHost profile; inspect a known topology; start/stop/restart an allow-listed Aspire resource; submit/query a payment through known local endpoints; run/cancel a bounded payment burst; execute the accepted load workflow; inspect/export evidence; and open a known dashboard URL.

There is no scenario-provided process path, shell text, database statement, unrestricted URL, or arbitrary executable. `mission-critical-talk-v7.json`, cue/slide navigation, `--scenario`, `--show`, `--rehearse`, and `--resume` are retired from the supported product path.

### Process ownership, attach, cleanup, and evidence

- **Ownership:** the process adapter starts the exact known AppHost project (`CoreBankDemo.AppHost` or `CoreBankDemo.LoadTests`) as a tracked child process tree and records that PID tree as *owned* for the session.
- **Attach:** if a healthy topology matches the expected resource/service/endpoint fingerprint, the runner offers explicit Attach instead of starting a second instance. Attached processes remain *unowned*: whole-AppHost Stop and Switch are forbidden. A fresh fingerprint match separately grants **resource-command authority** for allow-listed `aspire resource <resource> start|stop|restart` operations. Every disruptive resource command is individually confirmed; fingerprint loss or stale/unparseable state revokes that authority until a fresh match succeeds.
- **Cleanup:** on normal exit, cancellation, or Ctrl+C, only owned child trees receive graceful cancellation (SIGINT/close-then-wait) before forced termination; unowned/attached processes are never touched. No broad process-name or port-based kill command is ever issued.
- **Session evidence:** operation records are bounded and redacted and carry topology/profile plus run-generation provenance. They may be exported explicitly, but are not a recovery journal. On relaunch DemoRunner re-reads live Aspire/HTTP state and starts with empty operation history; it never restores a prior Passed state or checkpoint.

### Truthful operational state improves reliability without claiming infallibility

"Presentation safe" is not an availability guarantee. Assertions, health probes, resource transitions, payment outcomes, ambiguity, and timeouts are typed results and are never inferred from elapsed time or log text. `202 Pending` is durable uncertainty, not failure. Old evidence remains visible only with unmistakable topology/generation provenance. A failed live operation is never replaced or recolored by previous evidence.

### Single mutation lock and destructive confirmation

Only one mutating operation may be in flight across the console. Read-only inspection remains available, and the active burst's own Cancel is the sole exception. Resource Stop/Restart, whole-AppHost Stop/Switch, and Load Test Run require a modal naming the exact target and command. Cancel receives initial focus; `Y` confirms and Escape cancels. There is no hold gesture or double-Enter confirmation.

### Payment and load-test semantics remain owned by their existing contracts

Payment operations present ADR-018 exactly: standard is `202 Pending`; instant is committed `200 Completed`/`Failed` or truthful `202 Pending`. Generated and supplied idempotency keys remain stable for resend; omitted-key ambiguity is never retried automatically.

The Load Test workspace is an adapter over the accepted Reset → Run → Wait → Assert → Investigate workflow. Reset is Run's first internal phase and applies only to disposable LoadTests state. DemoRunner displays the five invariants and inline-instant-settlement evidence from k6/LoadTestSupport; it does not create another assertion authority.

### `.http` files remain the supported fallback and behavioral oracle

`demo-requests.http` and `payment-idempotency-tests.http` are unchanged by this story and remain fully runnable without the runner — they are both the manual fallback path and the reference for what "correct" HTTP behavior looks like when the runner's `sendHttp`/`assertHttp` actions are implemented and tested.

## Implementation

- `CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj` — standalone net10.0 executable; `Terminal.Gui` package reference only, version resolved centrally.
- `CoreBankDemo.DemoRunner/Program.cs` — composition root and ordinary operator-console CLI binding only.
- `CoreBankDemo.DemoRunner/Application/` — typed topology/resource/payment/burst/evidence/load state and commands behind process, Aspire, HTTP, health, browser, export, and time ports.
- `CoreBankDemo.DemoRunner/Infrastructure/` — owned Aspire process lifecycle, supported Aspire state/resource CLI, allow-listed HTTP/LoadTestSupport operations, health monitor, browser opener, and bounded/redacted evidence export.
- `CoreBankDemo.DemoRunner/Terminal/` — the five-workspace Terminal.Gui shell (Operations, Resources, Evidence/Results, Load Test, Faults — see ADR-019), bindings, responsive layout, confirmation modal, and theme; no process/HTTP/business logic.
- `tests/CoreBankDemo.DemoRunner.Tests/` — application-state, ownership/authority, resource transition, payment/idempotency, burst/cancellation, evidence provenance, load workflow, recovery, redaction, and presentation-model tests.
- `Directory.Packages.props` pins `Terminal.Gui` at `2.4.17`; `CoreBankDemo.sln`/`CoreBankDemo.Rebuild.slnf` add both projects to the ordinary gate.
- `.gitignore` excludes only generated local artifacts such as captured output and explicit evidence exports.

Story 7.4 owns this implementation and reuses the accepted LoadTestSupport/k6 workflow and evidence sources rather than inventing a parallel assertion path.

## Consequences

### Positive

- The operator gets one reusable, testable control surface instead of a deck-specific script and several manual surfaces, without touching the banking contract.
- The typed capability model and closed project-graph boundary make "no banking logic in the runner" a static, checkable fact.
- Live-state rehydration and provenance-labeled evidence keep the console truthful without pretending a previous rehearsal proves the current run.

### Negative / Trade-offs

- A second console UI package (Terminal.Gui) enters the dependency graph solely for this local tool.
- The capability allow-list must be extended, not bypassed, when a future demonstration needs a genuinely new operation.
- Maintaining separate whole-AppHost ownership and attached-resource command authority adds state and verification complexity.
- Retiring cue/checkpoint/proof-pack behavior is a deliberate breaking change to DemoRunner's local CLI and saved artifacts.

## Key takeaway

> A reusable presentation console can safely narrow the PRD's "no UI" non-goal only by staying outside the banking runtime, exposing typed allow-listed capabilities, separating process ownership from verified resource authority, and presenting live state and evidence truthfully.
