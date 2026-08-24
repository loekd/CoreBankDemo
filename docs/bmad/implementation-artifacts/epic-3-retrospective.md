# Epic 3 (E2) Retrospective — ServiceDefaults Rebuild

**Date:** 2026-08-24 · **Stories:** 3.1–3.4 (all done) · **Commits:** 57544ba, f04b6e5, 615c1e1, 9d70bf1

## Verdict: ACCEPTED

`CoreBankDemo.ServiceDefaults` is rebuilt from scratch: validated processing options, a Dapr-backed distributed lock port, CloudEvent wire types plus a publisher port, and `AddServiceDefaults` wiring — 117 tests, 99.6% line / 100% branch coverage, the 90% gate live and un-overridden by the end of the epic (the `Threshold=0` tripwire from story 1.2 finally removed, exactly as reserved). `CoreBankDemo.Messaging` (epic 2, already merged and tested) compiles unmodified against every rebuilt port throughout — the compile-compatibility constraint held for all four stories, verified independently at each story's close, not just claimed.

## Evidence

- Validated options (`ProcessingOptionsBase` + Inbox/Outbox/MessagingOutbox variants): `PartitionCount` fixed to 4 (ruling A3), `LockRenewIntervalSeconds` eliminated as dead config (ruling A4), fail-fast startup validation with all violations reported together (story 3.1).
- `IDistributedLockService`/`DaprDistributedLockService`/`NoOpDistributedLockService`: exact `ExecuteWithLockAsync` signature preserved (zero Messaging source changes required), 5/6-lifetime cooperative cancellation proven via `FakeTimeProvider` with no real clock waits (story 3.2).
- `CloudEventTypes` (already present at epic start, byte-for-byte legacy shapes) plus `IEventPublisher`/`DaprEventPublisher`: thin pass-through adapter, exceptions propagate unchanged (AD-11 stays with the kernel, not this port), JSON snapshot-tested against Dapr's actual `JsonSerializerOptions` defaults (story 3.3).
- `AddServiceDefaults` DI-container-inspection tests for OTel, health checks, service discovery, resilience, and both port factories; `Threshold=0` override removed (story 3.4).

## Real bugs found by review and fixed — the process working as designed

1. **Dispose/timer race (3.2):** the 5/6-cutoff timer callback called `CancellationTokenSource.Cancel()` with no guard against a concurrent `Dispose()` — on a real clock, a workload finishing right as the cutoff fired could race an `ObjectDisposedException` onto an unobserved ThreadPool thread, crashing the process. `FakeTimeProvider`'s synchronous-callback semantics could never have caught this; only blind/edge-case review reasoning about the real-clock path found it. Fixed by extracting the callback into a name method that swallows `ObjectDisposedException`, with a direct regression test.
2. **Ambient token passed to cleanup `Unlock` (3.2):** the `finally` block released the Dapr lock using the ambient (possibly-already-cancelled) token instead of `CancellationToken.None`, risking a leaked server-side lock on shutdown mid-workload while still nominally satisfying the "never throws" contract. Fixed by always releasing with `CancellationToken.None`.
3. **`ResolveOtlpEndpoint` host:port misparse (3.4):** `Uri.TryCreate(endpointValue, UriKind.Absolute, ...)` silently accepted bare `host:port` values like `"jaeger:4317"` by reading the host as the URI scheme, producing a garbage URI and skipping the `http://` normalization the frozen I/O matrix required. Found by the implementer while writing the parsing-matrix tests (not review), independently reproduced during review via a throwaway console app to confirm it wasn't a misunderstanding. Fixed by gating the absolute-URI parse attempt on the value containing `"://"`.

Unlike epic 2, stories 3.1 and 3.3's review passes found no convergent correctness bugs — the pattern held (two independent lenses converging is the reliable signal) but simply had nothing load-bearing to converge on for those two stories. That is itself evidence the review panel isn't just pattern-matching noise into findings; it reports clean when the code is clean.

## Process notes

- The `IEventPublisher`-vs-`IDistributedLockService` registration-style asymmetry (eager `DaprClient` presence check vs. lazy per-resolution check) was a deliberate, spec-frozen design decision in story 3.4 (a no-op event publisher would silently discard events — worse than a clear DI failure) — but blind-hunter review escalated it from "documented tradeoff" to "concrete, already-armed landmine" by actually grepping `CoreBankAPI/Program.cs` and `PaymentsAPI/Program.cs` and finding both currently call `AddServiceDefaults()` before `AddDaprClient()`. Reading the *actual current state* of consumer code, not just the port's own contract, is what turned a theoretical asymmetry into an actionable carry-forward item.
- Three of four stories (3.1, 3.2, 3.4) involved the implementer fixing a real bug *during* implementation or review, not just satisfying the spec as literally written — the spec's frozen I/O matrix repeatedly served as the oracle that made "this doesn't match the matrix" a checkable claim rather than a judgment call.
- `CoreBankDemo.ServiceDefaults/Extensions.cs` and `CloudEventTypes/*.cs` existed pre-epic (scaffolded early so 3.1–3.3 could compile against them) — story 3.3 and 3.4 both had to first verify byte-for-byte/zero-diff against the baseline commit before adding tests, rather than assuming "new files" meant "no legacy behavior to preserve."

## Carry-forward obligations

- **Elevated urgency:** `CoreBankAPI/Program.cs` and `PaymentsAPI/Program.cs` call `AddServiceDefaults()` before `AddDaprClient()` today. The moment epic 4/5 wires real event publishing, `IEventPublisher` will silently never register under that ordering. Whoever adds the first real `IEventPublisher.PublishAsync` call must reorder these two calls first (see `deferred-work.md`).
- Also flagged for epic 4/5: `PaymentsAPI/Program.cs` never calls `AddMessagingOutboxProcessingOptions()` — if it starts resolving `IEventPublisher`, `DaprEventPublisher` would silently bind to unconfigured defaults instead of failing fast.
- `CoreBankDemo.PaymentsAPI/appsettings.json` and `CoreBankDemo.CoreBankAPI/appsettings.json` still carry `PartitionCount:2` and dead `LockRenewIntervalSeconds` keys (carried forward from epic 2/3.1's retrospectives) — epics 4/5 must fix these when rebuilding those projects' config, not just the C# option types, which are already correct.
- `DaprEventPublisher.PublishAsync` applies no validation to `type`/`source`/`subject` (only `traceParent` is checked) — acceptable while the port has zero callers; revisit if epics 4/5 ever feed it unvalidated external input.
- A handful of low-confidence, low-severity items (undisposed `TryLockResponse`, `LogError`-severity ordinary-cancellation noise, snapshot tests' Dapr-defaults claim resting on a comment rather than a live assertion, `ResolveOtlpEndpoint` edge cases outside the frozen matrix) are logged in `deferred-work.md` — none are correctness bugs against any frozen contract, all are "revisit if it ever matters" rather than "must fix."
