---
title: "Code-accuracy review: resource-lifecycle PRD + addendum"
status: review
created: 2026-09-10
reviewer: code verification pass against working tree (branch feature/evidence-payload-bodies)
---

# Code-accuracy review

Every code claim in `prd.md` and `addendum.md` checked against the actual source. Verified against
the working tree as of branch `feature/evidence-payload-bodies` (commit `835b9f5`).

**Totals:** 49 discrete claims checked — **6 wrong**, **5 imprecise but not misleading**,
**34 confirmed correct**, **4 unverifiable from source** (live-session measurements).

All six cited files exist at the paths given:

| Cited path | Exists |
| --- | --- |
| `CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs` | yes |
| `CoreBankDemo.DemoRunner/Application/AspireJsonParser.cs` | yes |
| `CoreBankDemo.DemoRunner/Application/OperatorModels.cs` | yes |
| `CoreBankDemo.DemoRunner/Application/TopologyObservationDebouncer.cs` | yes |
| `CoreBankDemo.DemoRunner/Terminal/MainWindow.cs` | yes |
| `CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs` | yes |

---

## 1. Wrong claims

### W1 — Replica count never goes to 0 (`addendum.md` §A)

> "Stopping `corebank-api` moves its replica count 2 → 0 and empties its endpoint list; restarting
> walks it back 0 → 1 → 2 as replicas come up."

**Actual.** `ReplicaCount` is floored at 1 and can never be 0:

- `CoreBankDemo.DemoRunner/Application/AspireJsonParser.cs:105` — `Math.Max(1, matches.Count),`
- `CoreBankDemo.DemoRunner/Application/OperatorModels.cs:178` — `int ReplicaCount = 1,`

If a resource disappears from Aspire's output entirely, `BuildResource` returns `null`
(`AspireJsonParser.cs:83-86`) and the resource has **no row at all** — it lands in `missing`
rather than as a zero-replica row. So the two possible shapes are "row with ≥1 replica and an
empty/short endpoint list" or "no row".

**Impact.** The conclusion survives — the endpoint list really does empty, and the endpoint segment
is part of the fingerprint string (`AspireJsonParser.cs:45-48`), so the fingerprint really does
change. Only the stated numbers are wrong. Correct the sentence to speak about endpoints and row
presence, not a 0 replica count, or an implementer will go looking for a state that cannot occur.

### W2 — A topology **stop** does not bump `RunGeneration` (`addendum.md` §C; `prd.md` FR2)

> addendum §C: "A topology start, stop or switch bumps `RunGeneration` via `ActivateTopology`…"
> prd.md FR2: "*(A topology start, stop or switch begins a new run; a resource command does not.)*"

**Actual.** `StopAsync` (`OperatorConsoleController.cs:483`) never touches `RunGeneration`. Its
state update at `OperatorConsoleController.cs:520-534` sets `Profile = TopologyProfile.None`,
`Ownership = None`, `Topology = null` — no generation increment. `RunGeneration` has exactly one
assignment in the entire codebase, `OperatorConsoleController.cs:3294`, inside `ActivateTopology`,
which is called only from Start (`:414`), Attach (`:471`) and Switch (`:625`).

**Impact.** The *safety argument* still holds, but for a different reason than stated: `StopAsync`
calls `StopOutcomeFeedAsync`, which nulls `_feedContext` (`OperatorConsoleController.cs:2231`), and
the guard's `context is null` arm catches it; the `Profile == None` clause backs that up. Restate as
"start, attach or switch bumps `RunGeneration`; a stop tears down the feed context outright" — the
current wording invites a reviewer to conclude the chosen design has a hole after a stop, and it
does not.

### W3 — `:1554–1555` does need a change (`addendum.md` §D)

> "`:1554–1555` enablement already keys off `CanMutate` / `CanRestart` and needs no change."

**Actual.** `CoreBankDemo.DemoRunner/Terminal/MainWindow.cs:1554-1555`:

```csharp
_resourceActionButton.Enabled = !model.IsBusy && model.Resources.Any(row => row.CanMutate);
_restartResourceButton.Enabled = !model.IsBusy && model.Resources.Any(row => row.CanRestart);
```

Enablement keys off **`Any` row in the list**, not the **selected** row. `prd.md` FR10 requires:
"When the selected resource has no legal action, the button MUST be disabled and MUST say why in
the hint line rather than failing on press." Today the button stays enabled and fails on press —
`MainWindow.cs:2313-2317` shows `"{row.Name} has no legal next action in the fresh Aspire state."`
after the click, which is precisely the behaviour FR10 rules out.

**Impact.** These two lines must become selection-derived alongside the captions. Calling them
"no change" understates the change and will lose FR10 in implementation.

### W4 — The confirmation dialog does not print `aspire resource corebank-api stop` (`addendum.md` §E)

> "the identifier matches the resource list row the operator just selected and the
> `aspire resource corebank-api stop` command the confirmation dialog prints"

**Actual.** The command text is built per **instance**, not per resource
(`MainWindow.cs:2291-2294`):

```csharp
private static string ExactCommands(IReadOnlyList<string> instances, string command) =>
    string.Join(
        " && ",
        instances.Select(instance => $"aspire resource {instance} {command.ToLowerInvariant()}"));
```

`row.Instances` comes from `resource.InstanceNames` (`PresentationModel.cs:226`), populated from
Aspire's per-replica instance names (`AspireJsonParser.cs:107`), which for a replicated project
match on the `knownName + "-"` prefix (`AspireJsonParser.cs:121-122`) — i.e. `corebank-api-<suffix>`.
For a 2-replica `corebank-api` the dialog prints two instance-suffixed commands joined by `&&`, not
`aspire resource corebank-api stop`. `AspireCliAdapter.cs:168-180` executes the same per-instance
form.

Only the dialog **title** uses the bare resource name: `$"{row.NextAction} {row.Name}"`
(`MainWindow.cs:2321`) → `"Stop corebank-api"`.

**Impact.** The naming rationale in §E is built on a false premise. The rationale is still
defensible — the *title* matches — but as written it claims a correspondence with the printed
command that does not exist. Fix the sentence to cite the confirmation title.

### W5 — `KnownResources.Abbreviate` does not exist (`addendum.md` §E)

> "`KnownResources.Abbreviate` exists for a different purpose and is not extended here."

**Actual.** `KnownResources` is declared at
`CoreBankDemo.DemoRunner/Application/KnownOperatorSurface.cs:16` and has **no** `Abbreviate` member.
The only `Abbreviate` in the repository is a private method on the presentation layer:
`CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs:1307` —
`private static string Abbreviate(string resourceName)`, used once at `PresentationModel.cs:289`
for the compact topology status strip.

**Impact.** Cosmetic but it is a wrong type qualifier in a document that will be read as a code map.

### W6 — The demo's Step 5 cannot happen with the current gate (`prd.md` §4, FR8)

> §4 step 5: "Return to Resources. The same button — same place, same resource selected — now reads
> **Start corebank-api**. Press it."
> FR8: "stopped offers **Start**"

**Actual.** A stopped `corebank-api` makes the topology snapshot fail its fingerprint match, and
*every* resource mutation is gated on that match:

- `AspireJsonParser.cs:33-44` — `endpointMismatches` flags `corebank-api` as soon as no endpoint
  answers on the expected port (5032, `KnownOperatorSurface.cs:72`), so `fingerprintMatch` is
  `false` while the resource is down.
- `PresentationModel.cs:1261-1266` — `CanMutateResource` requires
  `state.Topology is { IsReachable: true, IsFingerprintMatch: true }`.
- `MainWindow.cs:1554` — the action button is enabled only if some row has `CanMutate`.
- `Infrastructure/AspireCliAdapter.cs:151-153` — even if pressed, the command is rejected:
  `if (!snapshot.IsReachable || !snapshot.IsFingerprintMatch || resource is null) return ResourceCommandResult.Rejected(...)`.
- `OperatorConsoleController.cs:348-350` — `ResourceAuthorityAvailable` is likewise set from
  `snapshot.IsFingerprintMatch`.

**Impact.** This is the most consequential omission in either document. As the code stands, stopping
`corebank-api` from the console disables the very control the demo needs to start it again, and the
CLI adapter would refuse the command regardless of the button. Making the caption
selection-derived (F2) does not address it. Either the fingerprint gate on *resource commands* must
be relaxed the same way `IsCurrent`'s fingerprint clause is, or FR8/§4 step 5 is unachievable.
Neither document mentions this gate at all. It belongs in the addendum next to the `IsCurrent`
analysis and should be an explicit story-level requirement.

---

## 2. Imprecise, but not misleading

### I1 — `AspireJsonParser.cs:44` vs `:45` (`addendum.md` §A)

The fingerprint assignment starts at line **45**, not 44 (line 44 is
`&& endpointMismatches.Count == 0;`). Within tolerance; noted for exactness. The quoted snippet
itself is character-for-character correct against lines 45-48.

### I2 — "`_feedContext` is assigned only in `StartOutcomeFeedAsync`" (`addendum.md` §A)

Two assignments exist, not one: `OperatorConsoleController.cs:2152` (the capture, inside
`StartOutcomeFeedAsync`, which does begin at `:2145` as cited) and
`OperatorConsoleController.cs:2231` (`_feedContext = null;` in `StopOutcomeFeedAsync`). The claim's
intent — no resource command re-captures it — is correct and is load-bearing for W2 above.

### I3 — The two-second trailing window is narrower than described (`addendum.md` §A, §C)

> "`state.Topology.Fingerprint` trails physical reality by roughly two seconds minimum."

`TopologyObservationDebouncer.Observe` short-circuits before debouncing when the observed snapshot
is unreachable **or fails its fingerprint match** (`TopologyObservationDebouncer.cs:9-13`,
returning `observed` immediately). While `corebank-api` is down the snapshot never matches, so
there is **no debounce at all** during the outage — the state tracks reality within one poll. The
two-poll lag applies only at the transition *back into* a fingerprint-matching state, i.e. the
moment both replicas are healthy on port 5032 again.

That is still exactly the moment the recovery burst lands, so §A's conclusion holds and §C's
rejection of the re-capture alternative holds with it. But "trails by two seconds minimum" as a
general statement about the outage window is wrong, and an implementer reasoning from it will
mis-model the degraded period.

### I4 — Row `NextAction` can be downgraded (`addendum.md` §D)

`:222` does call `NextAction(resource.Condition)` as claimed, but the value written into
`ResourceRowViewModel.NextAction` is `supported ? nextAction : "Unavailable"`
(`PresentationModel.cs:223`, `:230`) — the raw mapping is filtered through
`resource.Supports(command)`. Captions derived from the row will therefore inherit that filter,
which is desirable but unstated.

### I5 — The `IsCurrent` caller count is understated (`addendum.md` §D) — see §4 below

---

## 3. Confirmed correct

### `addendum.md` §A

| Claim | Verdict |
| --- | --- |
| `OperatorConsoleController.cs:2352` is `OnOutcomeEventReceived` | correct, exact line |
| The quoted opening block (`var context = _feedContext;` … `return;`, including both comment lines) | **character-for-character exact**, lines 2354-2360 |
| The `return` precedes both `AttributeEvent` and `AddEvidence` | correct — guard at `:2355-2360`, `AttributeEvent` at `:2366`, `AddEvidence` at `:2379` |
| A discarded event resolves no row and leaves no trace | correct — nothing runs after the `return` |
| `IsCurrent` at `:3573` | correct, exact line |
| The quoted `IsCurrent` body (three clauses) | **character-for-character exact**, lines 3573-3579 |
| `IsCurrent` compares profile, run generation and fingerprint | correct — three clauses, exactly as described |
| The quoted fingerprint expression | **character-for-character exact**, lines 45-48 |
| Fingerprint encodes name, replica count and endpoint list per resource | correct — `$"{resource.Name}:{resource.ReplicaCount}:{string.Join("|", resource.Endpoints.Order(...))}"` |
| `StartOutcomeFeedAsync` at `:2145` | correct, exact line |
| It runs on topology activation and feed restart | correct — called at `:416`, `:473`, `:627` (after `ActivateTopology`) and `:2226` (reconnect) |
| A resource command does not re-capture `_feedContext` | correct — no assignment on any resource-command path |
| `RunGeneration` increments only in `ActivateTopology`, at `:3294` | correct — `RunGeneration = state.RunGeneration + 1,` is the sole assignment in the repository, on that exact line |
| `RunGeneration` correctly does not move for a resource command | correct |
| `OperatorConsoleOptions.PollInterval` is 1 second at `OperatorModels.cs:705` | correct, exact line — `public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);` in `OperatorConsoleOptions` (`:690`) |
| `TopologyObservationDebouncer.Observe` requires two consecutive agreeing snapshots | correct — `:23-27` releases on the second matching signature, `:29-34` withholds on the first |
| The quoted withhold message | **exact** — `TopologyObservationDebouncer.cs:33` |
| `dapr/components/pubsub-redis.yaml` points at `localhost:6379` | correct — `redisHost: localhost:6379` |
| Event type `com.corebank.transaction.completed` | correct — `Constants.TransactionCompleted` |
| Topic `transaction-events` | correct — `CoreBankAPI/appsettings.json:19` |

### `addendum.md` §C

| Claim | Verdict |
| --- | --- |
| `_unmatchedTerminalEvents` at `:2111` | correct, exact line |
| It buffers events that arrive before their row exists | correct — `:2900-2920` buffers, `:2870-2872` drains on correlation |
| It sits downstream of the guard | correct — guard at `:2355`, buffering reached only via `AttributeEvent` at `:2366` |
| It is cleared on correlation reset | correct — `:2156-2159` (`resetCorrelation: true`) and `:2232-2235` (feed stop) |

### `addendum.md` §D — all button captions and line numbers verified exact

| Claim | Verdict |
| --- | --- |
| `:225` `_resourceActionButton = NewButton("Resource action")` | **exact**, line 225, caption verbatim |
| `:226` `_restartResourceButton = NewButton("Restart selected")` | **exact**, line 226, caption verbatim |
| `:224` `_switchButton = NewButton("Switch topology")` | **exact**, line 224, caption verbatim |
| `_switchButton` `Accepting` handler at `:743` | correct, exact line |
| `_switchButton` enablement at `:1553` | correct — `_switchButton.Enabled = model.CanStopOrSwitch;` |
| `:723` `StackButtons(actions, 5, _stopButton, _switchButton, _resourceActionButton, _restartResourceButton, _refreshButton)` | **character-for-character exact**, line 723 |
| `PresentationModel.cs:1289` `NextAction(ResourceCondition)` | correct, exact line |
| It yields `Start` / `Stop` / `Restart` / `Unavailable` | correct — `Stopped→Start`, `Healthy or Running→Stop`, `Failed or Degraded→Restart`, `_→Unavailable` (`:1291-1294`) |
| `:222` resolves it per row into `ResourceRowViewModel.NextAction` | correct (with the caveat in I4) |
| "already surfaced per row" | correct — rendered as `[{row.NextAction}]` at `MainWindow.cs:1508`, and consumed at `MainWindow.cs:2313`/`:2319`/`:2321` |
| All 8 explicitly cited `IsCurrent` call sites (`:251`, `:1052`, `:1504`, `:1553`, `:1659`, `:2039`, `:2597`, `:2642`) | every one is a real `IsCurrent` call site at that exact line |
| "several use it to decide whether a *mutation result* still applies" | correct — e.g. `CommitFaultsCoreAsync` (`:1504`, `:1553`), `RestartDevProxyAsync` (`:1659`), `RunLoadTestAsync` (`:1787`, `:1796`) |

### `prd.md`

| Claim | Verdict |
| --- | --- |
| The control is labelled "Resource action" | correct — `MainWindow.cs:225` |
| "The row beneath it already renders the real action as `[Stop]` or `[Start]`" | correct — `MainWindow.cs:1508` |
| Identity is established from topology shape — which resources exist, replica counts, endpoints | correct — `AspireJsonParser.cs:45-48` |
| FR12: the confirmation step displays the exact `aspire` commands and the affected instances | correct — `ConfirmationRequest(title, exactCommands, row.Instances)`, `MainWindow.cs:2319-2322` |
| FR13: commands fan out across every replica instance and the confirmation lists them | correct — `AspireCliAdapter.cs:161-180` loops instances; `row.Instances` is passed to the dialog |
| FR15: Stop AppHost / Start Regular / Start LoadTests already exist in the same column | correct — all stacked into the same `actions` container, `MainWindow.cs:722-724` |
| NG1/NFR1: no banking-service change is implied by any of the above | correct — every cited change site is under `CoreBankDemo.DemoRunner/` |

---

## 4. `IsCurrent` callers — exact count (addendum §D claims "~10")

**The claim understates it.** There are **19** call sites plus the definition. Excluding the three
that are unambiguously on the feed path, **16 callers lie beyond it** (15 if `CountForBurst` is
counted as feed-path, since it runs only from the event handler).

All in `CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs`:

| Line | Enclosing member | Feed path? | Cited in addendum? |
| --- | --- | --- | --- |
| 251 | `RefreshAsync` | no | yes |
| 259 | `RefreshAsync` | no | no |
| 298 | `RefreshAsync` | no | no |
| 1052 | `UpdateTrackedPayment` | no | yes |
| 1302 | `RunBurstAsync` | no | no |
| 1504 | `CommitFaultsCoreAsync` | no | yes |
| 1553 | `CommitFaultsCoreAsync` | no | yes |
| 1659 | `RestartDevProxyAsync` | no | yes |
| 1787 | `RunLoadTestAsync` | no | no |
| 1796 | `RunLoadTestAsync` | no | no |
| 1934 | `SubmitPaymentInternalAsync` | no | no |
| 1953 | `SubmitPaymentInternalAsync` | no | no |
| 2011 | `AdoptExistingRow` | no | no |
| 2039 | `BeginTrackingSubmission` | no | yes |
| 2192 | `TryReestablishOutcomeFeedAsync` | **yes** | no |
| 2265 | `OnFeedStatusChanged` | **yes** | no |
| 2355 | `OnOutcomeEventReceived` | **yes** (the defect site) | — |
| 2597 | `CountForBurst` | downstream of the handler | yes |
| 2642 | `TrackSubmittedPayment` | no | yes |
| 3573 | *definition* | — | yes |

The addendum's warning — "**Do not blanket-change the predicate**" — is if anything *more* justified
than it reads: the blast radius is 16 non-feed call sites across refresh, fault commit, Dev Proxy
restart, load-test, burst and payment-submission paths, not ~10. Update the number and consider
listing the members rather than bare line numbers, since the line numbers will drift.

Note also that `:2265` (`OnFeedStatusChanged`) is itself a feed-path caller the addendum does not
mention: it uses the same predicate to decide whether a feed status transition still applies. If the
fix is a separate, explicitly-named attribution check, `:2265` needs a decision too — status
transitions arriving during a resource bounce are subject to the identical fingerprint problem.

---

## 5. Not verifiable from source

These are runtime observations from the captured session and cannot be checked against the
repository. They are recorded here as unverified, not as disputed:

- `addendum.md` §B: AppHost PID 927525, all timestamps, the five-row `OutboxMessages` table,
  `InboxMessages` / `MessagingOutboxMessages` completion states, "1147 entries" in the Redis
  stream, both consumer groups at `pending: 0` with an identical `last-delivered-id`, and the
  quoted CloudEvent JSON.
- `addendum.md` §B environment note: the two Redis containers (`redis-495bfe3c` vs `dapr_redis`)
  and their port/`DBSIZE` state. The *configuration* half of that claim is verified — 
  `dapr/components/pubsub-redis.yaml` does point at `localhost:6379`, so the conclusion that
  pub/sub traffic bypasses the Aspire-managed Redis follows from config alone.
- `prd.md` §1.1 evidence table and the "under two seconds, on the first attempt" drain figure.
- `prd.md` §8 "Verification still owed" row.

Table and column names cited in §B are real: `CoreBankDbContext.MessagingOutboxMessages`
(`CoreBankDemo.CoreBankAPI/CoreBankDbContext.cs:20`), and the "Awaiting settlement" wording the PRD
attributes to the Operations screen is genuine console copy (e.g.
`OperatorConsoleController.cs:2057`, `Application/Ports/IOutcomeFeed.cs:111`).

---

## 6. Recommended edits

1. **`addendum.md` §A** — replace "replica count 2 → 0 … 0 → 1 → 2" with the endpoint-list /
   row-presence mechanism (W1).
2. **`addendum.md` §A/§C** — qualify the two-second trailing window: it applies at the transition
   back into a matching fingerprint, not throughout the outage (I3).
3. **`addendum.md` §C and `prd.md` FR2** — stop does not bump `RunGeneration`; it tears down
   `_feedContext` (W2).
4. **`addendum.md` §D** — `:1554–1555` must become selection-derived to satisfy FR10 (W3).
5. **`addendum.md` §D** — correct "~10 callers" to 16, and add `:2265` `OnFeedStatusChanged` to the
   list of feed-path users needing a decision (I5, §4).
6. **`addendum.md` §E** — the dialog prints per-instance commands; cite the dialog *title* instead,
   and change `KnownResources.Abbreviate` to `PresentationModel.Abbreviate` (W4, W5).
7. **Both documents** — add the `IsFingerprintMatch` gate on resource commands as a first-class
   concern. Without relaxing it, FR8 and §4 step 5 cannot be delivered (W6).
