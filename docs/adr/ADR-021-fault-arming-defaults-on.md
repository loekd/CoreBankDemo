# ADR-021: Fault arming defaults on in the Resources workspace

**Date:** 2026-09-12
**Status:** Accepted
**Deciders:** Architecture team

## Context

ADR-019 made arming a launch-time property that defaults off: *"Dev Proxy is opt-in, so
defaulting on would make the binary a hard prerequisite for every console-started
topology."* In practice this means every demo session that wants to move a fault slider
has to remember to arm first, then stop and restart the topology if it forgot — a
restart the operator often only discovers is needed once they are already on the Faults
workspace mid-talk.

The fault knobs themselves already default to `FaultLevels.AllZero`, and an all-zero
config is measured (ADR-019) to start Dev Proxy normally and inject nothing. Arming by
default therefore does not, by itself, introduce any latency, error rate, or throttling —
it only decides whether Dev Proxy is present to receive levels later.

## Decision

`OperatorConsoleState.FaultArmingRequested` now defaults to `true` for both AppHost
profiles (`OperatorModels.cs`). This supersedes the "defaults off" clause of ADR-019
point 28; the rest of ADR-019 (generated session config, atomic writes, the
restart-to-apply mechanics) is unchanged.

- Both AppHosts started from the Resources workspace now bring up Dev Proxy by default,
  so the fault sliders in the Faults workspace work without a stop/restart cycle.
- The operator can still opt out with the existing arming toggle in Resources; the
  toggle, its read-only-once-running behavior, and the restart-to-change remedy are
  unchanged.
- Preflight (`DoctorRunner.RunAsync`) already blocks Start when arming is requested and
  `devproxy` is not on PATH. Because arming is now the default, `devproxy` becomes a
  Start-time prerequisite for every console-started topology, not just one where the
  operator explicitly opted in.

## Consequences

- A fresh sandbox or machine now needs `devproxy` on PATH before `Start` succeeds in
  either AppHost profile, unless the operator first unarms. This trades a
  previously-optional dependency for one-click slider access on stage.
- Fault levels are still all-zero by default, so an armed-but-untouched topology behaves
  identically to an unarmed one from the payment traffic's perspective: no injected
  latency, errors, or throttling until a knob is staged and applied.
- `Faults: armed on next AppHost start` is now the caption an operator sees on first
  launch of the Resources workspace; explicitly unarming and starting still exercises the
  same unarmed code paths ADR-019/ADR-015 describe.
