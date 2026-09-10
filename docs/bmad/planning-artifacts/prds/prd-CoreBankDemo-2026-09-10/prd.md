---
title: "PRD: Evidence payload bodies"
status: draft
created: 2026-09-10
updated: 2026-09-10
---

# PRD: Evidence payload bodies

## Context

The Evidence workspace is where a DemoRunner session's durable record lives. Today an
`EvidenceRecord` carries `Method`, `Target`, `StatusCode`, `Duration`, `FaultLevels`,
`TransactionId` and one free-text `Detail` string. `Detail` sometimes contains a response body;
`PresentationModel.FormatBody` finds the first `{` or `[` in it and pretty-prints in place.

Three things are missing, and each one blocks a beat of the live demo:

1. **Outbound requests are never captured.** `HttpPaymentGateway` builds the payload and sets
   `Idempotency-Key`, then discards both — only the response body returns in `PaymentResult`.
2. **CloudEvents are parsed and thrown away.** `DaprOutcomeFeed.TryParse` deserializes into typed
   wire records; `message.Type` and `message.Data.Span` never leave `HandleAsync`.
3. **The one line the audience must read is redacted.** `JournalRedaction` replaces
   `idempotency-key\s*[:=]\s*…` with `[redacted]`.

## Goals

- On stage, point at `Idempotency-Key` in a request and at `transactionId` in that same record's
  response, and let the audience see they are the same value — without narration doing the work.
- Submit the same key twice, put the two records side by side, and show the response bytes are
  identical.
- Show a `transaction-events` CloudEvent as it was delivered — envelope and data — when
  demonstrating message-based interaction.
- Read like a tool the audience already knows: VS Code's `.http` extension, Postman.

## Non-goals

- The console does not explain what a payload means. It shows bytes; the presenter narrates.
  No diffing, no highlighting of "the matching field", no annotations.
- No new workspace, no new keystroke, no new banking-service surface.
- Burst payments stay row-less. A burst's outcomes are counted, not followed — the existing
  design decision stands, and 500 burst payments do not become 500 payload-carrying records.

## Users

Loek, presenting, with an audience watching the projector. The audience's prior is Postman and
`.http` files; the screen should meet that prior without a legend.

## Requirements

### F1 — Capture the request

- **FR-1.** Every evidence record produced by an outbound HTTP call carries that request as sent:
  method, absolute URL, the request headers the console set, and the request body bytes.
- **FR-2.** Headers are recorded verbatim, `Idempotency-Key` and `Content-Type` included. When the
  submission used `IdempotencyMode.Omitted`, no key header is recorded — the absence is the fact.
- **FR-3.** A record produced by a process invocation or a local file operation carries no request.
  It is not given an empty one.

### F2 — Capture the response

- **FR-4.** The response is captured alongside the request on the same record: status line, the
  response headers, and the body bytes. Headers are shown the way Postman shows them — a plain
  name/value list above the body — whenever the record is an HTTP exchange.
- **FR-5.** A call that never got an answer — timeout, dead connection — carries its request and no
  response. The pane states which, rather than rendering a blank column.

### F3 — Capture the CloudEvent

- **FR-6.** An `OutcomeEvent` record carries the CloudEvent as delivered: the envelope attributes
  `Dapr.Messaging`'s `TopicMessage` actually exposes — `Id`, `Source`, `Type`, `SpecVersion`,
  `DataContentType`, plus the Dapr routing facts `PubSubName`, `Topic` and `Path`, and anything in
  `Extensions` — and the `Data` payload bytes. `subject` and `time` are not surfaced by the SDK and
  are therefore not shown; an invented envelope line would be worse than a short one.
- **FR-7.** Held in memory only, inside the existing 500-record cap. Nothing new is written to
  disk; the existing Export action is the only writer, and it serializes `EvidenceRecord` directly,
  so new fields reach the export without further work.
- **FR-8.** An event whose type this console does not recognise gets a row too. `TryParse` returns
  null for an unknown type today and `HandleAsync` acks and drops it, so no record is ever created
  — defensible when the console only rendered parsed fields, wrong now that raw bytes are the
  point. An unrecognised event on `transaction-events` is among the more interesting things that
  can arrive mid-demo. It is recorded as an `OutcomeEvent` with its envelope and raw data, and no
  typed interpretation: the console must not claim a transaction id it could not parse, so such a
  row carries none and is attributed to nothing.

### F4 — Which records grow a payload

- **FR-9.** HTTP-exchange kinds carry request and response: `Payment`, `OutcomeQuery`,
  `Inspection`, `LoadTest`.
- **FR-10.** `OutcomeEvent` carries the CloudEvent, and neither a request nor a response.
- **FR-11.** `Topology`, `Resource`, `Export` and `Fault` carry neither. They are Aspire CLI
  invocations and local file writes, not HTTP exchanges. `Burst` rows are aggregates and carry
  neither.
- **FR-12.** For a record with no payload, the Details pane keeps its existing header block and
  states in one line that the action was not an HTTP exchange. An empty pane reads as a broken
  console; a stated absence reads as a fact.

### F5 — Render it like Postman

- **FR-13.** Under the existing header block, the Details pane shows two columns: `REQUEST` left,
  `RESPONSE` right.
- **FR-14.** Each column reads top to bottom as a raw HTTP exchange — request line or status line,
  then headers, then a blank line, then the body.
- **FR-15.** JSON bodies are pretty-printed for reading, as `FormatBody` does today. A body that is
  not JSON is shown verbatim rather than mangled into looking like it. A malformed or truncated
  body is shown as received.
- **FR-16.** An `OutcomeEvent` record renders one full-width `EVENT` column — envelope attributes,
  then data. It does not render an empty `REQUEST` column beside it.
- **FR-17.** Copy puts the raw request and response text on the clipboard, not the column layout,
  so it pastes into a `.http` file or Postman and runs.
- **FR-18.** Each column carries vertical and horizontal scroll bars in
  `ScrollBarVisibilityMode.Auto` — visible only when the payload is larger than the column. This
  replaces a narrow-terminal layout fallback: the console is presented at HD or better, and a
  scroll bar degrades gracefully where a layout switch would be a second thing to maintain.
- **FR-19.** The horizontal scroll bar also fixes existing behaviour. `_evidenceDetail` is
  `WordWrap = false` by default with a `Wrap: on/off` toggle, so a long JSON line runs off the
  right edge today with nothing on screen saying there is more.

### F6 — Make the pane readable

- **FR-20.** The Details pane gets a border, matching the nav rail's `LineStyle.Rounded` in the
  preferred layout and following the rail's compact-mode behaviour.
- **FR-21.** The pane keeps the same background surface as the rest of the workspace. The border
  and text contrast carry the separation; a different fill does not.

### F7 — Shorten the list rows

- **FR-22.** An evidence list row shows its summary truncated at the first em dash, with everything
  from the em dash onward dropped. 64 summaries in `OperatorConsoleController` carry an em dash and
  a trailing clause; the clause is what makes the list unscannable from the back of a room.
- **FR-23.** The full summary is unchanged on the record, in the Details pane, in the status line
  and in the export. Only the list row is shortened.
- **FR-24.** The inbound gutter marker (`< `) and the status glyph keep their columns. Truncation
  applies to the summary text only.

### F8 — Drop the redaction

- **FR-25.** `JournalRedaction`'s secret-pattern substitution is removed. Records store the bytes
  as sent and as received.
- **FR-26.** The 8192-character bound in the same class is kept. It is a different concern from
  redaction — it stops one large payload from taking the pane apart — and it matters more now that
  requests, responses and events are all retained. The class stops being about redaction and
  becomes about bounding, and should be renamed rather than left as an `Apply` that no longer
  redacts.

Accepted consequence: exported session JSON and the clipboard now carry raw headers and bodies.
The demo runs on synthetic accounts against a locally started topology, and the values in question
are console-generated correlation ids.

## Non-functional

- **NFR-1.** Memory: the 500-record cap now bounds request + response + event bytes rather than one
  detail string. With the 8 KB bound per field this is a low-megabytes ceiling for a payment demo.
- **NFR-2.** No banking service grows a surface for the console's benefit. Requests are captured at
  the console's own call sites; CloudEvents at its own subscription.
- **NFR-3.** No new keystroke and no new workspace. The change is to what records hold and how one
  pane draws.
- **NFR-4.** Existing behaviour that must survive: an arriving broadcast never steals the Details
  pane from a record being read; a trimmed selection falls back to the newest record; fault
  provenance stays on the record.
- **NFR-5.** `JournalRedactionTests` asserts the behaviour FR-25 removes, and has to go or be
  rewritten to cover bounding alone. `PresentationModelBuilderTests` and `MainWindowTests` cover
  the pane and rows.

## Open questions

None outstanding. Every question this draft raised was answered: response headers are in, the
length bound stays while the substitution goes, scroll bars replace a layout fallback, and unknown
event types get a row (FR-8). The decisions and their reasons are in `.memlog.md`.


## Success signals

Not metrics — this is a demo instrument. It works when, on stage:

- The `Idempotency-Key` header and the `transactionId` field are both visible without scrolling,
  on one record, at projector legibility.
- A resent key produces a second record whose response column is byte-identical to the first, and
  the audience can see that without being told.
- A settlement CloudEvent can be shown as delivered, immediately after the payment record that
  caused it.
- The evidence list can be read at a glance from the back of the room.
