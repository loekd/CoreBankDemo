---
title: 'Story 4.7: Event publishing processor'
type: 'feature'
created: '2026-08-28'
status: 'done'
baseline_commit: '75d9651dbb6eb1ed5a8c2c29112e568db72a29d1'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-4-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Story 4.6 atomically enqueues CoreBank domain events, but no component publishes those rows. Downstream consumers therefore never receive transaction outcomes or balance updates.

**Approach:** Add a thin `MessagingOutboxProcessor` over the shared outbox kernel, a CoreBank outbox repository, and an `IOutboxDeliveryStrategy<MessagingOutboxMessage>` that maps each stored event to its frozen CloudEvent payload and publishes through `IEventPublisher`.

## Boundaries & Constraints

**Always:** Reuse `OutboxProcessorBase<MessagingOutboxMessage>` for polling, partition locking, claiming, completion, retries, poison handling, trace restoration, and logging. Configure four-partition processing from `MessagingOutboxProcessingOptions`; use lock prefix `messaging-outbox`. Publish with stored `EventType`, stored `EventSource`, `subject = TransactionId`, stored `TraceParent`, and the ambient cancellation token. Map only the three supported constants: `TransactionCompleted` to `TransactionCompletedEvent(TransactionId, TransactionStatus, ProcessedAt ?? CreatedAt)`, `TransactionFailed` to `TransactionFailedEvent(TransactionId, TransactionStatus, ProcessedAt ?? CreatedAt, ErrorReason)`, and `BalanceUpdated` to `BalanceUpdatedEvent(TransactionId, AccountNumber, Amount, NewBalance, Currency)`. An unsupported event type or missing balance must throw so the kernel owns retry classification. Register the concrete repository once and map `IOutboxMessageStore<MessagingOutboxMessage>` to that scoped instance; register the strategy and hosted processor.

**Ask First:** Any change to the `IEventPublisher` signature, CloudEvent payload records, stored outbox schema, event constants, pubsub/topic configuration, or shared messaging kernel.

**Never:** Call `DaprClient` directly outside `DaprEventPublisher`; duplicate or override polling, locking, claiming, retry, completion, or poison logic; swallow publish/mapping exceptions; publish inside the story 4.6 database transaction; modify stories 4.1–4.6 behavior or PaymentsAPI.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|---------------|----------------------------|----------------|
| Completed transaction | Pending `TransactionCompleted` row | Publish the exact completed payload with stored type/source, transaction subject, timestamp, and traceparent; kernel marks row completed | N/A |
| Failed transaction | Pending `TransactionFailed` row | Publish failure payload including nullable error reason; kernel marks row completed | N/A |
| Balance update | Pending `BalanceUpdated` row with `NewBalance` | Publish exact account/delta/new-balance/currency payload | Missing balance throws and kernel schedules retry |
| Transport failure | `IEventPublisher` throws | No success-shaped return or completion | Exception propagates from strategy; kernel increments retry and records error |
| Unknown event | Unsupported `EventType` | Nothing publishes | Throw; kernel retry path handles it |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxRepository.cs` -- new `OutboxMessageRepositoryBase<MessagingOutboxMessage, CoreBankDbContext>` adapter exposing `DbContext.MessagingOutboxMessages`.
- `CoreBankDemo.CoreBankAPI/Outbox/DaprOutboxDeliveryStrategy.cs` -- new event-type switch and `IEventPublisher.PublishAsync` adapter; use frozen records under `CoreBankDemo.ServiceDefaults/CloudEventTypes/`.
- `CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxProcessor.cs` -- new thin `OutboxProcessorBase<MessagingOutboxMessage>` subclass; mirror `Inbox/InboxProcessor.cs` option translation and add only `LockNamePrefix`.
- `CoreBankDemo.CoreBankAPI/Program.cs` -- repository/store, strategy, and hosted-service registrations; `AddDaprClient`, `AddServiceDefaults`, and `AddMessagingOutboxProcessingOptions` already provide publisher, tracing, and options.
- `CoreBankDemo.Messaging/OutboxProcessorBase.cs` and `IOutboxDeliveryStrategy.cs` -- read-only kernel and delivery contract; its existing tests already prove generic completion/retry behavior.
- `CoreBankDemo.ServiceDefaults/IEventPublisher.cs` and `DaprEventPublisher.cs` -- read-only publishing port/adapter.
- `tests/CoreBankDemo.CoreBankAPI.Tests/DaprOutboxDeliveryStrategyTests.cs` -- new exact mapping, metadata argument, cancellation, unsupported-type, missing-balance, and exception-propagation tests.
- `tests/CoreBankDemo.CoreBankAPI.Tests/MessagingOutboxProcessorTests.cs` -- new concrete wiring tests proving success completion and publish-failure retry through the kernel, plus reflection guard against loop overrides.

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxRepository.cs` -- add the CoreBank outbox repository/store adapter.
- [x] `CoreBankDemo.CoreBankAPI/Outbox/DaprOutboxDeliveryStrategy.cs` and `tests/CoreBankDemo.CoreBankAPI.Tests/DaprOutboxDeliveryStrategyTests.cs` -- implement and unit-test exact publishing behavior for every matrix row.
- [x] `CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxProcessor.cs` and `tests/CoreBankDemo.CoreBankAPI.Tests/MessagingOutboxProcessorTests.cs` -- add the thin kernel subclass and prove success completion, failure retry, and no custom loop overrides.
- [x] `CoreBankDemo.CoreBankAPI/Program.cs` -- register the repository/store mapping, strategy, and hosted processor.

**Acceptance Criteria:**
- Given a story 4.6 outbox row, when the processor claims it, then the correct frozen payload is published via `IEventPublisher` with stored type/source/traceparent and `subject = TransactionId`, and the kernel completes the row.
- Given mapping or transport failure, when delivery runs, then the strategy throws and the kernel alone applies retry state.
- Given the concrete processor type, when reviewed by reflection, then it overrides only `LockNamePrefix` and contains no custom loop methods.
- Given the full rebuild solution, when tested, then all tests pass and CoreBankAPI retains at least 90% line coverage.

## Spec Change Log

## Design Notes

`IEventPublisher` deliberately owns Dapr pubsub/topic configuration. The strategy owns only row-to-payload routing and forwards CloudEvent metadata arguments; this keeps transport infrastructure behind the established port.

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: full suite green and CoreBankAPI line coverage at least 90%.
- `git diff --stat HEAD` -- expected: changes limited to CoreBankAPI story 4.7 files, tests, wiring, and this spec.

## Suggested Review Order

**Publishing boundary**

- Start with the event switch defining exact payload and metadata forwarding.
  [`DaprOutboxDeliveryStrategy.cs:14`](../../../CoreBankDemo.CoreBankAPI/Outbox/DaprOutboxDeliveryStrategy.cs#L14)

- Confirm transport calls remain behind the established publisher port.
  [`DaprOutboxDeliveryStrategy.cs:35`](../../../CoreBankDemo.CoreBankAPI/Outbox/DaprOutboxDeliveryStrategy.cs#L35)

**Kernel integration**

- Review the thin processor translating validated options into kernel settings.
  [`MessagingOutboxProcessor.cs:9`](../../../CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxProcessor.cs#L9)

- Verify the repository exposes only CoreBank's existing outbox set.
  [`MessagingOutboxRepository.cs:6`](../../../CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxRepository.cs#L6)

- Check DI maps the store to one scoped repository instance.
  [`Program.cs:56`](../../../CoreBankDemo.CoreBankAPI/Program.cs#L56)

**Verification**

- Inspect exact payload, metadata, edge-case, and propagation coverage.
  [`DaprOutboxDeliveryStrategyTests.cs:16`](../../../tests/CoreBankDemo.CoreBankAPI.Tests/DaprOutboxDeliveryStrategyTests.cs#L16)

- Confirm successful delivery completes rows through the shared kernel.
  [`MessagingOutboxProcessorTests.cs:21`](../../../tests/CoreBankDemo.CoreBankAPI.Tests/MessagingOutboxProcessorTests.cs#L21)

- Confirm transport failure leaves retry classification to the kernel.
  [`MessagingOutboxProcessorTests.cs:53`](../../../tests/CoreBankDemo.CoreBankAPI.Tests/MessagingOutboxProcessorTests.cs#L53)

- End with the reflection guard preventing custom polling-loop behavior.
  [`MessagingOutboxProcessorTests.cs:81`](../../../tests/CoreBankDemo.CoreBankAPI.Tests/MessagingOutboxProcessorTests.cs#L81)
