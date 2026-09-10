# Addendum — Resource lifecycle and trustworthy recovery reporting

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
