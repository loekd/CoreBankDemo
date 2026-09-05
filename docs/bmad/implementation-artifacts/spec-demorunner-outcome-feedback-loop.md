---
title: 'DemoRunner outcome feedback loop'
type: 'feature'
created: '2026-09-05'
status: 'done'
review_loop_iteration: 0
baseline_commit: '78e660d3fc357d7800ad8b9461da95fa3b1d8afe'
context:
  - '{project-root}/docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/EXPERIENCE.md'
  - '{project-root}/docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/DESIGN.md'
  - '{project-root}/docs/adr/ADR-015-presentation-safe-terminal-demo-console.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** A payment submitted from the console returns `202 Pending` and then nothing. CoreBank already broadcasts the committed outcome on `transaction-events`, and PaymentsAPI already consumes it, but the console never listens — so the operator must remember to run an outcome query, and on stage a settled payment looks identical to a stuck one.

**Approach:** The console subscribes to `transaction-events` itself, through a `daprd` sidecar it spawns and owns, using a `Dapr.Messaging` streaming subscription (outbound gRPC — no inbound listener, no port opened). Its own app-id yields its own consumer group, so PaymentsAPI keeps receiving every event. Submitted payments become tracked state that resolves **in place** when its event arrives; arriving events also append to Evidence. Nothing is added to any banking service.

## Boundaries & Constraints

**Always:**
- Correlate by `TransactionId` only — already returned on the `202` and carried by all three events.
- One `daprd` per topology, started with that profile's components directory (`dapr/components` = Redis 6379; `dapr/components-loadtest` = 6381). A mismatched directory connects to nothing.
- The console owns the sidecar: verify PID, tear it down on topology stop/switch, on `ShutdownAsync`, and in `RefreshAsync`'s topology-disappeared branch.
- Allocate the sidecar's grpc/http/metrics ports via `IEnvironmentProbe.IsPortFreeAsync` and pass all three explicitly (`--metrics-port` defaults to 9090 and would collide).
- Silence is never an outcome: no timeout converts an awaiting payment into a failure. Only a `transaction.failed` event or an operator-run outcome query proves rejection.
- Feed liveness is stated inline on the awaiting row (`(listening)`); when the subscription drops, unresolved rows change state to `Outcome unknown`, never left reading `Awaiting settlement`.
- Events resolve rows in place: never re-sort, never scroll, never move focus — including behind an open confirmation modal.
- Print the event's `ProcessedAt` and the console's observed-at time as two separate figures.

**Ask First:**
- Any change to a banking service, a Dapr component/subscription YAML, or the message contracts.
- Any new project reference on `CoreBankDemo.DemoRunner` (package references are fine).

**Never:**
- No `ProjectReference` from DemoRunner to any banking project — declare the three event contracts as local wire records.
- No identifier matching `UseDapr` (trips the `NoDaprServiceInvocation` guard).
- No inbound HTTP listener, no `--app-port`, no ASP.NET host in the console.
- No editing `dapr/components*/subscription-transaction-events.yaml` — it is `scopes: [payments-api]`, and the console's sidecar correctly ignores it.
- No replay/back-fill of events from before the subscription started.
- No silent resolution of a contradiction between an HTTP outcome and a broadcast outcome.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Settlement | `transaction.completed` matches a tracked payment | Row → `Settled`, both clocks shown; two `balance.updated` render as aligned legs | N/A |
| Rejection | `transaction.failed` matches a tracked payment | Row → `Rejected` with full `ErrorReason`; no balance legs (producer emits none) | N/A |
| One leg only | 1 of 2 `balance.updated` arrived | Row reads `1 of 2 legs observed` and stays there | Never inferred as complete |
| Unattributed | Event's `TransactionId` matches no tracked payment | Appended to Evidence labelled `Unattributed`; no payment row touched; excluded from burst proven-leg | N/A |
| Contradiction | HTTP proved `Completed`, then `transaction.failed` for same id | Both records kept and labelled with source+time; row reads `Contradiction` | Never pick a winner |
| Feed drops | Subscription faults with payments outstanding | Every unresolved row → `Outcome unknown — the console stopped listening at <time>`; evidence strip announces once | Reconnect stamps the gap; never back-fills |
| Sidecar missing | `daprd` not on PATH / fails to start | Topology still starts; rows read `Outcome not observed — no feed` with the reason and the outcome-query remedy | Never block a payment |
| Never-listening submit | Payment submitted while no feed | Row starts at `Outcome not observed — no feed`, never `Awaiting settlement` | N/A |
| Late event | Event arrives after topology switch | Discarded via the existing `IsCurrent(OperationContext)` staleness guard | N/A |

</frozen-after-approval>

## Code Map

Investigation is preserved here; do not re-search.

**State and mutation**
- `CoreBankDemo.DemoRunner/Application/OperatorModels.cs` — `:75 PaymentOutcome` enum (extend), `:184 PaymentResult` (returned, **never stored** today), `:205 EvidenceRecord` (**no TransactionId** — add), `:85 EvidenceKind` (add an inbound-event kind), `:222 BurstProgress` (add settled/rejected/awaiting), `:281 OperatorConsoleState` + `:324 Empty` (add tracked payments + feed status), `:343 OperatorConsoleOptions` (`MaximumEvidenceRecords=500`).
- `Application/OperatorConsoleController.cs` — `:1840 Update(Func<State,State>)` is **the only mutation path**, locks `:30 _sync`, raises `:68 StateChanged`. `:1819 IsCurrent` + `:1803 CaptureContext` = staleness guard for async completions. `:1728 AddEvidence`. `:795-841` burst counters (local ints → `BurstProgress`). `:1516-1523` payment summary text. `:1365 ShutdownAsync` (add teardown). `:184 RefreshAsync` → `:223` topology-gone branch (add teardown). `:1591 DelayAsync` uses `_options.PollInterval` (zero in tests).
- `Terminal/PresentationModel.cs` — `:120-129` evidence rows, `:175-177` evidence strip, `:183` burst status, `:487 SymbolFor`.
- `Terminal/MainWindow.cs` — `:959 OnStateChanged` → `:962 RunOnUiThread` (`Application.Invoke`), `:986 PollAsync` **copy this loop shape**, `:422 BuildEvidenceView`, `:1027` burst text.

**Ports / adapters / process**
- `Application/Ports/` — established shape: interface + DTO records in one file, `sealed` adapter in `Infrastructure/`, constructor-injected into the controller **before** `TimeProvider time`.
- `Infrastructure/CommandRunner.cs:5` — run-to-completion only; **cannot host a long-lived sidecar**. `Infrastructure/OwnedProcessTerminator.cs:10` — graceful-then-kill by PID, **reusable as-is**. `Infrastructure/AspireProcessAdapter.cs:43-147` — the ownership template (pre/post PID verification, `:206 CleanupNewAppHostAsync`).
- `Infrastructure/ProfileRegistry.cs:20-25` — profile→path mapping; add `DaprComponentsDirectory`.
- `Application/Ports/IEnvironmentProbe.cs:16 IsPortFreeAsync`; `Infrastructure/EnvironmentProbe.cs:18` `IsDevProxyAvailableAsync` is the pattern for a `daprd`-on-PATH check.
- `Program.cs:54-63` — manual construction, no DI; add the adapter argument here.

**Dapr facts (verified)**
- `dapr/components*/pubsub-redis.yaml` has **no `scopes:`** → any app-id may use component `pubsub`. Only the Subscription is `scopes: [payments-api]`, which the console's sidecar parses and ignores.
- No `consumerID` metadata → Dapr derives the consumer group from app-id. Distinct app-id = fan-out, PaymentsAPI unaffected.
- Redis: 6379 (Regular) / 6381 (LoadTests), password `myredispassword123`, fixed host ports, proxy disabled.
- daprd flags (read from the toolkit assembly and `daprd --help`): `--app-id --resources-path --config --dapr-grpc-port --dapr-http-port --metrics-port --scheduler-host-address "" --placement-host-address "" --log-level`. Omit `--app-port` (streaming subscriptions are outbound-dialled).
- The console gets no toolkit env injection — set the endpoint explicitly via `DaprPublishSubscribeClientBuilder.UseGrpcEndpoint`.
- `Directory.Packages.props:19-21` — central package management on; `Dapr.Messaging` **not yet pinned** (1.18.5 verified restorable).
- `tests/CoreBankDemo.PaymentsAPI.Tests/NoDaprServiceInvocationArchitectureTests.cs` scans service-invocation signals only; pub/sub is explicitly exempt.

**Tests**
- `tests/CoreBankDemo.DemoRunner.Tests/Fakes/OperatorHarness.cs` — central fake bundle; **the new port's fake goes here** plus a property on `OperatorHarness:8`. Conventions: `Queue(...)` scripted results, `List<>` recorders, `Exception? XException`, paired `TaskCompletionSource` (`XStarted`/`ReleaseX`, `RunContinuationsAsynchronously`) for async control.
- `tests/CoreBankDemo.DemoRunner.Tests/FakeTimeProvider.cs` — fixed clock, `Advance` only, **no timer virtualization** → keep `PollInterval = TimeSpan.Zero`.
- Imitate: `Application/OperatorConsoleControllerTests.cs`, `Terminal/PresentationModelBuilderTests.cs`, `Infrastructure/AspireAdapterTests.cs`.

## Tasks & Acceptance

**Execution:**
- [x] `Directory.Packages.props` + `CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj` -- pin and reference `Dapr.Messaging` 1.18.5 -- first non-BCL/non-UI package on the console; update the csproj's standalone comment.
- [x] `docs/adr/ADR-015-presentation-safe-terminal-demo-console.md` -- amend line 24 in place -- its "never opens a Postgres, Redis, Dapr, or container-engine socket" clause and "only its UI package" sentence are now false; state what replaces them and that the zero-banking-project-reference invariant is unchanged.
- [x] `CoreBankDemo.DemoRunner/Application/Ports/IOutcomeFeed.cs` -- new port + local wire records for the three events + a feed-status result -- no banking project reference.
- [x] `CoreBankDemo.DemoRunner/Infrastructure/DaprSidecarProcess.cs` -- long-lived child-process runner -- `CommandRunner` awaits exit and cannot serve; follow `AspireProcessAdapter`'s PID-verify/cleanup pattern and reuse `OwnedProcessTerminator`.
- [x] `CoreBankDemo.DemoRunner/Infrastructure/DaprOutcomeFeed.cs` -- `IOutcomeFeed` adapter -- spawn the sidecar for the profile, `UseGrpcEndpoint` at the allocated port, streaming-subscribe to `transaction-events`, surface faults as feed-lost rather than throwing into the UI.
- [x] `CoreBankDemo.DemoRunner/Infrastructure/ProfileRegistry.cs` -- add `DaprComponentsDirectory(root, profile)` -- Regular→`dapr/components`, LoadTests→`dapr/components-loadtest`; the Redis port differs, so this must not be guessed at the call site.
- [x] `CoreBankDemo.DemoRunner/Application/OperatorModels.cs` -- add tracked-payment record and feed status to `OperatorConsoleState`; add `TransactionId` to `EvidenceRecord`; extend `BurstProgress` and `EvidenceKind` -- the console has no payment row today.
- [x] `CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs` -- track each submission, consume the feed through `Update`, resolve in place, detect contradictions, label unattributed, drive feed-lost re-labelling, tear the sidecar down in `ShutdownAsync` and the topology-gone branch -- all mutation must go through `Update` and respect `IsCurrent`.
- [x] `CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs` -- project tracked payments, inbound event rows, split burst counters, and the inline `(listening)` qualifier -- behaviour per EXPERIENCE.md, visuals per DESIGN.md `event-row`/`feed-status-inline`.
- [x] `CoreBankDemo.DemoRunner/Terminal/MainWindow.cs` -- render the new projections; start/stop the feed with the topology -- marshal via `RunOnUiThread`; never move focus or re-sort on an arriving event.
- [x] `CoreBankDemo.DemoRunner/Program.cs` -- construct and wire the adapter -- manual composition root.
- [x] `tests/CoreBankDemo.DemoRunner.Tests/Fakes/OperatorHarness.cs` -- add `FakeOutcomeFeed` with a push method and fault injection -- every other port has one here.
- [x] `tests/CoreBankDemo.DemoRunner.Tests/Application/OperatorConsoleControllerTests.cs` -- cover every I/O matrix row -- these are the honesty rules; they are the point of the feature.
- [x] `tests/CoreBankDemo.DemoRunner.Tests/Terminal/PresentationModelBuilderTests.cs` -- assert the projections -- cheapest place to pin the rendered readouts.

**Acceptance Criteria:**
- Given a running topology with the feed established, when a submitted payment's `transaction.completed` arrives, then that row resolves in its existing list position without the list re-sorting, scrolling, or focus moving.
- Given a burst in flight, when events arrive, then the HTTP leg and proven leg render as two separate labelled lines and `awaiting` reaches zero only from received events.
- Given payments outstanding, when the subscription faults, then every unresolved row reads `Outcome unknown` with the stop time and none reads `Awaiting settlement`.
- Given the console is running, when it exits or the topology stops or switches, then its `daprd` process is terminated and no orphan remains.
- Given PaymentsAPI is running, when the console subscribes, then PaymentsAPI continues to receive every event (distinct app-id ⇒ distinct consumer group).

## Verification

**Commands:**
- `dotnet tool restore && dotnet build CoreBankDemo.Rebuild.slnf` -- expected: 0 warnings, 0 errors (baseline is clean).
- `dotnet test CoreBankDemo.UnitTests.slnf` -- expected: all green, including the new DemoRunner tests.
- `dotnet list CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj reference` -- expected: zero banking implementation projects (ADR-015's machine-checkable invariant).

**Manual checks:**
- Start the Regular AppHost from the console, submit one payment, confirm the row resolves to `Settled` with both balance legs and two distinct clocks. — **not run** (needs a live topology).
- With the console attached and subscribed, confirm PaymentsAPI's inbox still receives the same events (its own subscription is unaffected). — **proven against a real broker**, see below.
- Kill the console's `daprd`, confirm outstanding rows flip to `Outcome unknown` rather than sitting at `Awaiting settlement`. — **not run** (needs a live topology); the state transition itself is covered by `FeedLost_WithdrawsEveryAwaitingClaimAndAnnouncesOnce`.

**Broker evidence (2026-09-05).** The fan-out claim is the one that could break the demonstrated
system, so it was measured rather than inferred. Two `daprd` sidecars were started against one
Redis with the checked-in component shape — app-ids `payments-api` and `demorunner-console`, the
exact flag set this feature uses — and each opened a streaming subscription to `transaction-events`:

- Both sidecars reported `204` on `/v1.0/healthz`, confirming `--scheduler-host-address ""` and
  `--placement-host-address ""` are accepted rather than rejected as empty.
- One published event reached **both** subscribers. Redis reported two separate consumer groups,
  `payments-api` and `demorunner-console`, each with `entries-read 1`, `pending 0`, `lag 0`. The
  console's subscription is additive; it diverts nothing.
- Published the way `DaprEventPublisher` publishes (raw payload plus `cloudevent.type` metadata,
  Dapr building the envelope), the delivered `TopicMessage` carried the CloudEvent type in `Type`
  and the payload JSON in `Data` — exactly what `DaprOutcomeFeed.TryParse(message.Type,
  message.Data.Span)` reads. The parse path is therefore known-good against real wire traffic, not
  only against the fake.

**Known unrelated flake.** `CoreBankDemo.PaymentsAPI.Tests`'s
`ForwardAsync_keeps_waiting_for_a_busy_lock_until_the_budget_is_spent` fails intermittently when the
full solution's test assemblies run in parallel: it asserts two lock attempts inside a real
120 ms `BudgetMilliseconds`, and on a loaded machine the first attempt can consume the budget. It
passes in isolation and on rerun. Untouched by this branch and unrelated to it.

## Suggested Review Order

**The contract the console listens on**

- Entry point: the port, the three local wire records, and the feed states — read this first.
  [`IOutcomeFeed.cs:125`](../../../CoreBankDemo.DemoRunner/Application/Ports/IOutcomeFeed.cs#L125)

- Copied rather than referenced, because ADR-015 forbids the project reference.
  [`IOutcomeFeed.cs:12`](../../../CoreBankDemo.DemoRunner/Application/Ports/IOutcomeFeed.cs#L12)

- The one event shape the controller consumes; both clocks are separate fields.
  [`IOutcomeFeed.cs:53`](../../../CoreBankDemo.DemoRunner/Application/Ports/IOutcomeFeed.cs#L53)

**Attribution — the honesty core**

- Four-way attribution: tracked, burst, retired, unattributed. The feature lives here.
  [`OperatorConsoleController.cs:1814`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L1814)

- `Retired` exists so a prior burst's late event is never called "not submitted from this console".
  [`OperatorConsoleController.cs:1888`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L1888)

- Bounded id memory backing that claim; capacity 2000.
  [`OperatorConsoleController.cs:2296`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L2296)

- Tracks every submission carrying a TransactionId, and applies events that beat their own response.
  [`OperatorConsoleController.cs:1992`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L1992)

- Reads fault levels at event time, not from the frozen subscription context.
  [`OperatorConsoleController.cs:1807`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L1807)

**Withdrawing claims when the feed stops**

- A drop re-labels rows and burst counters together; silence never becomes an outcome.
  [`OperatorConsoleController.cs:1724`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L1724)

- Bounded reconnect: three attempts, fifteen seconds apart, budget reset on success.
  [`OperatorConsoleController.cs:1649`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L1649)

- Single formatter, so the header and the evidence record cannot word the same fact differently.
  [`OutcomeFeedNarrative.cs:15`](../../../CoreBankDemo.DemoRunner/Application/OutcomeFeedNarrative.cs#L15)

**The sidecar the console owns**

- Lifecycle: allocate four ports, spawn, prove readiness, subscribe, surface faults as feed-lost.
  [`DaprOutcomeFeed.cs:94`](../../../CoreBankDemo.DemoRunner/Infrastructure/DaprOutcomeFeed.cs#L94)

- Wire-format boundary: an unknown type or malformed body is dropped, never retried.
  [`DaprOutcomeFeed.cs:290`](../../../CoreBankDemo.DemoRunner/Infrastructure/DaprOutcomeFeed.cs#L290)

- A throwing handler must not tear down the stream for every healthy payment.
  [`DaprOutcomeFeed.cs:319`](../../../CoreBankDemo.DemoRunner/Infrastructure/DaprOutcomeFeed.cs#L319)

- Retry allocates a disjoint port set; retrying the same ports loses the same race.
  [`DaprOutcomeFeed.cs:412`](../../../CoreBankDemo.DemoRunner/Infrastructure/DaprOutcomeFeed.cs#L412)

- Every port passed explicitly, no `--app-port`; readiness proven by polling, never a fixed sleep.
  [`DaprSidecarProcess.cs:130`](../../../CoreBankDemo.DemoRunner/Infrastructure/DaprSidecarProcess.cs#L130)

- Readiness is the sidecar's own `/v1.0/healthz`.
  [`DaprSidecarProcess.cs:286`](../../../CoreBankDemo.DemoRunner/Infrastructure/DaprSidecarProcess.cs#L286)

- Regular and LoadTests reach different Redis ports, so this must not be guessed at the call site.
  [`ProfileRegistry.cs:38`](../../../CoreBankDemo.DemoRunner/Infrastructure/ProfileRegistry.cs#L38)

**State and rendering**

- The row that resolves in place; carries both HTTP and broadcast verdicts so a contradiction survives.
  [`OperatorModels.cs:298`](../../../CoreBankDemo.DemoRunner/Application/OperatorModels.cs#L298)

- Split HTTP leg and proven leg; `Awaiting` is computed and can no longer be clamped negative.
  [`OperatorModels.cs:331`](../../../CoreBankDemo.DemoRunner/Application/OperatorModels.cs#L331)

- Row projection: two clocks, aligned legs, the inline `(listening)` qualifier.
  [`PresentationModel.cs:274`](../../../CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs#L274)

- Feed status wording, pluralized through the shared narrative.
  [`PresentationModel.cs:253`](../../../CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs#L253)

- Preserves scroll offset as well as selection, so an arriving event never moves the list.
  [`MainWindow.cs:1725`](../../../CoreBankDemo.DemoRunner/Terminal/MainWindow.cs#L1725)

- Manual composition root; the adapter is constructed here.
  [`Program.cs:57`](../../../CoreBankDemo.DemoRunner/Program.cs#L57)

**Supporting**

- One test per I/O-matrix row plus the acceptance criteria.
  [`OperatorConsoleControllerTests.cs:1341`](../../../tests/CoreBankDemo.DemoRunner.Tests/Application/OperatorConsoleControllerTests.cs#L1341)

- Parse and start-failure coverage for the real adapter, previously untested.
  [`DaprOutcomeFeedTests.cs:1`](../../../tests/CoreBankDemo.DemoRunner.Tests/Infrastructure/DaprOutcomeFeedTests.cs#L1)

- Ties the three type constants, topic, and component name to the checked-in manifests.
  [`DaprComponentsProfileTests.cs:1`](../../../tests/CoreBankDemo.DemoRunner.Tests/Infrastructure/DaprComponentsProfileTests.cs#L1)

- ADR-015's "only its UI package" and "never opens a Dapr socket" clauses replaced.
  [`ADR-015:21`](../../../docs/adr/ADR-015-presentation-safe-terminal-demo-console.md#L21)
