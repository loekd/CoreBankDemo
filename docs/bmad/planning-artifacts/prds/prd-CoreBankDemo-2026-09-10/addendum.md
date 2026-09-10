# Addendum — Evidence payload bodies

Depth that belongs to architecture and UX rather than to the PRD. Nothing here is a requirement.

## Where the request bytes have to come from

The console has four HTTP call sites, and none of them return what was sent:

| Call site | Today returns | Needs |
| --- | --- | --- |
| `HttpPaymentGateway.SubmitAsync` | `PaymentResult` (response body only) | request payload + `Idempotency-Key` / `Content-Type` headers |
| `HttpPaymentGateway.CancelAsync` | `PaymentCancellationResult` | request payload |
| Inspection / outcome-query calls | body text | URL + request headers |
| `LoadWorkflowRunner` (has an `HttpClient`) | phase detail | per-phase request/response |

The obvious shape is one `HttpExchange` record — request line, headers, body, then status line,
headers, body — produced once at the call site and carried home on each result type, then hung on
`EvidenceRecord` beside `Detail`. That keeps `Detail` as the console's own prose and stops the two
from being conflated, which is what makes `FormatBody`'s "find the first brace" heuristic necessary
today. If the exchange is a first-class field, that heuristic can stop guessing.

Note `HttpPaymentGateway` already holds `BodyExcerptLength = 200` for its error prose. That is a
separate concern from the retained payload and should not be reused as the payload's bound.

## Where the CloudEvent bytes have to come from

`DaprOutcomeFeed.HandleAsync` holds the whole `TopicMessage` — `message.Type` and
`message.Data.Span` — and hands `TryParse` only what it needs. `OutcomeEvent` needs to carry the
raw JSON plus the envelope.

Verified against Dapr.Messaging 1.18.5: `TopicMessage` exposes `Data`, `DataContentType`,
`Extensions`, `Id`, `Path`, `PubSubName`, `Source`, `SpecVersion`, `Topic` and `Type`. There is no
`subject` and no `time` — the CloudEvent's own `time` attribute is not projected onto the message
object, so if the demo ever needs an event clock it comes from the console's own receive timestamp
(which `EvidenceRecord.Timestamp` already carries) or from a field inside `Data`, and the two must
not be presented as the same thing.

`TryParse` returning null drops unknown event types entirely, before any record exists — so FR-8 is
a capture change in `HandleAsync`, not a rendering one. It also has to stay clear of the accounting:
`_burstTransactions`, `_retiredTransactions` and the tracked-payment resolution all key off a parsed
transaction id, and a row that has none must reach none of them.

## Rendering the two columns

Two options, and they differ on copy rather than on looks:

- **Two `TextView`s side by side.** Independent scroll, and `Copy` can take the focused one's raw
  text, which satisfies FR-17 for free. Costs a second control, a focus rule, and a decision about
  what `Copy` does when neither has focus.
- **One `TextView` holding a composed two-column block.** One control, no focus rule, and the
  existing `_evidenceDetail.Text` copy path keeps working — but it copies the column art, which
  fails FR-17. Recovering the raw text would mean keeping a shadow string beside the displayed one.

The first is smaller in behaviour even though it is larger in controls. `_evidenceDetail` is
already a bare `TextView` with `ReadOnly = true` and a `Wrap: on/off` toggle, so whichever way it
goes, the wrap toggle has to apply to both columns or be reconsidered.

## Layouts considered and rejected

- **Stacked `REQUEST` then `RESPONSE` sections.** The smallest possible change — one string builder,
  no new control. Rejected: on a projector the response falls below the fold, so the
  key-equals-transaction-id comparison needs a scroll mid-sentence.
- **Tabs (Summary / Request / Response).** Least scrolling, but the audience only ever sees one
  panel, and the presenter is pressing keys while talking.
- **A narrow-terminal fallback to stacked sections.** Proposed, then dropped: the console is
  presented at HD or better, and scroll bars cover the degradation without a second layout to
  maintain.

Side-by-side was chosen knowing that the retry comparison is between *two records*, not between a
request and its own response — so the columns are not what serves that beat. They serve the
key-equals-id beat, which is the one that happens on a single record.

## Redaction alternatives considered

- **Allowlist header names** — show a known-safe set verbatim, redact the rest. Rejected as a
  rewrite of a working component for a threat that does not exist in a demo over synthetic data.
- **Keep redaction, show the key from the typed field** — the header block would render
  `[redacted]` in exactly the spot the presenter is pointing at.
- **Remove `idempotency-key` from the pattern only** — offered, and declined in favour of removing
  the substitution entirely.

The length bound and the substitution live in the same class and the same method. Keeping one and
dropping the other means the class stops being about redaction and becomes about bounding; it is
worth renaming rather than leaving a `JournalRedaction.Apply` that no longer redacts.

## Scroll bars — the API, verified

Terminal.Gui 2.4.17 (`~/.nuget/packages/terminal.gui/2.4.17/lib/net10.0/Terminal.Gui.xml`):

- `View.VerticalScrollBar` and `View.HorizontalScrollBar` are lazy-loaded properties on every
  `View` — not created until accessed, so they cost nothing on panes that never use them.
- `ScrollBar.VisibilityMode` takes `Auto`, `Always`, `Manual` or `None`. `Auto` shows the bar only
  when `ScrollableContentSize` exceeds `VisibleContentSize`.
- `TextView` already participates: it ships `UpdateHorizontalScrollBarVisibility`.

So the requirement is roughly one line per pane, `VisibilityMode = Auto` on both axes.

The horizontal bar is doing more than accommodating columns. `_evidenceDetail` is constructed
`WordWrap = false` and the workspace offers a `Wrap: on/off` toggle, so today a long line simply
runs past the right edge with nothing indicating it. Whether the wrap toggle still earns its place
once a horizontal bar exists is worth asking, but it is not this PRD's question.
