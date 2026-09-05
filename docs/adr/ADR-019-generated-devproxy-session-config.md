# ADR-019: A generated Dev Proxy session config overrides the checked-in profile in both AppHosts

**Date:** 2026-09-05
**Status:** Accepted
**Deciders:** Architecture team

## Context

Dev Proxy is wired into both AppHosts (`CoreBankDemo.AppHost`, `CoreBankDemo.LoadTests`) behind the `Features:UseDevProxy` gate, but its fault levels live in checked-in JSON: `CoreBankDemo.AppHost/devproxy/config/devproxyrc.json` (with its `devproxy-errors.json`) and `CoreBankDemo.LoadTests/devproxy/config/devproxyrc-latency.json`. The operator console (ADR-015) can therefore demonstrate a dependency *disappearing* — stop a resource — but never one *degrading*, which is both the more common production failure and the harder one to make visible on stage.

Changing levels mid-talk needs a write path. Three constraints bound it:

1. A demo must never dirty the working tree. The checked-in profiles are the named presets a talk points at; if the console edited them, `git status` after a session would be noise and the tuned LoadTests instant-rail-overrun band could be lost.
2. Dev Proxy's REST API (`127.0.0.1:8897`) exposes status, recording, and stop — but **no plugin-configuration mutation**. The file Dev Proxy watches is the only mechanism available for changing levels on a running proxy.
3. `errorsFile` resolves relative to the rc file that loaded it, and `pluginPath` uses `~appFolder`. A generated config therefore cannot live in a repo-root dot-directory; it has to sit beside the profile it derives from.

## Decision

Both AppHosts resolve their Dev Proxy config path at startup: **if `<AppHostDir>/devproxy/config/generated/devproxyrc.session.json` exists, it is used; otherwise the checked-in profile is.** The checked-in profiles become read-only preset sources that this console never writes.

- **The generated directory is gitignored** (`*/devproxy/config/generated/`) and is a sibling of the checked-in config, so `errorsFile` and `~appFolder` still resolve.
- **Every write is seeded from the checked-in profile and mutates it in place**, so plugin order, `pluginPath`, `port`, `urlsToWatch`, and any property a section declares that the console does not model are inherited rather than reinvented. A plugin the seed does not declare at all (the LoadTests profile ships latency only) is appended after the ones it does.
- **A zero knob disables its plugin rather than deleting it**, so the file always describes all three knobs and a later read is unambiguous.
- **Writes are atomic** (temp file, then `File.Move(..., overwrite: true)`), because Dev Proxy watches the path and would otherwise reload a half-written document.
- **The generated errors file covers the same URL surface the profile's other plugins watch.** The checked-in `devproxy-errors.json` is a preset source for the response *bodies* only: it scopes injection to `POST /api/accounts/validate`, which no request the console makes passes through, so copying it verbatim would leave the error-rate knob raisable but unobservable.
- **The file does not outlive the session.** The console deletes it on Stop, on the outgoing half of a Switch, on quit, and when an owned AppHost is observed to have disappeared — because a surviving file silently shadows the checked-in profile for every later non-console run (`aspire run`, a teammate's manual start) and disables the shipped presets. Deletion failure is reported on the evidence strip, never swallowed.
- **Arming stays a launch-time property and defaults off.** `Features:UseDevProxy` is read when the AppHost starts, so the console decides arming when it starts a topology and passes `Features__UseDevProxy` explicitly on the `aspire start` call (verified to propagate into `IConfiguration` against aspire 13.5.3). Dev Proxy is opt-in, so defaulting on would make the binary a hard prerequisite for every console-started topology; preflight probes for `devproxy` whenever arming is requested and blocks Start with a reason if it is missing.

## Consequences

- A checked-in Dev Proxy profile is now a *default*, not the only source of truth, for anyone reading these AppHosts. The `File.Exists` branch in each AppHost is the one place that says so.
- Fault levels can be changed on a running topology without a restart and without touching tracked files; `git status --porcelain` after a console session is empty.
- The override is deliberately asymmetric with normal configuration precedence: a generated file beats a checked-in one. That is safe only because it is gitignored, session-scoped, and deleted on session end — the three properties above are what keep it from becoming a stale-config trap.
- The console never claims a level is live because a file was written: it reports `Applied — not yet observed in traffic` until its own traffic carries the level, since Dev Proxy owns its reload timing.
- The load-test suite is unaffected and remains out of scope: arming Dev Proxy on the LoadTests topology already aborts the k6 run in `setup()` today (the profile's 9500–12000 ms band overruns the instant rail's 9000 ms budget). The console only warns before Run when faults are in force; a fault-tolerant assertion mode is a separate deliverable.
