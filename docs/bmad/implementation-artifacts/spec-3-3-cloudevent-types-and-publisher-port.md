---
title: 'Story 3.3: CloudEvent types and publisher port'
type: 'feature'
created: '2026-08-24'
status: 'done'
baseline_commit: 'f04b6e579398c61be14c6b827c0c653f96dfcc6e'
review_loop_iteration: 1
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-3-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Wire event shapes currently live once already (old `CoreBankDemo.ServiceDefaults/CloudEventTypes/`, deleted at epic start along with the rest of `ServiceDefaults`) but publishing itself was inlined directly against `DaprClient` inside `CoreBankAPI`'s `MessagingOutboxProcessor` — no port, not mockable, not epic-3-owned. AD-12 requires the wire shapes to live in exactly one place with byte-for-byte JSON fidelity; AD-6 requires `DaprClient` to be reached only through a fixed set of ports (`IDistributedLockService`, `IEventPublisher`, `TimeProvider`) (FR-15, FR-16).

**Approach:** Rebuild `CoreBankDemo.ServiceDefaults/CloudEventTypes/{Constants,BalanceUpdatedEvent,TransactionCompletedEvent,TransactionFailedEvent}.cs` as an exact copy of the legacy shapes (same namespace, same record property names/order/types) — snapshot-tested for byte-for-byte JSON fidelity per AD-12. Add a new `IEventPublisher` port, per epics.md's authoritative signature: `PublishAsync(type, source, subject, payload, traceParent, cancellationToken)`. Implement `DaprEventPublisher`, wrapping `DaprClient`, constructed against `MessagingOutboxProcessingOptions` (story 3.1 — already carries `PubSubName`/`TopicName` bound to `"pubsub"`/`"transaction-events"`) so pubsub/topic are DI-configured, not passed per-call. The adapter builds the same `cloudevent.type`/`cloudevent.source`/`cloudevent.subject`/`cloudevent.traceparent` metadata dictionary the legacy code built and calls `daprClient.PublishEventAsync(pubsubName, topicName, payload, metadata, cancellationToken)`.

## Boundaries & Constraints

**Always:** `IEventPublisher.PublishAsync`'s parameter list matches epics.md exactly: `(string type, string source, string subject, object payload, string? traceParent, CancellationToken cancellationToken = default)`; the three event records and `Constants` reproduce the legacy shapes exactly (verified by JSON snapshot tests using the same `System.Text.Json` defaults Dapr's SDK uses); `DaprEventPublisher` is the only place besides `DaprDistributedLockService` (story 3.2) that touches `DaprClient` (AD-6); metadata keys omit `cloudevent.traceparent` when `traceParent` is null/whitespace, mirroring the legacy null-check.

**Ask First:** Whether to also carry `cloudevent.id`/`cloudevent.tracestate` metadata (legacy set both — `cloudevent.id` from the outbox message's own `Id`, `tracestate` from `Activity.Current`/stored value). Epics.md's AC for this story names only `type/source/subject/traceparent` as the required metadata mapping to unit-test — resolved by NOT including `id`/`tracestate` params on the port for this story (the Messaging kernel already dedupes on its own composite key, not the Dapr envelope id, so this isn't a correctness gap); flagged in Spec Change Log as a deliberate scope decision, not silently dropped.

**Never:** Give `IEventPublisher.PublishAsync` a `pubsubName`/`topicName` parameter — those are DI-bound via `MessagingOutboxProcessingOptions`, per this story's approach, so the port stays call-site-simple; touch `IDistributedLockService`/`DaprDistributedLockService`/`NoOpDistributedLockService`/`CooperativeLockCancellation` (story 3.2, done) or `Extensions.cs`'s `AddServiceDefaults` wiring (story 3.4).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Publish succeeds | Valid type/source/subject/payload, `DaprClient.PublishEventAsync` succeeds | Task completes; metadata dict built with `cloudevent.type/source/subject` always present | N/A |
| traceParent supplied | Non-null, non-whitespace `traceParent` | `cloudevent.traceparent` key present in metadata with that value | N/A |
| traceParent omitted | `null` or whitespace `traceParent` | `cloudevent.traceparent` key absent from metadata entirely (not present-with-empty-value) | N/A |
| Dapr publish throws | `DaprClient.PublishEventAsync` throws | Exception propagates to the caller unchanged — `IEventPublisher` is a thin adapter, not a failure-swallowing boundary (unlike `IDistributedLockService`; the Messaging kernel's `IOutboxDeliveryStrategy` contract, not this port, owns failure classification per AD-11) | not caught here |
| BalanceUpdatedEvent JSON shape | Fixed known values for all 5 fields | Serializes to the exact byte-for-byte legacy JSON shape (property names/order/casing) | snapshot-tested |
| TransactionCompletedEvent JSON shape | Fixed known values for all 3 fields | Serializes to the exact byte-for-byte legacy JSON shape | snapshot-tested |
| TransactionFailedEvent JSON shape, ErrorReason present | Fixed values, non-null `ErrorReason` | Serializes with `errorReason` present | snapshot-tested |
| TransactionFailedEvent JSON shape, ErrorReason null | Fixed values, `ErrorReason = null` | Serializes with `errorReason` present as JSON `null` (default `System.Text.Json` behavior — records don't omit nulls unless configured to) | snapshot-tested |
| Constants values | N/A | `TransactionCompleted`/`TransactionFailed`/`BalanceUpdated` equal the exact legacy strings (`com.corebank.transaction.completed`/`.failed`, `com.corebank.account.balance.updated`) | pinned literal test |

</frozen-after-approval>

## Code Map

- New: `CoreBankDemo.ServiceDefaults/CloudEventTypes/Constants.cs`, `BalanceUpdatedEvent.cs`, `TransactionCompletedEvent.cs`, `TransactionFailedEvent.cs` — exact legacy shapes (see epic context §Legacy Behavioral Reference)
- New: `CoreBankDemo.ServiceDefaults/IEventPublisher.cs`, `DaprEventPublisher.cs`
- `tests/CoreBankDemo.ServiceDefaults.Tests/CloudEventTypes/` — new folder: JSON snapshot tests for the three records + `Constants` literal-value tests
- `tests/CoreBankDemo.ServiceDefaults.Tests/EventPublisher/` — new folder: `DaprEventPublisherTests` (mocked `DaprClient`, same direct-mock approach story 3.2 validated — `PublishEventAsync` is virtual/abstract on `DaprClient`)
- Not touched: `IDistributedLockService.cs`, `DaprDistributedLockService.cs`, `NoOpDistributedLockService.cs`, `CooperativeLockCancellation.cs` (story 3.2, done), `Extensions.cs` (story 3.4), `Configuration/*.cs` (story 3.1, done — `MessagingOutboxProcessingOptions` is consumed here, not modified)

## Tasks & Acceptance

**Execution:**
- [x] Tests first: JSON snapshot tests for the three event records + `Constants` literal tests, then `IEventPublisher`/`DaprEventPublisher` tests against a mocked `DaprClient`
- [x] `CloudEventTypes/{Constants,BalanceUpdatedEvent,TransactionCompletedEvent,TransactionFailedEvent}.cs` — exact legacy shapes (already present at baseline commit unchanged; verified byte-identical via `git diff` against `f04b6e5`, only tests were added)
- [x] `IEventPublisher.cs` (port) + `DaprEventPublisher.cs` (adapter, constructed against `IOptions<MessagingOutboxProcessingOptions>` + `DaprClient` + `ILogger`)

**Acceptance Criteria:**
- Given the frozen wire contracts, when the records are implemented, then they match the AD-12 shapes byte-for-byte in JSON serialization (snapshot tests)
- Given `IEventPublisher.PublishAsync(type, source, subject, payload, traceParent)`, when called, then it maps to Dapr `PublishEventAsync` with CloudEvent metadata (`cloudevent.type/source/subject/traceparent`) in the adapter — metadata mapping unit-tested against a mocked publish call
- Given a null/whitespace `traceParent`, when `PublishAsync` is called, then the `cloudevent.traceparent` metadata key is omitted entirely, not present-with-empty-value

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — expected: green, ServiceDefaults + Messaging both unaffected/passing
- `dotnet build CoreBankDemo.Messaging/CoreBankDemo.Messaging.csproj` — expected: green, no source changes needed (this story doesn't touch anything Messaging depends on, but re-confirming costs nothing and catches accidental scope creep)

## Spec Change Log

- 2026-08-24 (step-04): implemented `IEventPublisher`/`DaprEventPublisher` per plan, with `CloudEventTypes/*` and `Constants.cs` confirmed already present at the baseline commit with the exact legacy shapes (byte-identical `git diff` against `f04b6e5`) — only JSON snapshot/literal tests were added for them, no source changes. Verified independently (not just trusting the implementer's self-report): `git status`/`git diff --stat` matched the claimed file list exactly (only the two new source files, two new test folders, plus this spec and `sprint-status.yaml`); `dotnet build CoreBankDemo.Messaging/CoreBankDemo.Messaging.csproj` green with zero source changes; `dotnet test CoreBankDemo.Rebuild.slnf` green — 90/90 `ServiceDefaults.Tests` (19 new), 153/153 `Messaging.Tests`, 1/1 `CoreBankAPI.Tests`, 1/1 `PaymentsAPI.Tests`. Review (blind-hunter + edge-case-hunter + verification-gap, all model sonnet) found no convergent correctness bugs — unlike story 3.2, nothing here crashes, leaks, or corrupts state. Verification-gap independently re-checked every claim in the self-report (file diffs, byte-identical `CloudEventTypes` baseline, `DaprClient.PublishEventAsync<TData>` generic/abstract/virtual via reflection, build/test counts, coverage %, `Threshold=0`/TODO(story-3.4) override) and found zero discrepancies. Both blind-hunter and edge-case-hunter independently converged on one theme — `type`/`source`/`subject` get no null/empty validation before flowing into the CloudEvent metadata dictionary (only `traceParent` is checked, per the frozen spec) — deliberately not patched: `IEventPublisher` has no caller yet (dead code until story 3.4 wires DI and epics 4/5 add producers), the spec's contract is "thin pass-through, propagates unchanged" not "validates," and this is non-adversarial demo code. Also deferred to `deferred-work.md`: (a) the JSON snapshot tests' `DaprDefaults` mirroring the real `DaprClient`'s `JsonSerializerOptions` only via a one-time manual-reflection code comment, not an automated live assertion; (b) `PublishAsync` being non-`async` means a hypothetical synchronous throw from the Dapr SDK (vs. a faulted Task) would propagate synchronously — behaviorally identical for the expected inline-`await`-in-`try` caller pattern, untested either way. None of the deferred items are correctness bugs against the frozen I/O matrix.
