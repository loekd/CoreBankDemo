---
title: 'DemoRunner: a working Stop/Start button for a resource'
type: 'bugfix'
created: '2026-09-10'
status: 'done'
review_loop_iteration: 1
baseline_commit: '3d21b2f43e1596de13e1d16300f3325711f8e328'
context: ['{project-root}/docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10-resource-lifecycle/prd.md']
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Stopping `corebank-api` from the console makes `IsFingerprintMatch` false, and the console reads that as "I am looking at a different system". Every resource button goes dead, so Start is unreachable; arriving CloudEvents are discarded, so settled payments read "Awaiting settlement" for ever; and the Stop command itself can never be confirmed, so it times out and files a failure. The outbox demo cannot be given.

**Approach:** Stop using topology *shape* to decide identity. One rule, applied once: a run is the same run when its profile and run generation are the same. Then make the two resource controls name the action and resource they act on, and drop the Switch button.

## Boundaries & Constraints

**Always:**
- Keep refusing resource commands when the topology is unreachable, when the snapshot could not be parsed, or when it is stale beyond `SnapshotFreshness`.
- Keep the resource allow-list, the per-replica fan-out, and the confirmation dialog for Stop and Restart.
- Keep load-test gating as strict as it is today — it must keep going through `TopologySnapshot.IsReady`, which checks the fingerprint itself and is not touched by this change.

**Ask First:**
- Nothing. The operator has settled the open questions: always a clean start, never Attach, never a whole-AppHost stop or Switch mid-session. Simplicity beats blast-radius caution here — prefer one rule over carve-outs.

**Never:**
- Touch PaymentsAPI, CoreBankAPI, or any Dapr component file.
- Add a listening port or a new owned process.
- Add machinery for externally relaunched AppHosts or attached-mode drift. Out of scope by operator decision.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Stop confirms | Operator stops `corebank-api`; shape now mismatched | Command confirmed and filed as succeeded — no timeout, no ambiguous evidence, authority retained | N/A |
| Start after Stop | `corebank-api` stopped; snapshot fresh and reachable | Control enabled, reads `Start corebank-api`, dispatches to every replica | N/A |
| Recovery broadcast | `transaction.completed` arrives while shape mismatched, same profile + generation | Row resolves in place; event filed in Evidence | N/A |
| Payment across recovery | Submit while stopped, response returns after Start moved the shape | Row is created/updated normally | N/A |
| Feed lost | Feed reports `Lost` while a resource is stopped | Status reaches the operator; reconnect stays eligible | N/A |
| New run | Event or result arrives after a topology start (new generation) | Discarded | Silent, unchanged |
| Unreadable snapshot | `aspire describe` returns unparseable JSON | Resource commands refused; nothing dispatched | Rejected with the parse error |
| Unreachable / stale | Aspire unreachable, or snapshot older than `SnapshotFreshness` | Resource commands refused | Rejected, hint names Refresh |
| No legal action | Selected resource is `Unknown`/`Unreachable`/`Starting` | Control disabled; caption never reads `Unavailable <name>`; hint says why | No press-time failure |
| Failed resource | Next action is already Restart | The two controls never read the same words | Restart control stands down |
| Long name | Caption would exceed the action column | Falls back to the verb alone rather than truncating | N/A |

</frozen-after-approval>

## Code Map

- `Application/OperatorConsoleController.cs`
  - `:3573` `IsCurrent` — **delete the fingerprint clause; keep profile + `RunGeneration`.** One predicate, all 19 call sites, no sibling. A clean start still isolates runs: `ActivateTopology:3294` moves the generation and `StopOutcomeFeedAsync:2231` nulls `_feedContext`.
  - `:348` `ResourceAuthorityAvailable` (per-poll) and `:3178` `HasFreshResourceAuthority` — drop `IsFingerprintMatch`. `ErrorSummary` must still block, but it carries **two** meanings: the parser's shape-mismatch text (stop blocking on it) and `TopologyObservationDebouncer`'s "waiting for one confirming snapshot" hold (keep blocking — that is uncertainty, not shape). Name the debouncer's string a constant and test for it explicitly.
  - `:3254` `WaitForResourceAsync` — **requires `IsFingerprintMatch` to confirm, which a successful Stop makes false.** This is why Stop times out, files ambiguous evidence and revokes authority. Confirm on `IsReachable && ResourceReachedTarget(...)`.
  - `:669` rejection text promises a "fingerprint-matching" snapshot — reword.
- `Infrastructure/AspireCliAdapter.cs:151` — guard is `!IsReachable || !IsFingerprintMatch || resource is null`. Replace the middle clause with an **unreadable-snapshot** check: `AspireJsonParser`'s malformed-JSON branch (`AspireJsonParser.cs:61-73`) returns `IsReachable: true` with `Fingerprint = string.Empty` and fabricated `Unknown` resources whose `Supports()` answers true, so without this the console dispatches against a graph it could not read. Empty `Fingerprint` is the marker.
- `Terminal/PresentationModel.cs`
  - `:1261` `CanMutateResource` — drop `IsFingerprintMatch: true`.
  - `:1220` mismatch hint — report the state; do not prescribe "Stop and Start it again". Keep it short enough to render at 80 columns.
  - `:1208` cold hint names "Switch" — remove that word.
  - `:222`/`:230`/`:1289` — `NextAction` already yields `Start`/`Stop`/`Restart`/`Unavailable` per row. Reuse; `Unavailable` is a sentinel, never a verb.
- `Terminal/MainWindow.cs`
  - `:36` `ActionColumnWidth = 22` → 28; `:709` list width and `:716-718` frame derive from it.
  - `:224` `_switchButton`, handler `:743`, enablement `:1553` — remove all three. Leave `SwitchAsync`, `MutationKind.SwitchTopology` and `CanStopOrSwitch` in place.
  - `:225`/`:226` captions; `:723` `StackButtons` (stacks **vertically**, `Width = Dim.Fill()`); `:1554-1555` enablement (currently `Resources.Any(...)`, must follow the selection); `:2303` `TriggerSelectedResourceAction` and `:759` the restart handler — both recompute the selection, so route them through one shared selected-row helper.
  - `:862` `_evidenceList.ValueChanged` with the `_rebindingEvidenceList` guard — the idiom to copy for the resource list.
  - Rows come from `state.Topology.Resources`, **not** the allow-list, so `Restart loadtest-initializer` (28) is reachable; the column holds 28 minus button decoration.
- `docs/adr/ADR-015-presentation-safe-terminal-demo-console.md:45` — states that a fresh fingerprint match grants resource-command authority and that "fingerprint loss or stale/unparseable state revokes that authority". This change reverses the fingerprint half; the stale/unparseable half stays true.
- Tests: `Application/OperatorConsoleControllerTests.cs` (`:434` debounce refusal already pins the hold), `Infrastructure/AspireAdapterTests.cs`, `Terminal/MainWindowTests.cs`, `Terminal/PresentationModelBuilderTests.cs`, `Fakes/OperatorHarness.cs`.

## Tasks & Acceptance

**Execution:**
- [x] `Application/OperatorConsoleController.cs` -- delete the fingerprint clause from `IsCurrent`, leaving profile + `RunGeneration`; update its doc comment to say why shape is not identity.
- [x] `Application/OperatorConsoleController.cs` -- drop `IsFingerprintMatch` from `ResourceAuthorityAvailable` and `HasFreshResourceAuthority`; keep blocking on the debouncer hold via a named constant; reword the `:669` rejection.
- [x] `Application/OperatorConsoleController.cs` -- fix `WaitForResourceAsync` so a Stop can be confirmed -- without this the headline flow still fails.
- [x] `Infrastructure/AspireCliAdapter.cs` -- swap the fingerprint clause for an unreadable-snapshot check, rejecting with the parse error.
- [x] `Terminal/PresentationModel.cs` -- relax `CanMutateResource`; reword the mismatch hint; drop "Switch" from the cold hint.
- [x] `Terminal/MainWindow.cs` -- remove the Switch button; widen the column to 28.
- [x] `Terminal/MainWindow.cs` -- caption both controls from the selected row (`"{NextAction} {Name}"`), falling back to the verb alone when the caption will not fit and to the existing neutral label when there is no legal action; key enablement off that row; stand the restart control down when the action control already offers Restart; refresh on `ValueChanged` behind a rebind guard; route both press paths through the shared selected-row helper.
- [x] `Terminal/MainWindow.cs` -- let Start dispatch without the confirmation dialog (PRD FR20); Stop and Restart keep theirs.
- [x] `docs/adr/ADR-015-presentation-safe-terminal-demo-console.md` -- amend the resource-command-authority clause: authority follows a reachable, readable, fresh snapshot; an unreadable or stale snapshot still revokes it; shape no longer does.
- [x] `tests/...` -- cover every matrix row, with the Stop-confirms and unreadable-snapshot rows explicitly; assert the caption text (not merely enablement) for a no-legal-action row and for the longest reachable name; pin that a mismatched LoadTests graph still refuses `CanRunLoadTest` and `RunLoadTestAsync`.

**Acceptance Criteria:**
- Given `corebank-api` is running, when the operator stops it, then the command is confirmed and filed as succeeded, and the control then offers `Start corebank-api` and is enabled.
- Given a settlement broadcast arrives while a resource is stopped, when it is handled, then its row resolves and the event appears in Evidence.
- Given `aspire describe` returns unparseable JSON, when any resource command is attempted, then it is rejected and no CLI command runs.
- Given a LoadTests graph that does not match its profile, when a load test is attempted, then it is refused as before.

## Spec Change Log

- **Trigger:** three review layers, with probe evidence. `WaitForResourceAsync` still gated Stop confirmation on `IsFingerprintMatch`, so the headline flow timed out and revoked authority; an unparseable snapshot dispatched a real CLI command; the `Unavailable` sentinel rendered as a verb; caption width was derived from the allow-list while rows come from the topology; PRD FR17/FR20 were dropped silently; ADR-015 was reversed without amendment.
- **Amended:** the operator renegotiated the frozen intent — always a clean start, never Attach or a mid-session whole-AppHost stop — which removes the reason for a carved-out sibling predicate. `IsCurrentRun` is gone; `IsCurrent` itself loses the fingerprint clause. Added the wait-path fix, the unreadable-snapshot guard, caption fallbacks, FR20, and the ADR amendment. The "Ask First" gate on non-feed call sites is retired by that same decision.
- **Known-bad state avoided:** a build where Stop appears to work, files a failure, and re-disables the button that undoes it.
- **KEEP:** the `ErrorSummary` two-meanings insight from iteration 1 — the debouncer's hold must keep blocking mutations while the parser's shape text must not; a named constant plus an explicit test is the right shape, and `Refresh_ExternalStateChange_IsDebouncedUntilSecondSnapshot` must keep passing. Also keep: enablement driven by the selected row rather than `Resources.Any(...)`, the `ValueChanged` + rebind-guard idiom, and `SwitchAsync`/`CanStopOrSwitch` left untouched.

- **Trigger (live run, after the suite was green):** starting the Regular AppHost and stopping both `corebank-api` replicas through the Aspire CLI showed that Aspire reports a stopped project as **`Finished`**, never `Stopped`. `MapCondition` reads that as `Completed`, so two things were still broken with every unit test passing: `ResourceReachedTarget` accepted only `Stopped`, so a Stop could still never confirm; and `NextAction` had no case for `Completed`, so the button read "Unavailable" and never offered Start. Every fake in the suite used `Stopped`, so nothing could have caught it.
- **Amended:** `NextAction` now treats `Completed` like `Stopped` and offers Start; `ResourceReachedTarget` accepts `Stopped or Completed` for a Stop. `MapCondition` is deliberately untouched — `loadtest-initializer` legitimately completes, and changing the parser's vocabulary would move `IsReady` with it.
- **Known-bad state avoided:** a fully green build in which the demo's Stop still times out and the Start button never appears.
- **KEEP:** the regression tests are written from bytes captured off the live AppHost (`state: "Finished"`, `exitCode: 0`, empty `urls`, `commands.start` enabled). Do not "tidy" them back to `Stopped` — that is precisely the fiction that hid this.

## Design Notes

The whole change is one idea: **shape is not identity.** A resource the operator stopped is the same run, so neither correlation nor command authority may key on the fingerprint. What still isolates a run is the generation, which only a topology start moves.

Deliberately dropped as out of scope by operator decision: attached-mode external relaunch, and any reconcile machinery for rows stranded in an already-running session.

## Verification

**Commands:**
- `dotnet tool restore` -- expected: succeeds (Kiota generation needs it).
- `dotnet build CoreBankDemo.sln` -- expected: 0 errors, 0 new warnings.
- `dotnet test tests/CoreBankDemo.DemoRunner.Tests` -- expected: all pass, coverage gate satisfied.
- `git status --porcelain` -- expected: no stray probe or scratch files left in the test project.
