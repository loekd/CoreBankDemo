---
title: 'Evidence payload bodies: request, response and CloudEvent on the Details pane'
type: 'feature'
created: '2026-09-10'
status: 'done'
baseline_commit: '23fab1112c38475468b1e3bb2f4242f16f2435d7'
review_loop_iteration: 1
context:
  - '{project-root}/docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10/prd.md'
  - '{project-root}/docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10/addendum.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The Evidence workspace records that something happened but not what was sent. `HttpPaymentGateway` sets `Idempotency-Key` and discards the request; `DaprOutcomeFeed.TryParse` deserializes a CloudEvent into a typed record and drops its bytes, dropping the whole event when it cannot; and `JournalRedaction` replaces secret-looking header text with `[redacted]`. On stage the audience cannot see that the `transactionId` **is** the `Idempotency-Key`, cannot compare two idempotent responses byte for byte, and cannot be shown a `transaction-events` CloudEvent as delivered.

**Approach:** Capture the exchange where it happens — an `HttpExchange` on the four gateway call sites, a `CloudEventRecord` in the outcome feed — carry both home on `EvidenceRecord`, and render them in the Details pane as two columns, `REQUEST` left and `RESPONSE` right, each reading top to bottom as a raw HTTP exchange. Drop the secret substitution, keep the 8 KB bound, border the pane, and cut evidence list rows at the first em dash.

## Boundaries & Constraints

**Always:**
- The console shows bytes; the presenter narrates. No diffing, no highlighting the matching field, no annotation of what a payload means.
- Headers are recorded as they were set. When `IdempotencyMode.Omitted` was used, **no** key header is recorded — the absence is the fact, and an explanatory line in its place would be interpretation.
- ADR-015 holds: no project reference to any banking project, no store or broker access, no banking service grows a surface for the console's benefit.
- An event this console cannot attribute carries **no** transaction id and must reach none of the accounting: not `IndexOfTrackedPayment`, not `_burstTransactions`, not `_retiredTransactions`, not `RememberUnmatchedEvent`. The existing precedent is `TrackSubmittedPayment` at `OperatorConsoleController.cs:2583`.
- Existing behaviour that must survive: an arriving broadcast never steals the Details pane (`select: false` plus the `_rebindingEvidenceList` guard), a trimmed selection falls back to the newest record, fault provenance stays on the record, and `Copy` still reports its own outcome.
- The ≥90% line-coverage gate in `tests/Directory.Build.props:44` stays green. `HttpPaymentGateway*`, `LoadWorkflowRunner*`, `DaprOutcomeFeed*`, `MainWindow*`, `PresentationModelBuilder*` and all of `Application.*` are inside the measured boundary.

**Ask First:**
- Any change to a banking project. This is DemoRunner-only.
- Adding a keystroke, a workspace, or a control beyond the three text panes named below.
- Weakening or deleting an existing test rather than rewriting it to the new behaviour — except the two redaction assertions this spec explicitly retires.

**Never:**
- No `LoadTest`, `Burst`, `Topology`, `Resource`, `Export` or `Fault` record grows a request or a response. A `LoadTest` record covers seven-plus calls including a drain poll that fires up to 150 times; there is no single exchange to show and stacking them is out of scope.
- No second correlation identifier. An event without a `transactionId` gets a row, not an invented id.
- No narrow-terminal layout fallback. Scroll bars cover the degradation.
- No new writer to disk. Export stays the only one, and it serializes `EvidenceRecord` directly.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Submit, generated key | `SubmitAsync`, `IdempotencyMode.Generated` | Record carries `HttpExchange`: `POST` + absolute URL, `Idempotency-Key` and `Content-Type` headers, request body; status line, response headers, response body | N/A |
| Submit, key omitted | `IdempotencyMode.Omitted` | Same, with **no** `Idempotency-Key` header present in `RequestHeaders` | N/A |
| Submit, no answer | Timeout or `HttpRequestException` | Exchange carries the request and `StatusCode is null`; RESPONSE column reads that no answer arrived | Outcome mapping unchanged (`Ambiguous`/`TransportFailure`) |
| Cancel | `CancelAsync` | Exchange with `POST`, body, status line, headers, body. No `Idempotency-Key` — the call sets none | N/A |
| Outcome query / inspect | `QueryOutcomeAsync`, `InspectAsync` | Exchange with `GET` + resolved URL, **no** request headers and no request body; full response side | Resolver `ArgumentException` yields a record with no exchange |
| Known event, with id | `com.corebank.transaction.completed` + `transactionId` | `OutcomeEvent` record carries `CloudEventRecord` (envelope + `Data` bytes); attribution and row resolution unchanged | N/A |
| Unknown event type | `com.corebank.something.new` | Row created: envelope + raw data, `TransactionId` null, no typed payload, touches no accounting, summary says the type was not recognised | Acked `Success` as today |
| Known type, no id | `…transaction.completed` with `transactionId` missing or empty | Same as unknown type: row with envelope + raw data, no id, no accounting | Acked `Success` |
| Malformed event JSON | Body is not JSON | Row with envelope + raw bytes verbatim, no typed payload | `JsonException` caught, never thrown into the stream |
| Payload-less record | `Topology`, `Resource`, `Export`, `Fault`, `Burst`, `LoadTest` | Header block, then one line stating the action was not an HTTP exchange. No empty columns | N/A |
| JSON body | Any body parsing as JSON | Pretty-printed in the column | Malformed or truncated shown as received |
| Non-JSON body | HTML, plain text, empty | Shown verbatim, not mangled | N/A |
| Body over the bound | Body longer than `JournalText.MaxLength` | Truncated at 8192 with `…` | N/A |
| Copy | Any record selected | Clipboard receives raw request text, blank line, raw response text — no column art, no pretty-printing | Existing empty-list and empty-text messages unchanged |
| Row summary | Summary contains ` — ` | List row shows text before the first em dash only; gutter marker and status glyph keep their columns | Summary without an em dash is unchanged |
| Event row identity | Any `OutcomeEvent` summary | The row keeps the verb, the transaction id and the attribution word; only the explaining clause is dropped. ` · ` separates identity, ` — ` introduces the droppable clause, and no event summary puts an em dash before its id | A row with no id (unreadable event) keeps the type and drops the clause |
| Full summary | Same record | Details pane, status line and export carry the full untruncated summary | N/A |

</frozen-after-approval>

## Code Map

**Models — where the new types land**
- `CoreBankDemo.DemoRunner/Application/OperatorModels.cs:336` `EvidenceRecord` — 15 positional members ending in two optional ones (`FaultLevels`, `TransactionId`). Append two more optional trailing members so no existing construction site changes.
- `:255` `PaymentResult`, `:318` `PaymentCancellationResult`, `:328` `InspectionResult` — each gains one optional trailing `HttpExchange? Exchange = null`.
- `:141` `EvidenceKind` (10 members), `:76` `IdempotencyMode`, `:641` `MaximumTrackedPayments = 100`, `MaximumEvidenceRecords = 500`.

**HTTP capture**
- `CoreBankDemo.DemoRunner/Infrastructure/HttpPaymentGateway.cs:15` `SubmitAsync` — builds URL at `:20` (`EndpointResolver.EndpointFor`), body at `:26-33`, the single header at `:35` (`TryAddWithoutValidation("Idempotency-Key", …)`, skipped when null). Response read `:41-54`. Catch arms `:56-68` (`TaskCanceledException`) and `:69-83` (`HttpRequestException`) are the no-answer paths.
- `:94` `CancelAsync` — URL+method from the resolver at `:99`, body `:103-111`, no headers. `:263` `SendInspectionAsync` — sends a bare `new HttpRequestMessage(method, url)` at `:291`, no headers, no body; `ArgumentException` from the resolver caught at `:278` before any request exists.
- `:13` `BodyExcerptLength = 200` and `Excerpt` at `:353` are for **error prose only** (`DescribeViolation` `:334`, `:339`; cancel branches `:135`, `:154`). Do not reuse as the payload bound.
- `CoreBankDemo.DemoRunner/Infrastructure/EndpointResolver.cs:36` `EndpointFor` returns `(Url, Method)`; `SubmitAsync` discards the method and hardcodes POST.
- `CoreBankDemo.DemoRunner/Infrastructure/LoadWorkflowRunner.cs:210` `SendAsync` returns `InspectionResult` too. It compiles unchanged against the new optional member and its exchange is never attached to a record — `LoadTest` carries none.

**Bounding (was redaction)**
- `CoreBankDemo.DemoRunner/Application/Ports/JournalRedaction.cs` — whole file: `MaxLength = 8192`, `Apply`, `SecretLikePattern`. 14 production call sites: `OperatorConsoleController.cs:407`, `:621`, `:3380`, `:3385`; `AspireCliAdapter.cs:46`, `:125`, `:193`, `:203`, `:213`; `AspireProcessAdapter.cs:75`, `:89`, `:198`; `DaprSidecarProcess.cs:245`, `:339`, `:373`; `LoadWorkflowRunner.cs:207`. Most are process output, not evidence payloads — the substitution goes from all of them.

**CloudEvent capture**
- `CoreBankDemo.DemoRunner/Infrastructure/DaprOutcomeFeed.cs:351` `HandleAsync` — calls `TryParse(message.Type, message.Data.Span)`, invokes `EventReceived` only when non-null, always returns `TopicResponseAction.Success`. `:368` formats its handler-error string from `parsed.TransactionId` and must tolerate null.
- `:318` `TryParse` — the four-arm switch, each gated `{ TransactionId.Length: > 0 }`, `_ => null` for unknown types, `JsonException => null` at `:343`.
- **Verified:** `Dapr.Messaging.PublishSubscribe.TopicMessage` is `public sealed` with a public 7-arg constructor `(Id, Source, Type, SpecVersion, DataContentType, Topic, PubSubName)` and public setters on `Data`, `Path`, `Extensions`. It is constructible in tests, so the new parse seam can take a `TopicMessage` and stay statically testable. It exposes **no** `subject` and **no** `time` — do not surface either.
- `CoreBankDemo.DemoRunner/Application/Ports/IOutcomeFeed.cs:67` `OutcomeEvent(string EventType, string TransactionId, …four optional wire records)`, factories `From` at `:84`, `:87`, `:90`, `:93`; `ProcessedAt` `:76`, `IsTerminal` `:82`. Event type constants `:14-23`, `Topic`/`PubSubComponent` `:25-26`.

**Attribution — the paths a null id must not enter**
- `CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs:2348` `OnOutcomeEventReceived`. Keyed off the id: `:2362` `IndexOfTrackedPayment` (defined `:2875`), `:2375` `_burstTransactions.TryGetValue` (declared `:35-40`), `:2382` `_retiredTransactions.Contains` (`BoundedTransactionIdSet`, declared `:2112`, type `:2984`), `:2393` `RememberUnmatchedEvent` (`:2848`, dictionary keyed by id, cap 200 at `:2109`).
- `:2423` `private enum EventAttribution { Unattributed, Tracked, Retired }`; `:2903` `EventSummary` — its terminal arm `:2923` prints the id inside the Unattributed sentence.
- `:2396` the per-event `AddEvidence` (`select: false`); `:2199`, `:2241`, `:2323`, `:2709` the four other `OutcomeEvent` records (feed lifecycle and late attribution) — those carry no CloudEvent.
- `:3328` / `:3358` the two `AddEvidence` overloads; record built `:3374-3388`; trim `:3390`; selection `:3398-3411`; `CarriesAppliedFaults` `:3426` (`Payment | OutcomeQuery | Inspection` only — unchanged).
- Payment records: `:1966` submit, `:858` cancel, `:1107` outcome query, `:1134` inspect. These four are the only sites that attach an exchange. `:1335` burst aggregate and `:1794` load workflow attach none.

**Presentation**
- `CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs:210-224` the evidence projection (`< ` gutter for `OutcomeEvent`, `StatusGlyph` `:660`, `FaultProvenance` `:718`); `:69` `EvidenceRowViewModel`; `:229-231` the selected-detail branch; `:152-153` the two `OperatorPresentationModel` members; `:726` `EvidenceDetailText` (header block of 5 lines + optional `Transaction:` + blank + body); `:761` `FormatBody` (finds the first `{`/`[`, pretty-prints in place, keeps trailing text, returns input verbatim on `JsonException`); `:807` `IndentedJson`.
- `CoreBankDemo.DemoRunner/Terminal/MainWindow.cs:222-229` the eight Evidence controls; `:773-843` `BuildEvidenceView` — panes are **flat siblings** of the workspace `FrameView`, no container, `_evidenceList` `Dim.Percent(42)`, `_evidenceDetail` `X = Pos.Right(_evidenceList) + 1`, `Width = Dim.Fill(1)`, both `Height = Dim.Fill(3)`.
- `:802-811` selection → `_controller.SelectEvidence`; `:813-826` Details button; `:827-832` the wrap toggle (button only, no key binding); `:833` Copy wiring; `:2089-2112` `CopyDetailToTerminalClipboard` — reads `_evidenceDetail.Text`, i.e. the **formatted** text, and guards on `_evidenceRows.Count`.
- `:1446-1462` the render block; `:1680-1699` `ApplyAnnouncement` lends a row from both panes; `:1701-1712` `ApplyResponsiveLayout` — `:1706` `_navigation.BorderStyle = _compactLayout ? LineStyle.None : LineStyle.Rounded` is the **only** border assignment in the file, and Evidence is not touched here today; `:1336` `NewWorkspace`; `:2375` `ListBinding`; `:2254-2257` and `:2300-2302` test accessors.
- `CoreBankDemo.DemoRunner/Terminal/OperatorTheme.cs:19` `BaseScheme` ("CoreBankCockpit", `TextPrimary` on `SurfaceBase`); `:154` `Apply(view, schemeName)` — names only. **No Evidence control is given a scheme today**; they all inherit `BaseScheme` from the window, and a `Border` inherits its view's `SchemeName`. That is what makes FR-21 free: give the pane a border and assign nothing.
- Terminal.Gui 2.4.17: `View.VerticalScrollBar` / `HorizontalScrollBar` are lazy-loaded; `ScrollBarVisibilityMode.Auto` shows a bar only when content exceeds the viewport; `TextView` ships `UpdateHorizontalScrollBarVisibility`.

**Tests**
- `tests/.../Application/Ports/JournalRedactionTests.cs` — 45 lines, 4 tests. `:26-36` `Apply_SecretLikeHeaderText_IsRedacted` and `:38-44` `Apply_OrdinaryEvidenceText_IsNotRedacted` are the two this spec retires.
- `tests/.../Infrastructure/DaprOutcomeFeedTests.cs:123` `TryParse_UnknownEventType_IsDroppedRatherThanGuessedAt` and `:139` `TryParse_MissingTransactionId_IsDropped` — both assert the behaviour this spec inverts; rewrite, do not delete.
- `tests/.../Infrastructure/HttpPaymentGatewayTests.cs` (`:282` the 504 mapping), `tests/.../Infrastructure/LoadWorkflowRunnerTests.cs`.
- `tests/.../Application/OperatorConsoleControllerTests.cs:139` and `:146` reference `JournalRedaction.MaxLength`; `:1620` `UnattributedEvent_IsRecordedLabelledAndTouchesNoPaymentRow`; `:1928` `PaymentEvidence_CarriesTheTransactionIdItCorrelatesBy`; `:1448` `Evidence_IsBoundedAndSelectionIsExplicit`; `:2213` fault stamping.
- `tests/.../Terminal/PresentationModelBuilderTests.cs:60-101` the four `FormatBody` tests; `:103` `EvidenceDetail_CarriesTheHeadersAndThePayload`; `:152` the empty-body test; `:707` the `< ` gutter test.
- `tests/.../Terminal/MainWindowTests.cs:335` wrap toggle; `:405` and `:428` the two copy tests; `:637` selection moves the pane; `:667` the rebind guard.
- `tests/.../Fakes/OperatorHarness.cs:224-305` `FakePaymentGateway`; `:552` `FakeOutcomeFeed` with `Push` `:616` and the four typed helpers `:618-627`.

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.DemoRunner/Application/OperatorModels.cs` -- add `EvidenceHeader(string Name, string Value)`, `HttpExchange` (method, url, request headers, request body, nullable status code, reason phrase, response headers, response body) and `CloudEventRecord` (the ten `TopicMessage` facts plus `Data`); append `HttpExchange? Exchange = null` and `CloudEventRecord? Event = null` to `EvidenceRecord`, and `HttpExchange? Exchange = null` to `PaymentResult`, `PaymentCancellationResult` and `InspectionResult` -- optional trailing members so not one existing construction site changes. A null `StatusCode` on an exchange is how "no answer arrived" is stored.
- [x] `CoreBankDemo.DemoRunner/Application/Ports/JournalRedaction.cs` -- rename the file and class to `JournalText`, rename `Apply` to `Bound`, delete `SecretLikePattern` and the `Replace` call, keep `MaxLength = 8192` and the `…` suffix -- the class stops being about redaction and becomes about bounding. Update all 14 call sites listed in the Code Map.
- [x] `CoreBankDemo.DemoRunner/Infrastructure/HttpPaymentGateway.cs` -- capture an `HttpExchange` in `SubmitAsync`, `CancelAsync` and `SendInspectionAsync`: request method, absolute URL, request headers merged from `request.Headers` and `request.Content?.Headers` (so `Content-Type` is present), request body; then status code, reason phrase, response headers merged from `response.Headers` and `response.Content.Headers`, response body. Bound every body with `JournalText.Bound`. On the two catch arms return the exchange with the request and no response -- a request that got no answer is still evidence.
- [x] `CoreBankDemo.DemoRunner/Infrastructure/DaprOutcomeFeed.cs` -- add `internal static OutcomeEvent Parse(TopicMessage message)` that never returns null: it builds the `CloudEventRecord` from the message, delegates to the existing typed switch, and falls back to an event carrying envelope and raw data with a null `TransactionId` when the type is unrecognised, the JSON is malformed, or the id is missing or empty. Point `HandleAsync` at it and make its handler-error string tolerate a null id -- an event the console silently drops is the thing this story exists to stop.
- [x] `CoreBankDemo.DemoRunner/Application/Ports/IOutcomeFeed.cs` -- make `OutcomeEvent.TransactionId` nullable and add an optional trailing `CloudEventRecord? Envelope = null`; give the four `From` factories an envelope parameter -- the console must not claim an id it could not parse.
- [x] `CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs` -- in `OnOutcomeEventReceived`, guard on the id before any attribution: an event without one reaches no tracked row, no burst counter, no retired set and no unmatched buffer, and is recorded with a null `transactionId` and a summary that names the event type without claiming a transaction. Attach `result.Exchange` to the four HTTP evidence sites (`:858`, `:1107`, `:1134`, `:1966`) and the `CloudEventRecord` to the per-event site (`:2396`) -- and to no other site.
- [x] `CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs` -- truncate `EvidenceRowViewModel.Summary` at the first em dash; add an `EvidencePaneViewModel` carrying the header block, a left column with its title, an optional right column, and the raw copy text; render an exchange as request line / headers / blank / body and status line / headers / blank / body, an event as one full-width `EVENT` column of envelope then data, and a payload-less record as one line stating the action was not an HTTP exchange -- the columns are the story, and a truncated row is what makes the list readable from the back of a room.
- [x] `CoreBankDemo.DemoRunner/Terminal/MainWindow.cs` -- replace `_evidenceDetail` with a bordered container holding three read-only `TextView`s (header, request, response), give the container `LineStyle.Rounded` in preferred layout and `LineStyle.None` in compact by extending `ApplyResponsiveLayout`, set both scroll bars to `ScrollBarVisibilityMode.Auto` on the two column views, hide the right column and widen the left to full for an event record, apply the wrap toggle to both columns, move `ApplyAnnouncement`'s borrowed row to the container, and make `Copy` send the model's raw copy text -- pasting column art into a `.http` file is not a Postman experience.
- [x] `tests/CoreBankDemo.DemoRunner.Tests/**` -- rename `JournalRedactionTests` to `JournalTextTests` keeping the two bounding assertions and retiring the two redaction ones; rewrite the two `DaprOutcomeFeed` drop tests to assert a row is produced instead, constructing `TopicMessage` directly; add gateway tests for header and body capture including the omitted-key and no-answer cases; add presentation tests for every I/O matrix row; update the copy, wrap, selection and layout tests to the three-pane shape; keep the ≥90% line-coverage gate green.

**Acceptance Criteria:**
- Given a payment submitted with a generated key, when its record is selected, then the REQUEST column shows the `Idempotency-Key` header and the RESPONSE column shows a `transactionId` field, and both are on screen together without scrolling at HD.
- Given the same key is submitted twice, when the two records are compared, then their RESPONSE column text is byte-identical.
- Given a record whose kind is `Topology`, `Resource`, `Export`, `Fault`, `Burst` or `LoadTest`, when it is selected, then the pane shows its header block and one line stating the action was not an HTTP exchange, and renders no empty column.
- Given an event arrives whose type is not one of the four known constants, when it is handled, then an evidence row exists carrying the envelope and the raw data, no tracked payment changed, no burst counter moved, and the unmatched buffer did not grow.
- Given a `com.corebank.transaction.completed` event arrives with an empty `transactionId`, when it is handled, then the same holds and the row claims no transaction id.
- Given any evidence record is selected, when Copy is pressed, then the clipboard holds the raw request text followed by the raw response text, with no box-drawing characters and no re-indentation.
- Given a summary containing an em dash, when the evidence list renders, then the row shows only the text before it while the Details pane, status line and export show the whole summary.
- Given a body longer than 8192 characters, when it is stored, then it is truncated with a trailing `…` and no `[redacted]` appears anywhere in the record.
- Given the terminal is at the preferred layout, when Evidence renders, then the Details pane carries a rounded border on the same background as the workspace around it.

## Design Notes

The four call sites that map one record to one exchange are payment submit, payment cancel, outcome query and inspect. `LoadTest` is deliberately not among them: `OperatorConsoleController.cs:1794` writes one aggregate record for a Reset → Run → Wait → Assert → Investigate workflow whose drain phase polls every two seconds for up to five minutes. It is structurally a `Burst`, and it keeps its existing investigation detail.

Outcome-query and inspect send a bare `HttpRequestMessage` with no headers and no body, so their REQUEST column is a request line and nothing else. That is honest and it is what Postman would show; do not synthesise headers to fill the column.

`OutcomeEvent.TransactionId` becoming nullable inverts a documented decision — `DaprOutcomeFeedTests.cs:141` argues an event without an id "can neither resolve a row nor be honestly labelled unattributed". That argument still holds for *attribution*, which is why the guard keeps such an event out of every accounting path. What changed is that the row itself is now worth having: the console renders raw bytes, and an unrecognised event on `transaction-events` is among the more interesting things that can arrive mid-demo.

The Details pane needs no `ColorScheme` work. Every Evidence control inherits `BaseScheme` from the window and a `Border` inherits its view's `SchemeName`, so a border drawn on an unstyled container is already on the same surface as everything around it — FR-21 is satisfied by assigning nothing.

Column titles belong in the text, not in extra controls: making the first line of each `TextView` read `REQUEST` or `RESPONSE` satisfies the two-column requirement with three controls instead of five, and keeps the copy path reading one string per column.

## Verification

**Commands:**
- `dotnet build CoreBankDemo.UnitTests.slnf` -- expected: no errors, no new warnings (watch for nullable warnings from `TransactionId`).
- `dotnet test CoreBankDemo.UnitTests.slnf` -- expected: all tests pass.
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: both tiers pass and the ≥90% line-coverage gate holds.
- `grep -rn "JournalRedaction" --include=*.cs .` -- expected: no matches outside `bin`/`obj`.

**Manual checks (if no CLI):**
- Render Evidence through `RenderForTest`/`ResizeForTest` at the preferred layout and at the 80×24 floor; confirm the border appears and disappears with `_compactLayout` and that neither column is clipped away.

## Suggested Review Order

**What a record now holds**

- The two new payload carriers; everything else follows from their shape.
  [`OperatorModels.cs:356`](../../../CoreBankDemo.DemoRunner/Application/OperatorModels.cs#L356)

- The CloudEvent envelope: only what `TopicMessage` actually exposes.
  [`OperatorModels.cs:376`](../../../CoreBankDemo.DemoRunner/Application/OperatorModels.cs#L376)

**Capturing the exchange**

- Snapshots the message about to be sent, so an omitted key records no header.
  [`HttpPaymentGateway.cs:109`](../../../CoreBankDemo.DemoRunner/Infrastructure/HttpPaymentGateway.cs#L109)

- Never returns null: an event the console cannot read still gets a row.
  [`DaprOutcomeFeed.cs:363`](../../../CoreBankDemo.DemoRunner/Infrastructure/DaprOutcomeFeed.cs#L363)

- Parse sits inside the guard; one bad message must not fail the stream.
  [`DaprOutcomeFeed.cs:416`](../../../CoreBankDemo.DemoRunner/Infrastructure/DaprOutcomeFeed.cs#L416)

- Unwraps protobuf kinds; `ToString()` would JSON-quote and throw on unset.
  [`DaprOutcomeFeed.cs:402`](../../../CoreBankDemo.DemoRunner/Infrastructure/DaprOutcomeFeed.cs#L402)

**Keeping an unreadable event out of the accounting**

- The id guard: no tracked row, no burst counter, no retired set.
  [`OperatorConsoleController.cs:2352`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L2352)

- Identity before the em dash, clauses after it, so the row survives truncation.
  [`OperatorConsoleController.cs:2960`](../../../CoreBankDemo.DemoRunner/Application/OperatorConsoleController.cs#L2960)

**Drawing it like Postman**

- One projection: header, columns, and the raw text Copy sends.
  [`PresentationModel.cs:776`](../../../CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs#L776)

- Envelope then data, one full-width column, no empty REQUEST beside it.
  [`PresentationModel.cs:892`](../../../CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs#L892)

- Cuts at the em dash but never to nothing.
  [`PresentationModel.cs:756`](../../../CoreBankDemo.DemoRunner/Terminal/PresentationModel.cs#L756)

- Offsets the pane by the border's thickness so it still lines up with the list.
  [`MainWindow.cs:1765`](../../../CoreBankDemo.DemoRunner/Terminal/MainWindow.cs#L1765)

- Reflows only when the second column actually appears or goes.
  [`MainWindow.cs:1783`](../../../CoreBankDemo.DemoRunner/Terminal/MainWindow.cs#L1783)

**Bounding, now that nothing is redacted**

- Bodies at 8192; headers capped in count and value length too.
  [`JournalText.cs:40`](../../../CoreBankDemo.DemoRunner/Application/Ports/JournalText.cs#L40)

**Tests worth reading**

- The money shot: key on the request, id on the response, one record.
  [`HttpPaymentGatewayTests.cs:521`](../../../tests/CoreBankDemo.DemoRunner.Tests/Infrastructure/HttpPaymentGatewayTests.cs#L521)

- An unreadable event gets a row and moves no counter.
  [`OperatorConsoleControllerTests.cs:1646`](../../../tests/CoreBankDemo.DemoRunner.Tests/Application/OperatorConsoleControllerTests.cs#L1646)

- Two raw HTTP columns from one exchange.
  [`PresentationModelBuilderTests.cs:750`](../../../tests/CoreBankDemo.DemoRunner.Tests/Terminal/PresentationModelBuilderTests.cs#L750)

- The RESPONSE column comes back; mutation-verified, not assumed.
  [`MainWindowTests.cs:1383`](../../../tests/CoreBankDemo.DemoRunner.Tests/Terminal/MainWindowTests.cs#L1383)

- Real laid-out frames at two sizes, not arithmetic on constants.
  [`MainWindowTests.cs:1529`](../../../tests/CoreBankDemo.DemoRunner.Tests/Terminal/MainWindowTests.cs#L1529)
