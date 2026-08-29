---
title: 'Story 6.5: OpenTelemetry business metrics'
type: 'feature'
created: '2026-08-29'
status: 'ready-for-dev'
baseline_commit: '8e55a6488619239b533d086995715fa8740b585f'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-6-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** CoreBankDemo exports framework metrics and distributed traces, but its payment, transaction, and durable-message outcomes are visible only by reading logs, traces, or database rows. Operators cannot answer how many payments were accepted, how transactions ended, whether messages are moving, or where Inbox/Outbox retries and terminal failures occur from OpenTelemetry metrics.

**Approach:** Add one shared, strongly named business-metrics abstraction backed by `System.Diagnostics.Metrics`, register its meter in the existing OpenTelemetry pipeline, and instrument authoritative outcome points. Keep business outcomes, transport attempts, and durable store transitions separate so retries and business rejections remain honest, and enforce a closed low-cardinality attribute vocabulary.

## Boundaries & Constraints

**Always:** Register the shared meter through `AddServiceDefaults`; define instrument names, descriptions, units, store names, message types, transports, and outcome values once; record only after the represented outcome is known; use `Counter<long>` for events and `Histogram<double>` with unit `ms` for queue duration; keep exception propagation and Inbox/Outbox state semantics unchanged; test measurements with `MeterListener`.

**Ask First:** Any metric attribute sourced from request or payload data; an observable gauge that queries a database; a new metrics backend, dashboard, alert, package, endpoint, or wire-contract change; changing existing trace/span names or propagation.

**Never:** Put transaction ids, idempotency keys, account numbers, trace/span ids, exception messages/types, URLs, lock names, arbitrary currencies, or other user-controlled/unbounded values in metric attributes; count a business rejection as an Inbox/transport failure; claim counters prove current queue depth or exactly-once physical delivery; swallow or translate an exception to make metric recording succeed.

## Metric Contract

| Instrument | Type / Unit | Required attributes | Recorded at |
|---|---|---|---|
| `corebankdemo.payment.intake` | Counter / `{payment}` | `outcome=stored|duplicate|validation_failed` | Payments intake after the handler outcome is known |
| `corebankdemo.transaction.intake` | Counter / `{transaction}` | `outcome=accepted|replayed|in_flight|transport_failed` | CoreBank intake after the handler outcome is known |
| `corebankdemo.transaction.processed` | Counter / `{transaction}` | `outcome=completed|business_rejected` | After the atomic ledger/inbox/event transaction commits |
| `corebankdemo.messaging.store.operations` | Counter / `{operation}` | `messaging.store.name`, `messaging.store.kind=inbox|outbox`, `outcome=added|duplicate|failed` | Owning persistence path after insert/dedupe outcome; before rethrow on failure |
| `corebankdemo.messaging.items.processed` | Counter / `{item}` | `messaging.store.name`, `messaging.store.kind`, `outcome=completed|retry_scheduled|terminal_failed|completion_persistence_failed|retry_persistence_failed` | Shared processor after the authoritative handler/delivery and persistence outcome |
| `corebankdemo.messaging.queue.duration` | Histogram / `ms` | `messaging.store.name`, `messaging.store.kind` | Once per claimed item when handling/delivery starts |
| `corebankdemo.messaging.deliveries` | Counter / `{delivery}` | `messaging.direction=sent|received`, `messaging.transport=http|dapr`, `messaging.message.type`, `outcome=succeeded|failed|duplicate|unknown` | Concrete HTTP/Dapr boundary after its attempt outcome is known |

`messaging.store.name` is one of `payments-outbox`, `corebank-inbox`, `corebank-outbox`, or `payments-inbox`. `messaging.message.type` is a closed set covering the transaction command plus the existing CloudEvent constants (`transaction-completed`, `transaction-failed`, `balance-updated`, `unknown`); it is never copied verbatim from an incoming CloudEvent.

Transport delivery counters are attempt metrics: a publish can succeed and later be repeated if completion persistence fails. Durable item counters describe store-state outcomes. Neither counter alone is an exactly-once business assertion.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Measurements | Error Handling |
|---|---|---|---|
| New payment | Valid request stored | Payment intake `stored`; payments-outbox store `added` | N/A |
| Duplicate payment | Existing idempotency key | Payment intake `duplicate`; payments-outbox store `duplicate` | No second `added` |
| Invalid payment | Model/handler validation failure | Payment intake `validation_failed` | No store metric when storage was never attempted |
| New transaction command | CoreBank Inbox insert succeeds | Transaction intake `accepted`; HTTP receive `succeeded`; corebank-inbox store `added` | N/A |
| Duplicate/in-flight transaction | Existing Inbox row | Intake outcome matches replay/in-flight; HTTP receive `duplicate` | No second `added` |
| Committed ledger success | Debit/credit + three events commit | Transaction processed `completed`; three corebank-outbox store `added` operations | Record only after commit |
| Committed business rejection | Failure response + failure event commit | Transaction processed `business_rejected`; Inbox processing remains `completed` | Never processing/transport `failed` |
| Handler/delivery throws | Retry remains below max | Item processed `retry_scheduled`; delivery `failed` where a transport was attempted | Original exception/state transition behavior preserved |
| Retry reaches max | Retry count becomes terminal | Item processed `terminal_failed` exactly once | No additional terminal count on no-op repeats |
| Completion save fails | Work succeeded, completion persistence throws | Item processed `completion_persistence_failed`; successful delivery remains visible | Do not emit `completed`; existing reclaim behavior remains |
| Retry save fails | Work failed, retry persistence throws | Item processed `retry_persistence_failed` | Do not claim retry was scheduled |
| Host cancellation | Cancellation during work | No failed/retry measurement solely because of cancellation | Existing cancellation propagation remains |
| Lock not acquired | Another replica owns partition | No item/queue measurement | Normal skip |
| Unknown CloudEvent | Unknown route/type | Dapr receive `unknown`; payments-inbox store behavior follows Story 5.5 | Incoming type is not used as a tag |
| Negative clock delta | Durable timestamp later than current `TimeProvider` | Queue duration records `0` ms | No negative histogram values |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.ServiceDefaults/BusinessMetrics.cs` (new) -- shared meter, instruments, closed attribute constants, and typed recording methods; no caller constructs free-form tags.
- `CoreBankDemo.ServiceDefaults/Extensions.cs`; `tests/CoreBankDemo.ServiceDefaults.Tests/Extensions/AddServiceDefaultsTests.cs`; new focused metrics tests -- register `BusinessMetrics.MeterName` in `WithMetrics`, register the recorder once, and prove meter/instrument metadata.
- `CoreBankDemo.Messaging/MessageRepositoryBase.cs`; `InboxProcessorBase.cs`; `OutboxProcessorBase.cs` and their tests -- repository-backed store operations, queue-duration, and authoritative processing-outcome hooks. Supply the stable store identity from concrete repositories/processors; do not infer it from CLR type names.
- `CoreBankDemo.PaymentsAPI/Handlers/PaymentStorageHandler.cs`, payment intake endpoint, future Story 5.4 forward strategy, and future Story 5.5 subscription endpoints -- payment outcomes plus concrete HTTP-send and Dapr-receive boundaries.
- `CoreBankDemo.CoreBankAPI/Controllers/TransactionsController.cs`; `Inbox/TransactionIntakeHandler.cs`; `Inbox/TransactionExecutionHandler.cs`; `Outbox/OutboxEventEnqueuer.cs`; `Outbox/DaprOutboxDeliveryStrategy.cs` or `ServiceDefaults/DaprEventPublisher.cs` -- concrete HTTP receive, transaction intake/commit outcomes, atomic CoreBank outbox additions, and Dapr send. Choose one Dapr send hook only, after publish completion, to avoid double counting.
- `tests/CoreBankDemo.{Messaging,PaymentsAPI,CoreBankAPI,ServiceDefaults}.Tests` -- `MeterListener` assertions at the smallest owning layer; preserve the >=90% line gate.

## Tasks & Acceptance

**Execution:**
- [ ] Add `BusinessMetrics` with the exact metric contract and typed low-cardinality recording API; unit-test instrument metadata and forbidden-tag absence.
- [ ] Register the meter and recorder through `AddServiceDefaults`; prove both APIs receive the same contract through existing composition roots.
- [ ] Instrument repository-backed store outcomes, including dedupe and failure without changing exception behavior; record CoreBank's directly enqueued outbox rows only after their enclosing transaction commits.
- [ ] Instrument Inbox/Outbox queue duration and processing outcomes at authoritative state-transition points, including persistence-failure distinctions and cancellation exclusions.
- [ ] Instrument payment intake, transaction intake, and committed transaction outcomes exactly once.
- [ ] Instrument concrete HTTP and Dapr send/receive attempts without duplicating measurements across adapters, strategies, handlers, and processors.
- [ ] Run focused metrics tests and the full rebuild solution gate.

**Acceptance Criteria:**
- Given service startup, when the OTel metrics provider is built, then `BusinessMetrics.MeterName` is subscribed and exported through the same endpoint/resource configuration as framework metrics.
- Given every row in the Metric Contract table, when its owning path reaches each listed outcome, then `MeterListener` observes exactly one measurement with only the required closed-set attributes.
- Given duplicate, retry, poison, completion-persistence-failure, retry-persistence-failure, cancellation, and lock-contention paths, when they run, then counters describe the authoritative result without double counting or success-shaped fallback.
- Given successful or rejected transaction execution, when its database transaction rolls back, then no `transaction.processed` measurement is emitted; after commit, exactly one `completed` or `business_rejected` measurement is emitted.
- Given arbitrary ids, account numbers, trace context, exception text, currency, URL, or incoming event type, when instrumented paths run, then none appear as metric attributes.
- Given the full rebuild solution, when tested, then all tests pass and every logic project retains at least 90% line coverage.

## Design Notes

The shared recorder should own `Meter` and instrument lifetime. Callers use semantic methods rather than constructing `TagList` directly; this makes the cardinality policy reviewable and prevents drift. A no-op recorder is unnecessary because `System.Diagnostics.Metrics` instruments are already cheap when no listener subscribes.

Store and processor metrics intentionally use durable store identity. `inbox`/`outbox` alone cannot distinguish the CoreBank command Inbox from the Payments event Inbox, and CLR type names are unstable telemetry contracts.

Do not implement a current-backlog gauge in this story. An asynchronous database query cannot safely run inside an observable-instrument callback, and transition counters cannot reconstruct exact current queue depth after restart. A later story may add sampled queue-depth/oldest-age collection with explicit database and polling semantics.

## Verification

**Commands:**
- `dotnet test tests/CoreBankDemo.ServiceDefaults.Tests/CoreBankDemo.ServiceDefaults.Tests.csproj`
- `dotnet test tests/CoreBankDemo.Messaging.Tests/CoreBankDemo.Messaging.Tests.csproj`
- `dotnet test tests/CoreBankDemo.CoreBankAPI.Tests/CoreBankDemo.CoreBankAPI.Tests.csproj`
- `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankDemo.PaymentsAPI.Tests.csproj`
- `dotnet test CoreBankDemo.Rebuild.slnf`

## Suggested Review Order

1. Metric names, descriptions, units, closed-set values, and registration in ServiceDefaults.
2. Shared repository and processor hooks, especially retry versus persistence-failure classification.
3. Transaction commit timing and business-rejection semantics.
4. Concrete HTTP/Dapr send/receive hooks for accidental double counting.
5. `MeterListener` tests proving exact counts and bounded tags.
