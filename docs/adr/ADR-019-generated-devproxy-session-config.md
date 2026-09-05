# ADR-019: A generated Dev Proxy session config overrides the checked-in profile in both AppHosts

**Date:** 2026-09-05
**Status:** Accepted
**Deciders:** Architecture team

## Context

Dev Proxy is wired into both AppHosts (`CoreBankDemo.AppHost`, `CoreBankDemo.LoadTests`) behind the `Features:UseDevProxy` gate, but its fault levels live in checked-in JSON: `CoreBankDemo.AppHost/devproxy/config/devproxyrc.json` (with its `devproxy-errors.json`) and `CoreBankDemo.LoadTests/devproxy/config/devproxyrc-latency.json`. The operator console (ADR-015) can therefore demonstrate a dependency *disappearing* — stop a resource — but never one *degrading*, which is both the more common production failure and the harder one to make visible on stage.

Changing levels mid-talk needs a write path. Three constraints bound it:

1. A demo must never dirty the working tree. The checked-in profiles are the named presets a talk points at; if the console edited them, `git status` after a session would be noise and the tuned LoadTests instant-rail-overrun band could be lost.
2. Dev Proxy's REST API (`127.0.0.1:8897`) exposes status, recording, and stop — but **no plugin-configuration mutation**. Nor, as it turns out, can Dev Proxy 3.2.0 reload a changed configuration file: see *Taking effect* below.
3. `errorsFile` resolves relative to the rc file that loaded it, and `pluginPath` uses `~appFolder`. A generated config therefore cannot live in a repo-root dot-directory; it has to sit beside the profile it derives from.

## Decision

Both AppHosts resolve their Dev Proxy config path at startup: **if `<AppHostDir>/devproxy/config/generated/devproxyrc.session.json` exists, it is used; otherwise the checked-in profile is.** The checked-in profiles become read-only preset sources that this console never writes.

- **The generated directory is gitignored** (`*/devproxy/config/generated/`) and is a sibling of the checked-in config, so `errorsFile` and `~appFolder` still resolve.
- **Every write is seeded from the checked-in profile and mutates it in place**, so plugin order, `pluginPath`, `port`, `urlsToWatch`, and any property a section declares that the console does not model are inherited rather than reinvented. A plugin the seed does not declare at all (the LoadTests profile ships latency only) is appended after the ones it does.
- **A zero knob disables its plugin rather than deleting it**, so the file always describes all three knobs and a later read is unambiguous — with one mandatory exception, below.
- **At least one plugin is always left enabled.** Dev Proxy will not start with every plugin disabled: it throws `InvalidOperationException: No plugins configured or enabled. Please add a plugin to the configuration file.` from `PluginServiceExtensions.AddPlugins` and the process exits. This is easy to miss, and it constrains the whole disable-rather-than-delete approach: an all-zero config — which is what panic-off writes, and what arming resets to before an AppHost start — would otherwise bring up a proxy that dies on startup. Because PaymentsAPI routes through `HTTP_PROXY`, every proxied call would then fail outright, making `0` the console's most destructive control rather than its safest one. The generated config therefore keeps `LatencyPlugin` enabled with `latency: { minMs: 0, maxMs: 0 }` whenever nothing else is enabled; that combination is measured to start normally and inject nothing (pass-through). "Enabled" consequently never means "injecting", and the readback is unchanged: a zero latency section still reads as a zero latency knob, so all-zero round-trips to all-zero and the chip still reads `Armed`.
- **Writes are atomic** (temp file, then `File.Move(..., overwrite: true)`). This began as protection against a torn read and is now load-bearing for the opposite reason — see *Taking effect*.
- **The generated errors file covers the same URL surface the profile's other plugins watch.** The checked-in `devproxy-errors.json` is a preset source for the response *bodies* only: it scopes injection to `POST /api/accounts/validate`, which no request the console makes passes through, so copying it verbatim would leave the error-rate knob raisable but unobservable.
- **The file does not outlive the session.** The console deletes it on Stop, on the outgoing half of a Switch, on quit, and when an owned AppHost is observed to have disappeared — because a surviving file silently shadows the checked-in profile for every later non-console run (`aspire run`, a teammate's manual start) and disables the shipped presets. Deletion failure is reported on the evidence strip, never swallowed.
- **Arming stays a launch-time property and defaults off.** `Features:UseDevProxy` is read when the AppHost starts, so the console decides arming when it starts a topology and passes `Features__UseDevProxy` explicitly on the `aspire start` call (verified to propagate into `IConfiguration` against aspire 13.5.3). Dev Proxy is opt-in, so defaulting on would make the binary a hard prerequisite for every console-started topology; preflight probes for `devproxy` whenever arming is requested and blocks Start with a reason if it is missing.

### Taking effect: a controlled resource restart, not a file-watch reload

Dev Proxy 3.2.0 **cannot reload its own configuration**. Measured against a real proxy:

1. A generated config is honoured **at process start** — verified across three latency bands (800–2000 ms → 1.04/1.91/1.77 s observed; 4000–4500 → 4.28/4.02 s; 3000–3500 → 3.12/3.41 s).
2. An atomic temp-then-move write **never fires the watcher**, because it replaces the inode. The proxy keeps serving the config it started with.
3. An in-place write **does** fire the watcher: Dev Proxy logs `Configuration file changed. Restarting proxy...` then `Dev Proxy listening on 127.0.0.1:8000...`, after which it accepts TCP connections and immediately closes them (`Empty reply from server`, HTTP 000) and never serves again. A **new** process with the byte-identical config works perfectly.

So a written file, by itself, changes nothing — and making the watcher fire is worse than useless, because it takes the proxy down. **After every successful write, the console restarts the `devproxy` Aspire resource** (`aspire resource devproxy restart`, already allow-listed) through the same dispatch-and-wait-for-confirmation path every other resource command uses, and reports the levels as applied only once Aspire confirms the resource is back. A restart that is rejected or never confirmed leaves the levels *not* applied, with the failure on the evidence strip — the "a written config is not a live fault" rule, now with a second step that can fail. A successful restart is still not observation: the console waits for its own traffic to carry the levels before reading `Faults in force`.

Two supporting details follow from this and must not be "tidied away":

- **The temp-then-move write is deliberate.** Replacing the inode is what keeps the broken watcher quiet, leaving the controlled restart as the single mechanism. Reverting it to an in-place write reintroduces the dead-proxy failure.
- **Both AppHosts pass `--no-watch`** to `AddDevProxyExecutable`, as belt and braces for anyone editing a config by hand.

This is a workaround for an upstream defect, not a design preference. **Revisit when Dev Proxy fixes restart-on-config-change**: if a live reload becomes reliable, the restart (and its cost — in-flight calls through the proxy fail for a moment, which the workspace states before the operator pays it) can be dropped, and the staged-then-Apply interaction and the applied-versus-observed distinction both survive unchanged.

## Consequences

- A checked-in Dev Proxy profile is now a *default*, not the only source of truth, for anyone reading these AppHosts. The `File.Exists` branch in each AppHost is the one place that says so.
- Fault levels can be changed on a running topology without a restart and without touching tracked files; `git status --porcelain` after a console session is empty.
- The override is deliberately asymmetric with normal configuration precedence: a generated file beats a checked-in one. That is safe only because it is gitignored, session-scoped, and deleted on session end — the three properties above are what keep it from becoming a stale-config trap.
- The console never claims a level is live because a file was written, nor because a restart succeeded: it reports `Applied — not yet observed in traffic` until its own traffic carries the level.
- Applying a level (and panic-off) now costs a proxy restart, so calls in flight through the proxy can fail for a moment. This is stated in the Faults workspace rather than discovered mid-talk. It is the price of the upstream defect above, not of the design.
- The load-test suite is unaffected and remains out of scope: arming Dev Proxy on the LoadTests topology already aborts the k6 run in `setup()` today (the profile's 9500–12000 ms band overruns the instant rail's 9000 ms budget). The console only warns before Run when faults are in force; a fault-tolerant assertion mode is a separate deliverable.
