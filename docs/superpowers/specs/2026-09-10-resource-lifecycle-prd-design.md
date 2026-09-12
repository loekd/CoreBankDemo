# PRD: Resource lifecycle and trustworthy recovery reporting

> **Status:** Reference
> **Kind:** PRD
> **Original date:** 2026-09-10
> **Migrated from:** `docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10-resource-lifecycle/prd.md` on 2026-09-10
> **Related:** PR #17

## 1. Context

DemoRunner exists to make CoreBankDemo's resilience patterns visible to an audience. Its highest-value story is the **outbox**: PaymentsAPI accepts a payment and answers successfully while CoreBankAPI is down, parks the work durably, and drains it the moment the bank returns. Nothing is lost, nothing is double-spent, no operator intervention is required.

Today that demo cannot be given from the console. Stopping CoreBankAPI produces two failures at once:

1. **Every resource button goes dead.** The operator cannot start the service back up from the console; the hint line advises stopping and restarting the whole topology.
2. **Settled payments keep reading "Awaiting settlement."** Even after the bank is restarted by other means and has settled every parked payment, the rows never resolve.

Both failures trace to the same design decision, applied in eight places across two code paths: the console identifies "the system I am looking at" by the **shape** of the topology — which resources exist, their replica counts, their endpoints — and treats any deviation from the shape it expects as evidence that it is looking at the wrong system. Stopping a resource is a deviation. So the console responds to the operator's own Stop button by refusing to act on resources and refusing to believe what the bank tells it.

The system under demonstration is not at fault. It behaves correctly and recovers fast.

### 1.1 What the evidence shows

A live session was left running and inspected directly. Five payments were submitted during a CoreBankAPI outage between 13:33:28 and 13:34:06. Both `corebank-api` replicas were restarted at 13:38:14, from outside the console.

| Source of truth | What it recorded |
| --- | --- |
| PaymentsAPI `OutboxMessages` | All five parked, then `ProcessedAt` 13:38:16, `RetryCount` **0** |
| CoreBank `InboxMessages` | All five received 13:38:16, `Status` **Completed**, `RetryCount` **0** |
| CoreBank `MessagingOutboxMessages` | All five `transaction.completed` broadcasts published, `Status` **Completed** |
| Redis `transaction-events` stream | All five events present, well-formed, carrying the correct `transactionId` |

The backlog drained in **under two seconds, on the first attempt**. Across both databases there is no row in any state other than `Completed` or `Cancelled` — no stuck work anywhere in the system.

**Why the console's silence traces to the shape check specifically.** The symptom — rows stuck on "Awaiting settlement" — is also what a dropped-and-unreported subscription would produce, because the feed's own status handler carries the same shape check and would swallow the "Lost" notice (see `addendum.md` §A). The symptom alone does not discriminate. The broker does:

> Redis consumer group `demorunner-console` reports `pending: 0` and a `last-delivered-id` **identical to `payments-api`'s and current**.

A subscription that died during the outage and never recovered would leave that id frozen at the moment it died. It did not freeze. The console's sidecar consumed the stream continuously, received all five broadcasts, and acknowledged them. The events arrived and were discarded above the transport. That is the claim this PRD rests on.

### 1.2 The control that cannot say its own name

The button that triggers all this is labelled **"Resource action"** — naming neither the action nor its target, though the row beneath it already renders the real action as `[Stop]` or `[Start]`. It knows what it is about to do and declines to say so. That is the cosmetic half of this PRD. The half that matters: the button is disabled exactly when the operator needs it.

## 2. Goals

- **G1.** After stopping a resource from the console, the operator can start it again from the console — same place, same selection, no topology restart.
- **G2.** A payment that settles while the operator watches reaches a terminal state on screen, unaided, across any resource stop/start performed within a running topology.
- **G3.** Resource controls name the action and the resource they act on.
- **G4.** An operator who does nothing but press buttons in the console can run the outbox demo end to end.

## 3. Non-goals

- **NG1.** Changing any banking service. No endpoint, message, schema or configuration in PaymentsAPI or CoreBankAPI is in scope.
- **NG2.** A manual reconcile action for still-open rows. Declined for now: the outstanding gaps it would cover (FR11, the reconnect attempt cap, a null feed context) are narrow and separately visible, and the existing outcome query already covers genuine ambiguity. Revisit if F2 ships and rows still strand.
- **NG3.** A staged or animated "recovery moment". A sub-two-second drain of a visible backlog is dramatic on its own; the console's job is to stop hiding it.
- **NG4.** Recovering rows already stranded in a running session. Those events are acknowledged and gone. The fix prevents the loss; it does not resurrect it.
- **NG5.** The `redis` resource naming problem (§9). Real, out of scope here.

## 4. The demo this must support

One operator, presenting to an audience, driving the console alone.

1. Start the Regular topology — from the console, or `aspire run` it and attach.
2. Submit a payment. It settles immediately. The audience sees the happy path.
3. Select `corebank-api` in Resources and stop it. Confirm. The bank goes down and the audience watches it go.
4. Submit several payments. Every one is **accepted** — PaymentsAPI answers successfully with the bank dead. The rows sit visibly open. *This is the claim the audience is invited to disbelieve.*
5. Return to Resources. The same control, same place, same selection, now offers **Start**. Press it. **(Blocked today: the control is disabled — F1.)**
6. Within seconds every open row resolves to settled, unaided, each with its inbound broadcast in Evidence. **This is the payoff. (Blocked today: the events are discarded — F2.)**

## 5. Features and requirements

### F1. Recover a stopped resource from the console

A resource the operator stopped on purpose leaves the topology in an expected state, not a corrupt graph. The console must keep operating on resources while the topology's shape reflects the operator's own commands.

- **FR1.** With a resource stopped by an operator command, the console MUST keep offering lifecycle actions on the resources of that topology — including starting the stopped resource.
- **FR2.** The console MUST NOT direct the operator to stop and restart the whole topology as a remedy for a resource it stopped itself.
- **FR3.** The console MUST still refuse resource commands when the topology is genuinely unusable — unreachable, or its snapshot stale beyond the freshness bound. Those refusals keep their current explanations.
- **FR4.** Resource commands MUST remain confined to the existing allow-list, and MUST continue to fan out across every replica instance of the selected resource.
- **FR5.** When the operator starts a resource, the console MUST report the outcome of each instance it acted on, as it does today.

### F2. Outcome reporting survives a resource lifecycle change

- **FR6.** While a topology run is active, the console MUST continue to attribute incoming transaction events to their tracked payments across any number of individual resource stops, starts and restarts.
- **FR7.** The feed's own status changes — in particular a lost subscription — MUST reach the operator under the same conditions as FR6. A status the console suppresses is worse than one it never received, because rows keep asserting something the console no longer knows to be true.
- **FR8.** A dropped subscription MUST remain eligible for re-establishment while its topology is running, regardless of what the operator has done to individual resources.
- **FR9.** The console MUST still reject events belonging to a different topology **profile**, and MUST still start clean after a topology stop or switch.
- **FR10.** Identifying a run by profile and generation does **not** cover an AppHost relaunched **externally** while the console is attached: both survive the relaunch, so a new run's events may attribute to rows from the prior run. **This limitation is accepted deliberately** rather than solved with a new detection mechanism — it is reachable only by relaunching an AppHost outside the console mid-session, which no demo does. It is recorded in C1 and MUST NOT be quietly dropped from the counter-metric.
- **FR11.** Every event the console receives and can read MUST reach the Evidence log, subject to its existing retention bound. An event dropped for any other reason MUST NOT vanish silently.
- **FR12.** A payment whose settlement broadcast arrives while its row is open MUST reach its terminal state without operator action, with the row held in place — no re-sort, no scroll jump, no change of selection.
- **FR13.** Rows already marked with an unknown outcome because the feed genuinely dropped MUST remain so. A recovered subscription is not retroactive evidence.

### F3. Named resource lifecycle controls

- **FR14.** The resource action control MUST name both the action and the resource it will act on, using the resource identifier as the resource list shows it.
- **FR15.** The offered action MUST track the selected resource's condition: stopped offers **Start**, healthy or running offers **Stop**, failed or degraded offers **Restart**.
- **FR16.** The offered action MUST update as the operator's selection moves, not at press time. *(New behaviour: nothing currently observes selection changes on the resource list.)*
- **FR17.** When the selected resource has no legal action, the control MUST be disabled **on that basis**, and the hint line MUST say why. *(Today enablement asks whether* any *resource can be mutated, so an illegal action fails on press instead.)*
- **FR18.** The restart control MUST name its target on the same basis and remain available for bouncing a healthy resource.
- **FR19.** The two controls MUST NOT present identical captions simultaneously. *(For a failed resource both would otherwise read "Restart …".)*
- **FR20.** Stop and Restart MUST keep the existing confirmation, which continues to display the exact per-instance `aspire` commands and the affected instances. `[ASSUMPTION]` Start proceeds without confirmation: it destroys nothing and is the demo beat that must land cleanly.
- **FR21.** Captions MUST fit the action column. That column is widened from 22 to **28 characters** to accommodate the longest allow-listed case, `Restart loadtest-support`. The resource list beside it cedes the six columns.

### F4. A smaller action row

- **FR22.** The **Switch topology** button MUST be removed from the Resources action row.
- **FR23.** Changing profile is thereafter **Stop AppHost** followed by **Start Regular** / **Start LoadTests**, all already present in the same column.

## 6. Cross-cutting requirements

- **NFR1.** No banking service gains an endpoint, subscription, message type or configuration entry.
- **NFR2.** The console adds no listening port and no new owned process.
- **NFR3.** Captions must fit the action column at its committed width of 28, with the longest allow-listed resource identifier selected, and the narrowed resource list must stay readable at the narrowest supported terminal width. Removing Switch (FR22) frees a **row**, not characters — the widening in FR21 is what creates the room.
- **NFR4.** Captions must survive a monochrome terminal and a projector: the words carry the meaning, colour only reinforces.
- **NFR5.** Unit tests must cover the recovery path end to end: stop a resource, prove the action control still offers Start, prove an event arriving afterwards resolves its row.
- **NFR6.** The shape check is consulted at **19 call sites**, most of them unrelated to this PRD. Relaxations MUST be made per call site with that site's purpose understood, never as a blanket change to the shared predicate.

## 7. Success metrics

- **M1.** Stop CoreBankAPI from the console, submit *N* payments, start it again **from the console**: all *N* rows reach a terminal state with no further operator action. Currently unreachable — step 5 of §4 cannot be performed.
- **M2.** The demo in §4 runs end to end without the presenter explaining away anything on screen.
- **M3.** Time from pressing Start to the last row resolving stays within a few seconds of the measured drain time.

**Counter-metrics** — these must not move:

- **C1.** No event from a different topology run or profile is attributed to a current row — **except** in Attached mode following an external AppHost relaunch, where profile and generation both survive and events may attribute to rows from the prior run. That exception is accepted (FR10) and must be stated, not silently absorbed.
- **C2.** No increase in payments reported with a proven-but-wrong outcome.
- **C3.** No resource command reaches a resource outside the allow-list.
- **C4.** No refusal that protects the operator from a genuinely unusable topology is lost (FR3).

## 8. Assumptions and accepted limitations

| Item | Disposition |
| --- | --- |
| `[ASSUMPTION]` Start needs no confirmation; Stop and Restart keep theirs (FR20) | Flag if you want symmetry |
| Both controls name their resource; action column widened 22 → 28 to fit | Decided — FR14, FR21 |
| External relaunch in attached mode may misattribute events | Accepted deliberately — FR10, C1 |
| `[NOTE]` Rows stranded in the current live session stay stranded (NG4) | Restart the console before the next demo |

## 9. Follow-ups

- **The `redis` button will name the wrong container.** `dapr/components/pubsub-redis.yaml` points at `localhost:6379`, which is the `dapr init` Redis holding all pub/sub traffic; Aspire's own `redis` resource is a separate, empty container. Once buttons name their targets (F3), a control reading `Stop redis` stops the container that carries none of the traffic. Out of scope (NG5), but it undercuts F3's premise and deserves its own ticket.
- **Verification owed.** On a live console, Evidence should show no `transaction.completed` rows during the recovery window and should show them for payments submitted after the shape settles. Confirms the diagnosis; does not gate it, given §1.1.

## 10. Downstream

Consumed by the architecture workflow and the epics and stories workflow. Mechanism analysis, verified code references, caller inventory and rejected alternatives are in `addendum.md`.

The single largest implementation risk is NFR6: of the shape check's call sites, only the ones named in `addendum.md` §A.2 are in scope. A blanket change to the shared predicate would be the easy fix and the wrong one.


## Addendum

## Addendum — Resource lifecycle and trustworthy recovery reporting

Technical depth behind `prd.md`. Every code reference below was verified against the working tree at `origin/main` `d1a8a35`.

## A. The shape check, and where it fires

### A.1 What the check is

`Application/AspireJsonParser.cs:45` builds a fingerprint string per snapshot:

```csharp
var fingerprint = string.Join(
    ",",
    resources.Select(resource =>
        $"{resource.Name}:{resource.ReplicaCount}:{string.Join("|", resource.Endpoints.Order(StringComparer.Ordinal))}"));
```

and a separate boolean `IsFingerprintMatch`, true only when every required resource is present, every replica count matches `KnownResources.ExpectedReplicaCount`, and every expected endpoint port is present.

Two distinct things, derived from topology shape and **both** broken by stopping a resource:

- `IsFingerprintMatch` goes **false** — `corebank-api` either drops out of the graph (counted as *missing*) or reports a replica count below the expected 2, and its port-5032 endpoint disappears. Note `ReplicaCount` is floored at 1 (`AspireJsonParser.cs:105`, `Math.Max(1, matches.Count)`), so it never reads 0; the endpoint list and the missing-resource path are what actually move.
- The fingerprint **string** changes, so equality against a previously captured one fails.

### A.2 Where each one is consulted

**Command path — gated on `IsFingerprintMatch`. This is what disables the buttons.**

| Location | Effect while a resource is stopped |
| --- | --- |
| `Terminal/PresentationModel.cs:1261` `CanMutateResource` | `CanMutate` false for every row |
| `Terminal/MainWindow.cs:1554-1555` | Both resource buttons disabled |
| `Application/OperatorConsoleController.cs:3182` `HasFreshResourceAuthority` | Controller-level refusal |
| `Infrastructure/AspireCliAdapter.cs:151` | Adapter rejects: *"not present in the verified graph"* |
| `Terminal/PresentationModel.cs:1220` | Hint reads *"The running graph no longer matches the known profile — Stop and Start it again."* |

Start-after-Stop is therefore unreachable from the console, and the hint actively misdirects: it proposes a topology restart as the remedy for a state the operator created deliberately.

**Feed path — gated on the fingerprint string, via `IsCurrent`.**

`OperatorConsoleController.cs:3573`:

```csharp
private bool IsCurrent(OperationContext context)
{
    var state = State;
    return state.Profile == context.Profile
        && state.RunGeneration == context.RunGeneration
        && string.Equals(state.Topology?.Fingerprint ?? string.Empty, context.Fingerprint, StringComparison.Ordinal);
}
```

Three feed-path callers carry it, not one:

1. **`OnOutcomeEventReceived:2352`.** The guard returns *before* both `AttributeEvent` and `AddEvidence`, so a discarded event resolves no row **and leaves no trace** — which is why the failure presents as silence rather than error.
2. **`OnFeedStatusChanged:2262`.** Same guard. Its own doc comment promises that *"the words 'Awaiting settlement' leave the screen at the instant they stop being true"* — but a **Lost** status arriving while the shape is mismatched is swallowed, so the rows keep asserting exactly that. **This is why the observed symptom cannot by itself distinguish "events discarded" from "feed died unreported".**
3. **`TryReestablishOutcomeFeedAsync:2192`.** Same guard, so a dropped subscription cannot be re-established while a resource is stopped. With `MaximumFeedReconnectAttempts = 3`, a feed lost in that window may never return.

### A.3 What discriminates the hypotheses

Redis, not the screen. Consumer group `demorunner-console` reports `pending: 0` and a `last-delivered-id` that is current and identical to `payments-api`'s. A subscription that died mid-outage and never recovered would leave that id frozen where it died. It kept consuming, so the subscription was alive and the events were discarded above the transport, at `OnOutcomeEventReceived`.

`OnFeedStatusChanged` and `TryReestablishOutcomeFeedAsync` remain in scope regardless — they carry the same latent defect, and the former would mask the very failure mode that makes `OnOutcomeEventReceived` hard to diagnose.

### A.4 Timing

- `OperatorConsoleOptions.PollInterval` = **1 second** (`Application/OperatorModels.cs:705`).
- `TopologyObservationDebouncer.Observe` requires **two consecutive agreeing snapshots** before accepting a change — but **short-circuits when `!observed.IsFingerprintMatch`** (`TopologyObservationDebouncer.cs:9-13`), returning the mismatched snapshot immediately.

So the mismatch registers fast, and the two-snapshot lag applies on the way **back** to a matching shape. In the captured session CoreBank restarted at 13:38:14–15 and published all five broadcasts between 13:38:16.63 and 13:38:17.52 — inside that trailing window, while `state.Topology` still carried a mismatched fingerprint. The outbox drains faster than the console's own debounce settles.

### A.5 Where `RunGeneration` actually moves

Only `ActivateTopology:3294`, reachable from Start, Attach and Switch. **`StopAsync` never touches it** (`:483`, state update at `:520-534`); a stop is safe from stale-event attribution for a different reason — `StopOutcomeFeedAsync:2231` sets `_feedContext = null`, and the guard's `context is null` branch then rejects everything.

This is precisely why PRD FR10 records an accepted limitation: an AppHost relaunched **externally** while the console is attached moves neither profile nor generation, so generation alone cannot distinguish it from the run it replaced.

## B. Measurements from the captured session

AppHost PID 927525, Regular profile, started 13:23:14.

| TransactionId | CreatedAt | ProcessedAt | Retries |
| --- | --- | --- | --- |
| `7ab2ed3d…` | 13:33:28.36 | 13:38:16.57 | 0 |
| `8019bf2d…` | 13:34:04.17 | 13:38:16.58 | 0 |
| `fdc50cab…` | 13:34:05.27 | 13:38:16.80 | 0 |
| `f4e1f293…` | 13:34:05.80 | 13:38:16.81 | 0 |
| `3bf5c2d4…` | 13:34:06.41 | 13:38:16.96 | 0 |

CoreBank `InboxMessages`: all five `Completed`, `RetryCount 0`, `LastError` empty. CoreBank `MessagingOutboxMessages`: all five broadcasts `Completed`. No row anywhere in either database in any other state. A representative discarded event:

```json
{"type":"com.corebank.transaction.completed","subject":"7ab2ed3d-…",
 "data":{"transactionId":"7ab2ed3d-…","status":"Completed",
         "processedAt":"2026-09-10T13:38:16.635198+00:00"}}
```

### Environment note

Two Redis containers are present: Aspire's `redis-495bfe3c` (no published host port, `DBSIZE 0`) and `dapr_redis` from `dapr init` (publishes `0.0.0.0:6379`, holds the 1,147-entry stream). `dapr/components/pubsub-redis.yaml` targets `localhost:6379`, so **all pub/sub traffic flows through the `dapr init` Redis**. Anyone inspecting the broker will otherwise open the wrong container — as happened during this investigation. This is also the basis of the follow-up in PRD §9.

## C. Options considered for the feed-path identity check

**Chosen — profile + run generation, fingerprint string dropped from this check.**
No timing window, because nothing in the check derives from a debounced observation. **Known gap, accepted:** an externally relaunched AppHost in attached mode moves neither profile nor generation, so its events can be attributed to prior-run rows (PRD FR10, C1). Detecting it by AppHost process identity was considered and set aside as unnecessary machinery for a state no demo reaches.

**Rejected — re-capture `_feedContext` on fingerprint drift.**
The refresh can only fire when `state.Topology` updates, which is the debounced path in §A.4. The hole stays open across the recovery burst — the one moment that matters. A check that continuously redefines what it checks against is also hard to state as a safety property.

**Not viable — buffer and replay discarded events.**
`_unmatchedTerminalEvents:2111` sits *downstream* of the guard and is cleared on correlation reset. Routing discarded events into it would mean retaining events the console has just declared untrustworthy.

## D. Surface being changed

**`Terminal/MainWindow.cs`**

- `:224` `_switchButton = NewButton("Switch topology")` — removed, with its handler `:743` and enablement `:1553`.
- `:225` `_resourceActionButton = NewButton("Resource action")` — caption becomes selection-derived.
- `:226` `_restartResourceButton = NewButton("Restart selected")` — likewise.
- `:723` `StackButtons(actions, 5, …)` — loses one entry.
- `:1554-1555` — **does need to change.** Enablement is `model.Resources.Any(row => row.CanMutate)`: *any* row, not the selected one. Today an illegal action fails on press at `:2313-2317`; FR17 requires it to be disabled on the selection's own basis.
- **No `SelectedItemChanged` subscription exists on `_resourceList`.** Every `SelectedItem` read is on-demand at press time (`:768`, `:2311`); `:2415` is a test helper. FR16 needs new plumbing — this is not a caption-only change.
- `ActionColumnWidth = 22` (`:36`), and `StackButtons:1402` lays buttons out **vertically** (`Y = startY + index`, `Width = Dim.Fill()`). Removing Switch frees a row, not a character. **Resolved: widen to 28** (PRD FR21) to fit `Restart loadtest-support`, the longest allow-listed case. `_resourceList.Width = Dim.Fill(ActionColumnWidth + 2)` (`:709`) and the actions frame at `:716-718` both derive from the constant, so the list narrows with it.

**`Terminal/PresentationModel.cs`**

- `:1289` `NextAction(ResourceCondition)` already yields `Start` / `Stop` / `Restart` / `Unavailable`, resolved per row at `:222` and rendered as `[{row.NextAction}]` at `MainWindow.cs:1508`. Note `:230` downgrades it to `"Unavailable"` when the resource does not support the command. **The state machine the captions need already exists and is correct** — but FR16 and FR17 still require real behavioural work.
- `:1261` `CanMutateResource`, `:1220` hint text — both in scope for F1.

**`Application/OperatorConsoleController.cs`**

- `:3573` `IsCurrent` — **20 occurrences, 19 call sites**, of which 3 are the feed path (§A.2) and 16 are not. Several use it to decide whether a *mutation result* still applies, where shape sensitivity is deliberate. **Do not blanket-change the predicate.** Introduce a separate, explicitly named check for event attribution and leave mutation-staleness callers alone. This is the main implementation risk (PRD NFR6).
- `:3182` `HasFreshResourceAuthority` — in scope for F1.
- `:2262`, `:2192` — feed-path callers beyond the obvious one.

**`Infrastructure/AspireCliAdapter.cs:151`** — in scope for F1.

**Retention.** `MaximumEvidenceRecords = 500` (`OperatorModels.cs:692`), ring-buffered at `:3500`. FR11 is bounded by this deliberately; "every event" without that bound would be unsatisfiable against a 1,147-entry stream.

## E. Naming decision

Buttons use **raw resource identifiers** (`Stop corebank-api`), not curated display names. Rationale: the identifier matches the resource-list row the operator just selected, and the confirmation dialog's **title** already reads `Stop corebank-api` (`MainWindow.cs:2321`). Note the dialog's *command* lines are per-instance — `ExactCommands` (`:2291-2294`) emits one `aspire resource corebank-api-<suffix> stop` per replica — so the identifier matches the title and the row, not the commands verbatim.

There is no `KnownResources.Abbreviate`; the abbreviation helper is `PresentationModel.Abbreviate` (`PresentationModel.cs:1307`, private) and serves a different purpose; it is not extended here.

The resulting width conflict is resolved by widening the action column rather than by shortening the names (PRD FR21).
