---
title: 'DemoRunner Faults workspace — operator-steered Dev Proxy fault injection'
type: 'feature'
created: '2026-09-05'
status: 'done'
review_loop_iteration: 1
baseline_commit: '147e7afc29e43622b7bb8a08ff1d94ff80db7d99'
context:
  - '{project-root}/docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/EXPERIENCE.md'
  - '{project-root}/docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/DESIGN.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Dev Proxy is wired into both AppHosts but its fault levels are frozen in checked-in JSON. The operator cannot raise latency, error rate, or throttling mid-talk, so the console can demonstrate a dependency *disappearing* (stop a resource) but never one *degrading* — the more common production failure and the harder one to show.

**Approach:** Add a fifth **Faults** workspace (key `5`) whose three sliders stage levels and whose single **Apply** writes a generated, gitignored session config that Dev Proxy reloads on its own. Both AppHosts learn to prefer that generated file over their checked-in one, which becomes a read-only preset source. `0` is a global panic-off. The console reports `Applied — not yet observed in traffic` until its own traffic carries the levels, and only then claims `Faults in force`.

## Boundaries & Constraints

**Always:**
- The two spines in `context` are the authoritative contract and win over this spec on any UI conflict.
- Fault controls and panic-off stay enabled while `ActiveMutation` is held — the second named exemption after burst Cancel.
- Applying faults never blocks on, cancels, or is blocked by an in-flight action.
- Severity is carried by number, bar length, and label — never by colour.
- A staged level always renders beside its live level as an explicit delta (`5% → 40%`).

**Ask First:**
- Any change to `k6/script.js` or `LoadTestAssertionService` (see Design Notes — the load suite cannot survive injected errors and that realignment is deferred, not in scope).
- Adding `devproxy` to `KnownResources.RequiredFor` (would make the console refuse to attach when the proxy is off).
- Changing `ICommandRunner.RunAsync`'s existing parameters rather than adding an optional one.

**Never:**
- Write to `CoreBankDemo.AppHost/devproxy/config/devproxyrc.json`, `devproxy-errors.json`, or `CoreBankDemo.LoadTests/devproxy/config/devproxyrc-latency.json`. Read-only preset sources.
- Claim a level is live because a file was written.
- Gate fault changes behind the `Y` confirmation modal, or auto-escalate faults on a timer.
- Inject faults through application config or a fault path inside the banking services.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Stage then apply | Armed topology, error rate dragged 5→40 | Slider shows `5% → 40%`, Apply enabled; on Apply one session config is written containing every knob, chip → `Applied — not yet observed` | Write failure surfaces on the evidence strip, staged values retained, chip unchanged |
| Observation | Applied latency floor 800ms; a payment completes at 950ms after the apply timestamp | Chip → `Faults in force`, level renders live | No qualifying traffic: chip stays `Applied — not yet observed` with elapsed climbing |
| Apply all-zero | Every knob staged to 0 | Session config written with plugins disabled; chip → `Armed` immediately, no observation wait | N/A |
| Panic-off mid-burst | `0` pressed while a 200-payment burst holds `ActiveMutation` | Every knob → 0 and applies in one step, no confirmation; burst continues uninterrupted | Write failure surfaces on evidence strip; knobs stay at their last applied values |
| Unarmed topology | Topology started with `Features:UseDevProxy` false, or Attached | Workspace visibly disabled with the reason and a one-step remedy; sliders dimmed but values legible | N/A |
| Cold start | Console launched against an already-running armed topology | Sliders read the levels actually in force — generated session config if present, else the checked-in profile — never an invented zero | Unreadable/malformed config: sliders show the checked-in defaults and the evidence strip names the failed read |
| Stale session config | A generated config survives from a prior session | Console rewrites it from its own defaults on arming, so a prior session's levels never silently apply | N/A |

</frozen-after-approval>

## Code Map

**Contract**
- `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/EXPERIENCE.md` -- IA:53 (Faults workspace), Component Patterns:94-98, State Patterns:119,125-130, Accessibility:154,156-157, Flow 2:204-215
- `.../DESIGN.md` -- `status-chip-fault`:90, `fault-slider`:173, `fault-preset-chip`:187, `fault-arming-toggle`:193, `apply-faults-button`:199, `panic-off-control`:204, lock-exempt signature:285

**AppHost side (read-only config becomes preset)**
- `CoreBankDemo.AppHost/AppHost.cs:97-107` -- `Features:UseDevProxy` gate; `.WithConfigFile(...)` hard-codes `devproxyrc.json`. Port 8000. `appsettings.json:20-22` has `UseDevProxy: true`.
- `CoreBankDemo.LoadTests/AppHost.cs:66-82` -- same shape, `devproxyrc-latency.json`, port 8001, latency-only 9500-12000ms. `appsettings.json:13-15` has `UseDevProxy: false`.
- `.gitignore` -- existing convention: dot-prefixed, repo-root-anchored, comment cites the story/ADR (`/.demo-runner-exports/`).

**DemoRunner — no DI container, hand-wired**
- `CoreBankDemo.DemoRunner/Program.cs:31-59` -- composition root; `FindRepositoryRoot()` at :118-127 anchors generated paths.
- `Infrastructure/ICommandRunner.cs:26-34` / `CommandRunner.cs:15-29` -- `ProcessStartInfo` never sets `Environment`; no env support exists.
- `Infrastructure/AspireProcessAdapter.cs:50-63` -- builds `aspire start --apphost <path> --format Json --non-interactive --nologo`; ownership is proven by an `aspire ps` pre/post diff plus PID match (:89-96) — keep intact.
- `Infrastructure/ProfileRegistry.cs` -- 16 lines, profile→csproj path only; natural home for per-profile Dev Proxy metadata.
- `Application/OperatorModels.cs:5` `WorkspaceKind` (ordinal is load-bearing — `_workspaces[(int)active]`), `:13` `TopologyProfile`, `:47` `MutationKind`, `:81` `EvidenceKind`, `:165` `ActiveMutation`, `:200-212` `EvidenceRecord`.
- `Application/OperatorConsoleController.cs:13` `EvidenceProvenance`, `:1223-1244` `TryBeginMutation`, `:1292-1331` `AddEvidence`, `:828-850` `CancelActiveBurst` — **the lock-exempt precedent to copy**.
- `Terminal/PresentationModel.cs:106-114` -- topology bar string; today's DevProxy UI is a `DevProxy ON/OFF` substring derived from resource health. `:133-134` `IsBusy`/`CanCancelBurst`.
- `Terminal/MainWindow.cs:31` `NavigationLabels`, `:158-171` nav buttons (Y stride 2) + `_workspaces`, `:173-181` StatusBar, `:283` `BuildResourcesView`, `:420` `BuildLoadView`, `:508-520` `CreateNavigationButton`, `:529-560` helpers, `:644-670` `Render` enablement, `:679-699` `MountWorkspace`, `:779-797` `OnKeyDown` (window-level, already global), `:1019-1094` internal test hooks.
- `Terminal/OperatorTheme.cs:9-13` -- five schemes; no `accent-navy`/`text-muted`/outline token yet.

**Terminal.Gui 2.4.17** -- there is **no `Slider<T>`**. Use `Terminal.Gui.Views.LinearRange<int>`: `RangeKind = LinearRangeSpanKind.Closed` for the two-handle latency band (`Value` is a `LinearRangeSpan<int>` with `Start`/`End`), `LeftBounded` for the single-handle fill-from-left knobs. `Options`, `ValueChanged`/`ValueChanging`, `ShowLegends`, `MinimumInnerSpacing`.

**Tests** -- `tests/CoreBankDemo.DemoRunner.Tests/` xUnit v3 + AwesomeAssertions, hand-written fakes. `Fakes/OperatorHarness.cs:26-42` is the single controller construction point. `Infrastructure/AspireAdapterTests.cs:460-489` `RecordingCommandRunner` asserts exact argv. **90% line-coverage gate with an explicit `<Include>` allow-list at `CoreBankDemo.DemoRunner.Tests.csproj:8` — a new `Infrastructure/` type must be added there.** Will break and need updating: `MainWindowTests.cs:14-53,408-421`, `PresentationModelBuilderTests.cs:16` (`HaveCount(4)`).

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.AppHost/AppHost.cs` + `CoreBankDemo.LoadTests/AppHost.cs` -- resolve the Dev Proxy config path to `devproxy/config/generated/devproxyrc.session.json` when that file exists, else the checked-in default -- lets the console steer levels without ever writing a checked-in file; symmetric in both hosts.
- [x] `.gitignore` -- ignore `*/devproxy/config/generated/` with a comment citing this spec -- generated session configs are per-session and never checked in.
- [x] `CoreBankDemo.DemoRunner/Application/FaultLevels.cs` -- new record carrying error-rate %, latency floor/ceiling ms, throttling requests-per-window, plus `AllZero` and the per-profile preset set -- the single value type staged, applied, stamped, and read back.
- [x] `CoreBankDemo.DemoRunner/Application/Ports/IFaultInjector.cs` -- port: `Read(profile)`, `Write(profile, levels)`, `Reset(profile)` -- keeps the controller free of file I/O, matching the existing ports/adapters split.
- [x] `CoreBankDemo.DemoRunner/Infrastructure/DevProxySessionConfigWriter.cs` -- adapter writing `devproxyrc.session.json` + a sibling `devproxy-errors.session.json`, seeded from the checked-in profile so plugin/urlsToWatch shape is preserved and `errorsFile` stays a relative sibling name; a zero knob disables its plugin rather than deleting it. Follow `SessionEvidenceExporter.cs:280-301` (create dir, catch only `IOException`/`UnauthorizedAccessException`, return a result record) -- **add to the coverage `<Include>` list**.
- [x] `CoreBankDemo.DemoRunner/Infrastructure/ICommandRunner.cs` + `CommandRunner.cs` -- add an optional `IReadOnlyDictionary<string,string>? environment = null` parameter applied to `ProcessStartInfo.Environment` -- the only way to arm a topology at launch; optional so both existing fakes keep compiling.
- [x] `CoreBankDemo.DemoRunner/Infrastructure/AspireProcessAdapter.cs` -- pass `Features__UseDevProxy` on the `aspire start` call from a new arming argument -- env beats `appsettings.json`, so this arms LoadTests and can disarm Regular. Leave the ps-diff/PID ownership proof untouched.
- [x] `CoreBankDemo.DemoRunner/Application/OperatorModels.cs` -- append `Faults` to `WorkspaceKind` (last — ordinal is load-bearing), add `MutationKind.ApplyFaults`, `EvidenceKind.Fault`, and a `FaultLevels` field on `EvidenceRecord` -- provenance must distinguish a `202` under 12s of latency from one under none.
- [x] `CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs` -- staged/applied levels + arming on state; `StageFaults`, `ApplyFaultsAsync`, `PanicOffAsync`, `SetArming`; extend `EvidenceProvenance` and `AddEvidence` with applied levels; flip observed per the rule in Design Notes. `ApplyFaultsAsync`/`PanicOffAsync` must **not** call `TryBeginMutation` — copy `CancelActiveBurst:828-850`.
- [x] `CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs` -- replace the `DevProxy ON/OFF` substring with the tri-state fault chip (`- Unavailable` / `· Armed` / `! Faults in force`), add a `FaultsViewModel` (live+staged per knob, delta strings, preset name or `Custom`, `CanApply`, disabled reason), and a fifth navigation item.
- [x] `CoreBankDemo.DemoRunner/Terminal/MainWindow.cs` -- `BuildFaultsView()` following the `BuildLoadView` pattern: three `LinearRange<int>` knobs, preset chips, Apply in the fixed lower region, panic-off; register `_faultsView` in `_workspaces`, add the rail item (`y: 8`) and StatusBar shortcut, handle `Key.D5` and a global `Key.D0` in `OnKeyDown`, and keep fault controls enabled in `Render` when `IsBusy`. Add internal test hooks matching `:1019-1094`.
- [x] `CoreBankDemo.DemoRunner/Terminal/MainWindow.cs` (Resources) -- arming toggle in `BuildResourcesView`, captioned with its launch-time meaning, read-only on a running topology and disabled with a reason when Attached.
- [x] `CoreBankDemo.DemoRunner/Terminal/OperatorTheme.cs` -- add the outline-teal-no-fill lock-exempt scheme shared by burst Cancel, the sliders, and panic-off.
- [x] `CoreBankDemo.DemoRunner/Program.cs` -- construct and inject `DevProxySessionConfigWriter`.
- [x] `tests/CoreBankDemo.DemoRunner.Tests/` -- unit-test every I/O Matrix row; update `MainWindowTests.cs:14-53,408-421` and `PresentationModelBuilderTests.cs:16` for five workspaces; assert `Features__UseDevProxy` on the recorded `aspire start` argv; assert the writer never opens a checked-in config path; add the new adapter to the coverage `<Include>` list.

**Acceptance Criteria:**
- Given a burst holding `ActiveMutation`, when the operator presses `5` and applies a new latency band, then the workspace is reachable, Apply succeeds, and the burst is neither cancelled nor delayed.
- Given faults applied on the Regular topology, when the operator presses `0` from any workspace, then every knob reaches zero in one step with no confirmation and the chip returns to `Armed`.
- Given any sequence of stage/apply/panic-off operations, when the run ends, then `git status --porcelain` reports no modification to any checked-in Dev Proxy config.
- Given the LoadTests topology is armed, when the Faults workspace opens, then its preset chips offer the tuned 9500-12000ms instant-rail-overrun band as the default preset with error rate and throttling at zero.
- Given faults are in force, when the operator opens the Load Test workspace, then Run states that conditions are non-default before it is fired.
- Given a terminal narrower than the preferred width, when the Faults workspace renders, then the numeric value column survives and the bar degrades first.

## Spec Change Log

- **2026-09-05 — `Reset(profile)` writes all-zero, not the checked-in preset.** The frozen matrix requires arming to rewrite a surviving session config so a prior session's levels never silently apply, and the cold-start row forbids an *invented* zero. Writing zero on arming satisfies both: the console armed the proxy, so zero is the level genuinely in force, not an invention. It also keeps Flow 3 step 7 truthful — the operator *stages and applies* the shipped Regular preset from a chip, which only makes sense if it is not already applied — and keeps an armed LoadTests topology out of the ⚠ k6 incompatibility until the operator deliberately stages the overrun band.
- **2026-09-05 — ~~arming defaults to on~~, and the console always passes `Features__UseDevProxy` explicitly.** ~~With `Reset` writing zero, an armed start lands on the frozen `Armed, none in force` state, so defaulting to armed injects nothing while making the capability reachable in one keypress.~~ **Superseded by Correctness constraint 6 (review loop 1): arming defaults *off*, because defaulting on makes the `devproxy` binary a hard prerequisite for every console-started topology.** The second half stands: passing the variable on every start (`true` *and* `false`) makes the decision explicit for both profiles instead of inheriting whichever value each AppHost's `appsettings.json` ships.
- **2026-09-05 — coarse slider step is `Shift`+arrow, added as an explicit key binding.** Terminal.Gui 2.4.17 ships `Ctrl`+arrow on `LinearRange` bound to `LeftExtend`/`RightExtend`, which moves the *other* handle rather than stepping coarsely. `Shift`+arrow was free and is bound to three `Left`/`Right` commands, matching `EXPERIENCE.md:156`. `Home`/`End` needed no change.
- **2026-09-05 — `IProcessAdapter.StartOwnedAsync` gained an `armFaults` parameter.** The frozen Ask-First list guards `ICommandRunner.RunAsync`'s existing parameters (honoured: `environment` is optional and trails `ct`), not this port. Arming can only be decided at launch, so the start call is the only place it can be carried.

- **2026-09-05 (review loop 1) — six review findings amended into Design Notes rather than re-derived.** Triggering findings: the fault chip stopped consulting the `devproxy` resource at all; the generated session config was never deleted and so shadowed the checked-in profile for non-console runs; the error-rate knob could never be observed; the observation heuristic accepted a slow call or a real outage as proof; cold start claimed observed straight from a file read; arming defaulted on without any check that the binary exists. Amended: the six **Correctness constraints** below. Known-bad state avoided: a console that reports `Faults in force` against a dead proxy, silently disables the shipped presets on a colleague's machine, and can never legitimately prove the error knob. **KEEP on any re-derivation:** the ports/adapters split (`IFaultInjector` + `DevProxySessionConfigWriter`); seeding every write from the checked-in profile; a zero knob disabling rather than deleting its plugin; `ApplyFaultsAsync`/`PanicOffAsync` bypassing `TryBeginMutation`; restricting observation proof to single-call evidence kinds; `LinearRange<int>` with `Closed` for the band and `LeftBounded` for the single knobs; and `Shift`+arrow as the coarse step.

- **2026-09-05 (review loop 1, applied) — the six Correctness constraints and the reviewer patch list are implemented; two reviewer claims were investigated and rejected.** Rejected as false: (a) `Key.D0` breaking digit entry in a `TextField` — Terminal.Gui offers the key to the focused subview first, so a focused `TextField` consumes the digit and `MainWindow.OnKeyDown` never sees it (true of the pre-existing `1`–`4` as well); (b) the need for a process-level `Features__UseDevProxy` fallback — the variable propagates through `aspire start` into the AppHost's `IConfiguration`, verified against aspire 13.5.3. Additional decisions taken while applying the constraints: the latency band's keyboard handling is taken over from `LinearRange` (plain arrow = floor, `Ctrl`+arrow = ceiling, `Shift` coarsens either, `Home`/`End` = floor/ceiling extremes), because Terminal.Gui's `Closed` range drives only one handle from the keyboard and snaps it to the ladder minimum on the first press after a programmatic `Value` assignment; `IPreflightRunner.RunAsync` gained a `faultArmingRequested` argument so a missing `devproxy` binary blocks Start with a reason instead of failing opaquely inside the AppHost; and the override of a checked-in Dev Proxy profile by a generated session config is recorded as **ADR-019**.

- **2026-09-05 (review loop 2) — the Intent's central mechanism does not exist in Dev Proxy 3.2.0.** Measured against a real proxy: a generated config is honoured at process start, an atomic write never fires the watcher (inode replacement), and an in-place write fires it into a self-restart that leaves the proxy accepting and instantly closing every connection. Amended: the **⚠ Dev Proxy 3.2.0 cannot reload its own configuration** block in Design Notes. Known-bad state avoided: an Apply that writes a correct file and reports levels as applied while the proxy serves the old ones — or, with an in-place write, kills the proxy outright mid-talk. **KEEP on any re-derivation:** the temp-then-move write (now load-bearing for *not* firing the watcher), `--no-watch` on both AppHosts, the controlled `devproxy` resource restart reusing the existing resource-command/wait machinery rather than a second one, restart failure never reporting levels as applied, and `FaultsObserved` still requiring traffic proof. The frozen Intent sentence "which Dev Proxy reloads on its own" is left unedited per the frozen-after-approval rule and is corrected here instead.

- **2026-09-05 (review loop 3) — Dev Proxy refuses to start with every plugin disabled, which made panic-off destructive.** Found in live testing: the all-zero config that panic-off writes (and that arming resets to) produced `InvalidOperationException: No plugins configured or enabled...`, so the restart brought up a proxy that exited immediately and every call routed through `HTTP_PROXY` failed. Amended: the **⚠ Dev Proxy will not start with every plugin disabled** note in Design Notes and the matching bullet in ADR-019. Known-bad state avoided: the console's one safety control being its most destructive action, and an armed topology whose Faults workspace is unreachable. **KEEP on any re-derivation:** the at-least-one-enabled-plugin invariant as a property of the writer rather than of the all-zero branch, `LatencyPlugin` at `0/0` as the inert keep-alive, and the unchanged readback (all-zero round-trips to all-zero; `Armed`, never `Faults in force`).

## Design Notes

**Generated config path (the decision the spine left open).** `<AppHostDir>/devproxy/config/generated/` — "alongside the profile it belongs to," per `EXPERIENCE.md:26`. It must be a sibling of the checked-in config because `errorsFile` resolves relative to the rc file and `pluginPath` uses `~appFolder`; a repo-root dot-directory would break both. The console seeds each write from the checked-in profile, so plugin order, `pluginPath`, `port`, and `urlsToWatch` are inherited rather than reinvented.

**Cold-start readback.** The console reads the generated session config if present, else parses the checked-in profile. This is why a zero knob disables its plugin instead of removing it — the file always describes all three knobs, so a later read is unambiguous.

**⚠ Dev Proxy will not start with every plugin disabled (measured, review loop 3).** It throws `InvalidOperationException: No plugins configured or enabled...` and exits. An all-zero config is exactly what panic-off writes and what arming resets to, so without a guard `0` would kill the proxy permanently — and, since PaymentsAPI routes through `HTTP_PROXY`, take every proxied call down with it, inverting `EXPERIENCE.md:97`. Arming would break identically. The writer therefore guarantees at least one enabled plugin for **every** knob combination, keeping `LatencyPlugin` enabled at `minMs: 0, maxMs: 0` when nothing else is — measured to start normally and inject nothing. Readback is unchanged, so all-zero still round-trips to all-zero, `MatchingPresetName` still returns `All off`, and the chip still reads `Armed`.

**Applied vs observed.** All-zero applies land on `Armed` immediately. Otherwise the console waits for an evidence record timestamped after the apply whose duration is at least the applied latency floor, or whose status code matches an injected error. Only the console's own traffic is evidence — Dev Proxy's `127.0.0.1:8897` API exposes status, recording, and stop, but no plugin-config mutation and no reliable per-request feed.

**⚠ Dev Proxy 3.2.0 cannot reload its own configuration (measured, review loop 2).** The Intent's "a generated, gitignored session config that Dev Proxy reloads on its own" is **false as written**, and so is `EXPERIENCE.md:28`'s watched-file-reload assumption. What was measured against a real Dev Proxy 3.2.0:

1. A generated config **is** honoured at process start — verified across three bands (800–2000 ms → 1.04/1.91/1.77 s; 4000–4500 → 4.28/4.02 s; 3000–3500 → 3.12/3.41 s). The writer's output is correct and valid.
2. An **atomic temp-then-move write never fires the watcher at all**, because it replaces the inode. Apply wrote a correct file that was silently never read.
3. An **in-place write does fire the watcher**, and Dev Proxy logs `Configuration file changed. Restarting proxy...` then `Dev Proxy listening on 127.0.0.1:8000...` — after which it accepts TCP connections and immediately closes them (`curl`: `Empty reply from server`, HTTP 000) and never serves again. Killing it and starting a **new** process with the byte-identical config works perfectly.

So there is no working in-process reload path. **Apply and panic-off therefore restart the `devproxy` Aspire resource** after a successful write, through the existing allow-listed resource-command and wait-for-confirmation machinery, and treat the levels as applied only once Aspire confirms the resource is back. The atomic write is kept and is now load-bearing in the opposite direction: it deliberately does *not* trigger the broken watcher, leaving the controlled restart as the single mechanism. Both AppHosts also pass `--no-watch`. A restart that fails is reported and the levels are **not** reported as applied — the same "a written config is not a live fault" rule, now with a second step that can fail. A successful restart is still not observation: `FaultsObserved` continues to require traffic proof. The cost (in-flight calls through the proxy can fail while it comes back) is stated in the workspace before the operator pays it. See ADR-019; revisit when the upstream defect is fixed.

**Correctness constraints (review loop 1).** These are binding, and each exists because the first implementation got it wrong:

1. **The chip must consult the proxy, not just intent.** `Armed` and `Faults in force` both require the `devproxy` resource to be present and Healthy/Running in the current snapshot. If it is absent, stopped, or unhealthy, the chip reports unavailable regardless of what was armed or applied. Replacing the old health-derived `DevProxy ON/OFF` text with a purely intent-derived chip removed the console's only truthful signal about the proxy process.
2. **The generated config must not outlive the session.** Delete it on Stop, on Switch (for the topology being left), and on quit. A surviving file shadows the checked-in profile for every later non-console run (`aspire run`, a teammate's manual start) and silently disables the shipped presets. Deletion failure is reported, never swallowed.
3. **A knob that cannot be observed must not be offered as provable.** The synthesized errors file must cover the same URL surface the profile's other plugins watch, so an injected error can actually reach a call the console makes. Copying the checked-in errors file verbatim scopes injection to `POST /api/accounts/validate` only, which no console request observes directly — making `Faults in force` unreachable via the error path and contradicting `EXPERIENCE.md` Flow 2 step 5.
4. **Observation must be bounded, not merely exceeded.** A latency floor of 0 is never proof (`duration >= 0` is trivially true). Proof requires a non-zero floor, and a duration at or above that floor but not wildly beyond the applied ceiling, so a real outage is not mistaken for injected latency. When proof has not arrived within a bounded window, say so rather than letting the elapsed readout climb forever.
5. **Cold start reads levels, never proof.** Adopting levels from a config file sets the sliders and nothing else. `FaultsObserved` stays false until traffic carries them — the same rule the rest of the feature is built on.
6. **Arming defaults off, and is preflighted.** Dev Proxy is opt-in (`.claude/skills/devproxy-install/SKILL.md`; LoadTests ships `UseDevProxy: false`). Defaulting on makes the binary a hard prerequisite for every console-started topology. `EnvironmentProbe`/`DoctorRunner` must probe for `devproxy` whenever arming is requested, so a missing binary is reported before the start rather than as an opaque failure.

**Verified (review loop 1):** `Features__UseDevProxy` **does** propagate through `aspire start` into the AppHost's `IConfiguration` — confirmed empirically against aspire 13.5.3 with a minimal probe AppHost (`true` → `Features:UseDevProxy=True`; unset → `False`). The documented process-level fallback is not needed.

**⚠ Load-test suite is out of scope and already incompatible.** `k6/script.js:148-153` throws unless a fresh instant payment settles inline within the 9000ms budget — but the LoadTests profile deliberately injects 9500-12000ms. **Arming Dev Proxy on LoadTests aborts the k6 run in `setup()` today, before this change.** Adding errors or throttling additionally breaks at least six server-side invariants deterministically (`NoFailedMessages`, `NoPendingMessages`, `ExpectedUniqueProcessed`, `AllSubmittedProcessed`, `PerKeyOrdering`, `StageCardinality`) — `PerKeyOrdering` most structurally, since retry-after-error *is* a reordering mechanism and a terminally-failed row never gets a `ProcessedAt`. `BalanceConservation` and `BalancesCorrect` keep passing vacuously. A fault-tolerant assertion mode is a separate deliverable; this spec only ensures the console warns before Run when faults are in force.

## Verification

**Commands:**
- `dotnet build CoreBankDemo.sln` -- expected: no warnings-as-errors, no new obsolete-API breaks
- `dotnet test tests/CoreBankDemo.DemoRunner.Tests/` -- expected: all green and the 90% line-coverage gate still met
- `git status --porcelain CoreBankDemo.AppHost/devproxy CoreBankDemo.LoadTests/devproxy` -- expected: empty after a manual console session

**Manual checks:**
- Start the Regular AppHost from the console with arming on; confirm `devproxy` appears in `aspire ps` and that `Features__UseDevProxy` actually reaches the AppHost through `aspire start` (the one unverified link in the arming chain — if env does not propagate, fall back to setting it on the console's own process, which `CommandRunner` already inherits).
- At 80x24, confirm the value column and both latency handles remain readable and keyboard-operable.

## Suggested Review Order

**The steering mechanism (start here)**

- Entry point: how a fault level becomes a file Dev Proxy reloads, seeded from the checked-in preset.
  [`DevProxySessionConfigWriter.cs:24`](../../../CoreBankDemo.DemoRunner/Infrastructure/DevProxySessionConfigWriter.cs#L24)

- The single value type staged, applied, stamped and read back; a zero knob disables, never deletes.
  [`FaultLevels.cs:12`](../../../CoreBankDemo.DemoRunner/Application/FaultLevels.cs#L12)

- The port that keeps file I/O out of the controller; note `DeleteAsync` — the config must not outlive the session.
  [`IFaultInjector.cs:27`](../../../CoreBankDemo.DemoRunner/Application/Ports/IFaultInjector.cs#L27)

- Path shared with both AppHosts by convention only; the tie is asserted, not assumed.
  [`ProfileRegistry.cs:52`](../../../CoreBankDemo.DemoRunner/Infrastructure/ProfileRegistry.cs#L52)

**The two AppHost changes that make live steering possible at all**

- Generated config wins when present; the checked-in profile stays a read-only preset.
  [`AppHost.cs:105`](../../../CoreBankDemo.AppHost/AppHost.cs#L105)

- Symmetric, so the tuned 9500-12000ms band is never edited to be used.
  [`AppHost.cs:80`](../../../CoreBankDemo.LoadTests/AppHost.cs#L80)

**Truth model — the part most worth arguing with**

- Apply and panic-off deliberately bypass the in-flight lock, serialized instead and re-checked after the await.
  [`OperatorConsoleController.cs:948`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L948)

- De-escalation is one key and never confirmed; escalation stays two steps.
  [`OperatorConsoleController.cs:970`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L970)

- What counts as proof: single-call kinds only, bounded floor-to-ceiling, never an aggregate's duration.
  [`OperatorConsoleController.cs:1671`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L1671)

- Cold start adopts levels for the sliders and nothing else — a file read is never proof.
  [`OperatorConsoleController.cs:356`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L356)

- Deletion on Stop, Switch and quit, so the file never shadows the preset for a non-console run.
  [`OperatorConsoleController.cs:256`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L256)

**Arming is a launch-time property**

- Optional environment dictionary; the only way to arm a topology.
  [`CommandRunner.cs:14`](../../../CoreBankDemo.DemoRunner/Infrastructure/CommandRunner.cs#L14)

- Passes `Features__UseDevProxy` explicitly both ways rather than inheriting each appsettings.json.
  [`AspireProcessAdapter.cs:43`](../../../CoreBankDemo.DemoRunner/Infrastructure/AspireProcessAdapter.cs#L43)

- Preflights the binary only when arming is requested, so a missing devproxy blocks Start with a reason.
  [`EnvironmentProbe.cs:18`](../../../CoreBankDemo.DemoRunner/Infrastructure/EnvironmentProbe.cs#L18)

**Presentation**

- The chip requires the devproxy resource to be running; intent alone never earns `Armed`.
  [`PresentationModel.cs:240`](../../../CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs#L240)

- Tri-state, symbol plus label, never colour alone; severity is deliberately colourless.
  [`PresentationModel.cs:242`](../../../CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs#L242)

- Three `LinearRange<int>` knobs; the band's keys are owned here because Terminal.Gui drives one handle.
  [`MainWindow.cs:215`](../../../CoreBankDemo.DemoRunner/Terminal/MainWindow.cs#L215)
