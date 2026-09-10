# Adversarial review — PRD: Resource lifecycle and trustworthy recovery reporting

Reviewed: `prd.md`, `addendum.md` (both dated 2026-09-10).
Method: every claim about the console was checked against the source it cites. Code quotes below
are from the working tree at review time.

**Verdict: do not proceed to architecture as written.** The document is well-argued and its
diagnosis is *plausible*, but the diagnosis is not proven, the proposed fix demonstrably weakens a
safety property the PRD claims it preserves, and the F2/F3 half rests on three statements about
the existing UI code that are simply false. The F1 half needs one more measurement before it is
worth building. The F2/F3 half needs its "this is a presentation change, not a behavioural one"
claim withdrawn.

---

## 1. Is the root cause proven, or merely consistent with the evidence?

**Merely consistent.** Every artefact in §1.1's table is *upstream* of the console's handler. The
table proves the banking system worked and that the console's Dapr sidecar acknowledged the stream
entries. It contains nothing at all from inside the DemoRunner process. The PRD nevertheless writes
as though the case is closed:

> "The console received the proof and discarded it." (§1.1)
> "Not a race that is usually won; a window the recovery traffic reliably lands in" (addendum §A)

### F-1 (blocker) — The PRD's own verification proposal cannot discriminate between the hypotheses

§8 concedes the diagnosis is unverified:

> "Verification still owed | On the live console, Evidence should show **no** `transaction.completed`
> rows around 13:38:16 and **should** show them from 13:38:55 onward. That confirms the diagnosis
> end to end."

It does not confirm anything. An empty Evidence log around 13:38:16 is produced *identically* by at
least four different faults. The observation has no discriminating power, and §8 asserts that it
does. Meanwhile §1.1 has already dropped the hedging and states the conclusion flatly, and §9 hands
the unverified conclusion to `bmad-architecture`. That gap — "verification still owed" in the table,
"it is fully explained" in the prose — is the single most self-deceiving thing in the document.

### F-2 (blocker) — Alternative A: the feed went Lost, and the Lost status was swallowed too

`OperatorConsoleController.cs:2264` — `OnFeedStatusChanged` opens with the *same* guard as the
handler the PRD indicts:

```csharp
private void OnFeedStatusChanged(OutcomeFeedStatus status)
{
    var context = _feedContext;
    if (context is null || !IsCurrent(context))
    {
        return;
    }
```

So during a fingerprint mismatch the console also discards *feed status transitions*. If the
subscription actually dropped during the outage, the `Lost` status was binned, the rows never
flipped to `Outcome unknown`, and the screen reads **"Awaiting settlement" forever** — the exact
reported symptom, with no event ever reaching `OnOutcomeEventReceived` at all.

Worse, the recovery path is gated the same way. `TryReestablishOutcomeFeedAsync` (`:2192`):

```csharp
if (state.Feed.State != OutcomeFeedState.Lost
    || state.Profile == TopologyProfile.None
    || !IsCurrent(context))
{
    return;
}
```

Reconnection is therefore *also* blocked by the fingerprint mismatch — and it is the only place
besides activation that re-captures `_feedContext` (via `StartOutcomeFeedAsync(..., resetCorrelation: false)`).

This matters because it **changes the fix**. Under the PRD's hypothesis, dropping the fingerprint
clause is sufficient. Under this one, the events never arrived and you additionally need the
reconnect path to work — and `MaximumFeedReconnectAttempts = 3` at `FeedReconnectInterval` of 15s
means a subscription that stayed down for the ~5 minutes of this outage has *permanently* given up,
which no change to `IsCurrent` repairs. The addendum's §D caller inventory (`:251`, `:1052`,
`:1504`, `:1553`, `:1659`, `:2039`, `:2597`, `:2642`) **omits both `:2192` and `:2265`** — the two
feed-path callers most likely to change the shape of the fix.

Redis `pending: 0` does not rule this out. It shows the *sidecar* acked; it says nothing about
whether the DemoRunner process's subscription endpoint received the five POSTs.

### F-3 (major) — Alternative B: `_feedContext` was null, not stale

Both guards fail identically on `context is null`. `StopOutcomeFeedAsync` (`:2229`) sets
`_feedContext = null` and clears `_unmatchedTerminalEvents`, `_burstTransactions` and
`_retiredTransactions`. It is called from the refresh path's "AppHost disappeared" branch (`:301`),
which fires when a poll observes `!observed.IsReachable` and discovery then reports no snapshot for
the profile. With both `corebank-api` replicas down for five minutes, whether the AppHost's
`aspire describe`/`aspire ps` stayed reachable throughout is *not recorded anywhere in the
evidence*. If it blipped, the console tore its own state down, `_feedContext` went null, and the
generation-based fix does nothing — because the null check precedes the `IsCurrent` call.

### F-4 (major) — Alternative C: attribution failed downstream, not at the guard

The PRD's own C2 says the console "refus[es] to silently pick a winner between HTTP and broadcast",
and the git history immediately preceding this PRD is `feat(corebank): broadcast
transaction.cancelled so a residual 202 resolves`. Payments submitted with the bank down get a 202.
Whether a `transaction.completed` broadcast alone is sufficient to move a residual-202 row to
terminal is a question about `AttributeEvent`, which the PRD never opens. If it is not sufficient,
the rows stay "Awaiting settlement" *even with the guard passing*, and F1 ships without fixing the
demo.

### F-5 (major) — The one causal claim in §1.1 is not in the evidence at all

> "The rule fires on the one thing that is definitively not a different system: **the operator
> pressing the console's own Stop button.**"

§1.1 says only "Both `corebank-api` replicas were restarted at 13:38:14" — passive, agent unnamed.
Nothing in §1.1 or addendum §B records **when the stop happened, or that it was issued from the
console** rather than from a terminal or by a crash. The stop timestamp is missing entirely, so the
reader cannot even check that the five payments (13:33:28–13:34:06) fall inside the outage window.
The PRD's rhetorical centre of gravity rests on an unrecorded fact.

Likewise §8's "should show them from 13:38:55 onward" — **13:38:55 appears nowhere else in either
document.** There is no later payment in §1.1's narrative, none in addendum §B's table. A dangling
timestamp in the one row that is supposed to settle the diagnosis.

### F-6 (minor) — The debounce argument is overstated

Addendum §A: "`state.Topology.Fingerprint` trails physical reality by roughly two seconds minimum."
`TopologyObservationDebouncer.Observe` short-circuits first:

```csharp
if (!observed.IsReachable || !observed.IsFingerprintMatch)
{
    _candidateSignature = null;
    return observed;
}
```

With `corebank-api` at 0 replicas, `replicaMismatches` is non-empty (`AspireJsonParser.cs:29`,
`ExpectedReplicaCount` is 2 for `corebank-api`), so `IsFingerprintMatch` is false and the snapshot
passes through **undebounced**. The debounce only applies re-entering a matching state. Separately,
the debouncer's `Signature` covers `Name:Condition:Health:ReplicaCount` — *not* endpoints — while
the fingerprint does include endpoints, so endpoint-only drift bypasses the debounce entirely. The
argument survives, but "reliably lands in" is asserted from a single sample.

### What would actually discriminate

Cheap, and none of it was collected:

1. A counter or log line at the `return` in `OnOutcomeEventReceived` (`:2357`) — how many events
   were discarded, and with what `context.Fingerprint` vs `state.Topology.Fingerprint`.
2. The DemoRunner sidecar's app-delivery log for 13:38:16–17: did five POSTs reach the process?
3. `state.Feed.State` and the Resources status line as they read on screen at 13:38:20. `Listening`
   kills F-2; `Lost` or `NotStarted` kills the PRD's hypothesis as the *sole* cause.
4. Whether `state.Profile` was ever `None` between 13:34 and 13:38 (kills or confirms F-3).

**Recommendation:** run (1)–(3) before architecture. They are one instrumented session.

---

## 2. Does profile + run generation create new failure modes?

Yes — and FR2's central claim is false.

### F-7 (blocker) — FR2's "not weakened" is provably wrong in attached mode

> **FR2.** "The console MUST still reject events that belong to a different topology profile, or to
> a different topology run than the one on screen. The safety property this check exists for is
> preserved, not weakened."

`RunGeneration` moves only in `ActivateTopology` (`:3287`), which is reached from `StartAsync`
(`:414`), `AttachAsync` (`:471`) and `SwitchAsync` (`:625`) — i.e. **only from console-initiated
transitions.** The fingerprint clause is today the *only* thing that catches a topology run the
console did not initiate. Concretely:

- Console is **Attached** (`AttachAsync`, `:471`) to an AppHost the operator started in their own
  terminal. Operator Ctrl-Cs that AppHost and runs `aspire run` again. Same profile. Generation
  unchanged. With the fingerprint clause gone, events from process #2 are attributed to rows
  tracked against process #1. Today the endpoint list in the fingerprint catches this.
- The refresh path only tears down when discovery finds **no** snapshot for the profile:

  ```csharp
  if (discovered.IsReachable
      && discovered.Snapshots.All(snapshot => snapshot.Profile != refreshContext.Profile))
  ```

  A relaunch that completes between two 1-second polls, or overlaps the old process, never trips
  it. **C1 ("A relaunch must still start clean") is not delivered by the proposed design.**

This also means the fix's blast radius on the *other* `IsCurrent` callers is understated. Addendum
§D already flags "Do not blanket-change the predicate" and calls it "the main implementation risk",
but the PRD body carries none of that — §9 hands architecture a requirement (FR2) whose stated
safety guarantee the chosen mechanism cannot meet.

**Ask:** either add an AppHost-process-identity term to the event-attribution check (PID, start
time, or dashboard URL — something stable across a resource bounce and unstable across a relaunch),
or amend FR2 to say honestly *which* safety property is being traded away.

### F-8 (major) — Crashed-and-auto-restarted resources are silently in scope

G1 scopes to "any resource stop/start **the operator performs**". The mechanism has no such
scoping: dropping the fingerprint clause makes the console equally blind to a replica that crashed
and was restarted by Aspire, or one the operator restarted via the Aspire dashboard. That is
arguably *desirable* — but it is a behaviour change nobody has been asked to approve, and it
removes the only signal the console had that its picture of the topology went stale for a reason
it did not cause. G1's "the operator performs" is doing work the design cannot honour.

### F-9 (major) — Replica scaling and genuine replacement are not addressed

`ExpectedReplicaCount` is 2 for `payments-api` and `corebank-api` (`KnownOperatorSurface.cs:64`).
`BuildResource` returns `null` when a resource has no matching candidates, dropping it from the
snapshot entirely. Neither the PRD nor the addendum says what should happen when:

- a resource comes back with a *different* replica count (scaled 2→1);
- a resource is genuinely replaced — new image, new port — mid-run.

Both are indistinguishable from a bounce under the new check. Today they are caught. Not
necessarily wrong, but undocumented and unrequirement-ed.

### F-10 (minor) — The "no timing window at all" claim needs a caveat

Addendum §C: "No timing window at all, because nothing in the check is derived from a debounced
observation." True of `IsCurrent`. Not true of the surrounding flow: `_feedContext` is still
captured *before* `_outcomeFeed.StartAsync` (`:2152`), and `ActivateTopology` still calls
`_debouncer.Reset()` (`:3289`). The window moves; it does not vanish.

---

## 3. Are the requirements testable as written?

Several are not verifiable without reading the implementation.

### F-11 (major) — FR2 is circular

"a different topology run than the one on screen" is defined only by an implementation parenthesis:
"*(A topology start, stop or switch begins a new run; a resource command does not.)*". A tester who
does not already know `RunGeneration` exists cannot distinguish FR2 from FR1. The definition should
be stated in operator-observable terms (the generation number is already rendered in the status
line at `:354`, `:3311` — use it), or FR2 should name it.

### F-12 (major) — FR3 is unsatisfiable and collides with an existing cap

> **FR3.** "Every event the console receives and can read MUST be recorded in Evidence... An event
> dropped for any reason MUST NOT vanish silently."

`OperatorConsoleOptions.MaximumEvidenceRecords` is **500** (`OperatorModels.cs:692`). The captured
session's stream held 1,147 entries. Under FR3 the console files *every* readable event, so on a
LoadTests run the settlement broadcasts M1 asks you to verify are evicted by the events FR3 just
added. FR3 as an absolute cannot hold; neither document mentions the cap. Also "can read" is
undefined — the code already has an `EventAttribution.Unreadable` path (`:2374`) that *does* reach
`AddEvidence`, so FR3 as written is narrower than shipped behaviour.

### F-13 (major) — FR5 is written against unobservable state

> **FR5.** "...when it has already received and acknowledged that payment's settlement broadcast."

The acknowledgement is the *Dapr sidecar's*, not the console's, and it is invisible from the
console. As written FR5 can only be tested by inspecting Redis — i.e. by rerunning the whole §1.1
investigation. Restate in terms of what the operator sees.

### F-14 (minor) — FR6 and FR9 hinge on unverifiable adverbs

- FR6: "because the feed **genuinely** dropped" — the console cannot tell genuine from spurious;
  that is the whole subject of §1.
- FR9: "at the moment the selection moves — **never lagging behind by a poll**" — presupposes the
  reader knows there is a 1-second poll (`PollInterval`, `OperatorModels.cs:705`). It is also the
  wrong property: the caption tracking the *selection* promptly says nothing about the caption
  tracking the *truth*. See F-17.

### F-15 (minor) — M3 has no threshold

> **M3.** "...stays within a few seconds of the measured drain time"

"A few" is not a number, and "the measured drain time" refers to a single unrepeatable session
(13:38:16.57–13:38:16.96). M1's "Currently 0 of *N*" is likewise an assertion about on-screen
behaviour that was never measured on screen — §1.1 measured databases and Redis.

---

## 4. Where the PRD assumes the reader's context

### F-16 (major) — Undefined terms load-bearing in the requirements

- **"the outcome query"** — NG2 declines a reconcile action because "the existing outcome query
  already covers genuine ambiguity". This is the justification for a non-goal, and the term is
  never defined, located, or shown to the reader. A PM cannot evaluate NG2.
- **"the console's own sidecar"** (§1.1 table) — assumes the reader knows DemoRunner runs a Dapr
  sidecar with its own consumer group. Nothing establishes it.
- **`pending: 0`** — assumes Redis Streams consumer-group semantics, and specifically that
  `pending: 0` implies successful app delivery rather than merely an advanced cursor. That
  inference is the load-bearing step in the whole evidence table and it is left implicit.
- **"topology run" / "run generation"** — used in FR2 and C1 with no definition in the PRD body;
  the meaning lives only in `addendum.md` §A.
- **"wait for green"** (§4 step 1) — colour-coded state that NFR4 elsewhere insists must not be the
  carrier of meaning. The demo script contradicts NFR4 in its first line.
- **C2's "refusal to silently pick a winner between HTTP and broadcast"** — a whole design
  principle referenced in a counter-metric, explained nowhere.
- **13:38:55** (§8) — has no antecedent anywhere in either document (see F-5).

### F-17 (major) — Three concrete claims about the existing UI are wrong

The addendum's §D presents F2/F3 as nearly free. It is not.

1. **"enablement already keys off `CanMutate` / `CanRestart` and needs no change"** (§D,
   `:1554–1555`). The actual code:

   ```csharp
   _resourceActionButton.Enabled = !model.IsBusy && model.Resources.Any(row => row.CanMutate);
   _restartResourceButton.Enabled = !model.IsBusy && model.Resources.Any(row => row.CanRestart);
   ```

   That is `Any(row => …)` across the whole list — **not the selected row.** FR10 ("When the
   selected resource has no legal action, the button MUST be disabled") therefore requires a change
   the addendum explicitly says is unnecessary.

2. **FR9 needs wiring that does not exist.** There is no `SelectedItemChanged` handler on
   `_resourceList` anywhere in `MainWindow.cs` — `_resourceList.SelectedItem` is only ever *read*
   (`:768`, `:2311`, `:2415`). Captions today can only refresh on the poll-driven render pass, so
   "never lagging behind by a poll" is new event plumbing, unlisted in §D.

3. **"This is a presentation change, not a behavioural one"** (§D) is false given (1) and (2).

### F-18 (major) — NFR3's premise is wrong: the action row is a *column*

> **NFR3.** "The action row must stay legible at the console's narrowest supported terminal width
> with the longest resource identifier selected. Removing Switch (FR14) buys the room the longer
> captions need."

`ActionColumnWidth = 22` (`MainWindow.cs:36`), and `StackButtons` lays buttons out **vertically**:

```csharp
buttons[index].X = 0;
buttons[index].Y = startY + index;
buttons[index].Width = Dim.Fill();
```

Removing Switch frees **one row of vertical space**, not one character of width. The captions FR7
and FR11 demand need *horizontal* room in a fixed 22-column pane. With Terminal.Gui's `[ … ]`
decoration, `Restart corebank-api` (20 chars) needs ~24 and `Restart loadtest-support` (24 chars,
and `loadtest-support` is on the `ResourceCommandAllowList`) needs ~28. **Both overflow 22 columns
today, and FR14 does not help.** The §8 assumption row —

> `[Stop AppHost] [Stop corebank-api] [Restart corebank-api] [Refresh state]`

— depicts a horizontal row that does not exist in this UI. F3 does not buy what NFR3 says it buys;
either `ActionColumnWidth` grows (taking width from `_resourceList`, `:709`) or the captions need a
truncation rule the PRD does not specify.

### F-19 (major) — FR12 and FR13 contradict each other

> **FR12.** "...`[ASSUMPTION]` Start proceeds without confirmation"
> **FR13.** "**Both controls** MUST continue to fan out across every replica instance of the
> selected resource, and **the confirmation MUST continue to list them**."

For a Start there is no confirmation left to list them in. Today `TriggerSelectedResourceAction`
(`:2303`) routes *every* action through `ConfirmAndRestore` with
`new ConfirmationRequest($"{row.NextAction} {row.Name}", …)` — which means the dialog **already
prints "Start corebank-api" and the instance list**. FR12 therefore deletes the only place the
Start target is currently named, in the same PRD whose F2 exists to make targets legible. Pick one.

### F-20 (minor) — FR8 covers 5 of 9 conditions

`ResourceCondition` has nine members (`OperatorModels.cs:32`): `Unknown, Unreachable, Stopped,
Starting, Running, Healthy, Degraded, Failed, Completed`. FR8 names five. `PresentationModel`'s
`NextAction` (`:1289`) maps the other four to `"Unavailable"` — including `Completed`, which
`ResourceReachedTarget` (`:3272`) treats as a *successful* start outcome, and which is the normal
resting state of `k6` and `loadtest-initializer`. `Starting` is a guaranteed transient during the
demo's step 5. FR8/FR10 are silent on all of them.

---

## 5. Non-goals that are load-bearing scope in disguise

### F-21 (major) — NG1/NFR1 fence out a dependency the demo actually rests on

> **NG1.** "No endpoint, message, schema or **configuration** in PaymentsAPI or CoreBankAPI is in
> scope."

Addendum §B, filed as an "environment note ... Not a defect for this PRD":

> "`dapr/components/pubsub-redis.yaml` points at `localhost:6379`, so **all pub/sub traffic flows
> through the `dapr init` Redis, not the Aspire one**"

Two consequences the PRD does not draw:

1. The whole outbox demo depends on a broker that Aspire does not manage and the console cannot
   see. M1's repeatability on another machine is not established.
2. `redis` is on `ResourceCommandAllowList` (`KnownOperatorSurface.cs:45`). After F2 ships, the
   console will offer a button reading **`Stop redis`** that stops the *Aspire* Redis — which holds
   nothing (`DBSIZE 0`) and is not the broker. F2's entire premise is that a button should name
   what it does. This one will name something it does not do, in front of an audience. NG1 forbids
   fixing it.

### F-22 (major) — NG2's justification is false in exactly the cases F1 does not cover

> **NG2.** "Considered and declined: once events stop being discarded there is nothing to catch up"

There remains plenty to catch up:
- FR6 deliberately preserves rows stranded by a genuine feed drop.
- `MaximumFeedReconnectAttempts = 3` means a feed lost for more than ~45 s is gone for the session,
  with no path back and no catch-up.
- Events arriving while `_feedContext is null` (between `StopOutcomeFeedAsync` and the next
  `StartOutcomeFeedAsync`) are still discarded silently — the null branch precedes `IsCurrent` and
  F1 does not touch it.

NG2 may still be the right call for scope, but the stated reason is wrong and should not be the
reason of record.

### F-23 (minor) — G1's scope is broader than any fix can honour

> **G1.** "...across **any** resource stop/start the operator performs within a running topology."

`ResourceCommandAllowList` includes `postgres` and `redis`. Stopping `postgres` breaks settlement
outright; per F-21, stopping `redis` breaks nothing. G1 promises correct settlement reporting
across both. Narrow G1 to the resources whose bounce the demo actually exercises, or accept that M2
("without the presenter explaining away anything on screen") is not achievable.

### F-24 (minor) — FR15 asserts an equivalence it has not checked

> **FR15.** "Changing profile is thereafter **Stop AppHost** followed by **Start Regular** /
> **Start LoadTests**, all of which already exist in the same column."

`SwitchAsync` (`:544`) performs guard work the two-step path does not obviously replicate — it
refuses when the target profile is already running or its ports are occupied, *before* stopping the
owned AppHost:

```csharp
if (targetState.Snapshot is not null || !targetState.PortsFree)
{
    return CommandResult.Rejected($"Switch blocked before stopping the owned AppHost: {targetState.Detail}.");
}
```

Removing the button removes that pre-check ordering. FR15 should state what happens when Start
fails after Stop succeeded, or FR14 should be deferred until it does. (Note the buttons are stacked
in a column, not a row — FR14's "action row" and NFR3's phrasing both describe a layout that is not
there; see F-18.)

---

## Priority

**Blockers — resolve before architecture**

- F-1 diagnosis stated as proven while §8 admits it is not, and the proposed verification cannot
  discriminate.
- F-2 the feed-status and reconnect paths carry the same guard and are missing from §D's caller
  inventory; if they are implicated, the fix is a different fix.
- F-7 FR2's "preserved, not weakened" is false — an externally relaunched AppHost keeps the same
  profile and generation, so C1 is not delivered.

**Majors — fix in the PRD**

F-3, F-4, F-5, F-8, F-9, F-11, F-12, F-13, F-16, F-17, F-18, F-19, F-21, F-22.

**Minors**

F-6, F-10, F-14, F-15, F-20, F-23, F-24.

---

## Two things the PRD gets right, so they are not lost in redrafting

- Addendum §D's warning — "Callers of `IsCurrent` beyond the feed path ... must each be reviewed
  ... **Do not blanket-change the predicate.** The safer shape is a separate, explicitly-named
  check for event attribution" — is correct and is the right design. It should be promoted into the
  PRD body as a requirement, not left as an addendum aside.
- Addendum §E's choice of raw resource identifiers over curated display names is well reasoned and
  survives review intact. Note that it is also what makes the captions too wide for the 22-column
  action pane (F-18) — the naming decision is right; the layout requirement built on top of it is
  wrong.
