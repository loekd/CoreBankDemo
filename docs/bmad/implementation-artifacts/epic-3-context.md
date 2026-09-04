# Epic 3 Context: E2 — ServiceDefaults

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Rebuild `CoreBankDemo.ServiceDefaults` from scratch, test-first: validated processing options, a distributed-lock port with a Dapr implementation, the CloudEvent record types plus a publisher port, and a thin `AddServiceDefaults` wiring extension. The old sources are deleted at epic start (kept only where `CoreBankDemo.Messaging` already references them — see Legacy Behavioral Reference below). This closes out FR-20 (every pattern seam — locking, publishing, persistence, time — is unit-testable without infrastructure) and FR-23 (configuration matches documentation: partition count 4, no dead feature flags), fixing two known brownfield defects along the way: `PartitionCount` misconfigured to 2 instead of 4 (ruling A3), and a bound-but-unused `LockRenewIntervalSeconds` option (ruling A4).

## Stories

- Story 3.1: Validated processing options
- Story 3.2: Distributed lock port and Dapr implementation
- Story 3.3: CloudEvent types and publisher port
- Story 3.4: Service wiring defaults

## Requirements & Constraints

- Every pattern seam (locking, publishing, persistence, time) must be an interface usable with a mock — no infrastructure required to unit-test pattern logic.
- Configuration must match documentation exactly: `PartitionCount` = 4 everywhere it is bound/validated; no `Features:UseDapr` flag or other dead config anywhere in code or config files.
- Options binding fails fast: `AddOptions<T>().BindConfiguration(...).ValidateDataAnnotations().ValidateOnStart()`, with all violations reported together, not just the first.
- No options member may exist unless something reads it — enforce with a dead-option check against a known-consumers list.
- Coverage gate (line, 90%) applies to this project too: hosting-only members (DI wiring, endpoint mapping) get `[ExcludeFromCodeCoverage]`; option-binding/validation logic must be covered.
- All work targets the `CoreBankDemo.Rebuild.slnf` filter, not the full solution.

## Technical Decisions

- **Fixed port set (AD-6):** infrastructure is reached only through `IDistributedLockService`, `IEventPublisher` (wraps `DaprClient`), and `TimeProvider` in this epic's scope — no other coupling to Dapr/Redis leaks out of ServiceDefaults.
- **Locking is expiry-based, never renewed (AD-7):** partition locks rely on expiry plus cooperative cancellation near end-of-lifetime; no renewal mechanism exists or is wired. `LockRenewIntervalSeconds` must not exist as a member anywhere in the rebuild.
- **Trace propagation (AD-8):** `TraceParent`/`TraceState` propagate on the Dapr hop via CloudEvent metadata (`cloudevent.type`/`source`/`subject`/`traceparent`); `IEventPublisher.PublishAsync` takes a `traceParent` parameter explicitly. New spans follow the `observability` skill (registered `ActivitySource`, tags include `IdempotencyKey`/`PartitionId` where applicable).
- **One written owner for wire shapes (AD-12):** `TransactionCompletedEvent`, `TransactionFailedEvent`, `BalanceUpdatedEvent` live once in ServiceDefaults `CloudEventTypes` and must match the frozen shapes byte-for-byte in JSON (snapshot-testable) — see exact legacy shapes below, which are the preservation target absent an ADR.
- **Dependency direction:** `Messaging` → `ServiceDefaults`; `ServiceDefaults` never references an API project. Lock names follow `<prefix>-partition-<id>` (prefixes: `payments-outbox`, `payments-inbox`, `corebank-inbox`, `corebank-messaging-outbox`), never shared between stores.
- **CloudEvent type constants:** `com.corebank.transaction.completed`, `com.corebank.transaction.failed`, `com.corebank.account.balance.updated` — fixed by AD-1/AD-12, not open to change without an ADR.

## Legacy Behavioral Reference

Old `CoreBankDemo.ServiceDefaults` sources are deleted at epic start; this is what existed and must be preserved (with AD-7/A3/A4-driven fixes) or explicitly superseded.

**`IDistributedLockService.ExecuteWithLockAsync`** — exact current signature, which `CoreBankDemo.Messaging` (`InboxProcessorBase`, `OutboxProcessorBase`, already merged and tested) compiles against and calls today:
```csharp
Task<bool> ExecuteWithLockAsync(
    string lockName,
    int lockExpirySeconds,
    Func<CancellationToken, Task> workload,
    CancellationToken cancellationToken = default);
```
Callers pass `_options.LockExpirySeconds` and a partition workload lambda, and discard the `bool` result (`false` = lock not acquired = silent skip, not an error). **Story 3.2 must keep this external signature unchanged** — Messaging is already built and tested against it; changing it is epic 3's call to make explicitly (and update Messaging accordingly), never a silent break.

**`DaprDistributedLockService`** (old impl, for behavior reference — internal shape may change under 3.2's new AC of a disposable lock handle exposing its own `CancellationToken`): hardcoded lock store name `"lockstore"`; lock owner token `"{Environment.MachineName}-{Guid.NewGuid()}"`; calls the (obsolete-attributed) `daprClient.Lock(store, lockName, owner, lockExpirySeconds, ct)` then `daprClient.Unlock(...)` in a `finally`. Failed acquisition returns `false`, never throws. On success, derives a workload-scoped token that auto-cancels at **5/6 of `lockExpirySeconds`** (`CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)` + `CancelAfter`); an `OperationCanceledException` from that *work* token specifically (not the ambient token) is caught, logged, and turned into `false`. Every other exception anywhere in the method is caught at the top level, logged, and also turned into `false` — `ExecuteWithLockAsync` never throws.

**`NoOpDistributedLockService`**: same interface; `ExecuteWithLockAsync` always returns `Task.FromResult(false)` — no lock, no workload execution. Used for lock-free hosting (LoadTestSupport).

**`ProcessingOptionsBase` + Inbox/Outbox/MessagingOutbox variants** (DataAnnotations): `PartitionCount` `[Required][Range(1,100)]`, no default (currently misbound to 2 in config — ruling A3 says the rebuild must default/validate to 4); `LockExpirySeconds` `[Required][Range(1,300)]`; `LockRenewIntervalSeconds` `[Required][Range(1,240)]` — bound and validated but **never read anywhere** (ruling A4: dead, must not exist post-rebuild — Messaging's own `InboxProcessorOptions`/`OutboxProcessorOptions` already omit it by design); `PollingIntervalMs` `[Required][Range(100,300_000)]` default 5000. `InboxProcessingOptions`/`OutboxProcessingOptions` are empty marker subclasses (`SectionName` = `"InboxProcessing"`/`"OutboxProcessing"`). `MessagingOutboxProcessingOptions` adds `PubSubName` `[Required][MinLength(1)]` default `"pubsub"` and `TopicName` `[Required][MinLength(1)]` default `"transaction-events"`, `SectionName` = `"MessagingOutboxProcessing"`.

**`CloudEventTypes`** — already exist in old ServiceDefaults, not yet touched by any Messaging story (stories 2.1–2.6 reference no `CloudEventTypes` type or namespace): `Constants.TransactionCompleted/.TransactionFailed/.BalanceUpdated` (values above); records `BalanceUpdatedEvent(string TransactionId, string AccountNumber, decimal Delta, decimal NewBalance, string Currency)`, `TransactionCompletedEvent(string TransactionId, string Status, DateTimeOffset ProcessedAt)`, `TransactionFailedEvent(string TransactionId, string Status, DateTimeOffset ProcessedAt, string? ErrorReason)`. Epic 3 owns rebuilding these fresh; AD-12 requires byte-for-byte JSON shape preservation unless an ADR changes them.

**`AddServiceDefaults(serviceName[, additionalActivitySources])`** (current wiring, target shape for 3.4's DI-inspection AC): registers OTel logging+metrics+tracing (resource service name; AspNetCore/HttpClient/Grpc/Runtime instrumentation; OTLP exporter, endpoint overridable via `JAEGER_OTLP_ENDPOINT` else default env-based exporter), a `"self"` health check tagged `"live"`, service discovery, `AddStandardResilienceHandler` + service discovery on all typed `HttpClient`s, and `IDistributedLockService` as a singleton factory (`NoOpDistributedLockService` when no `DaprClient` is registered in DI, else `DaprDistributedLockService`) plus a singleton `ActivitySource(serviceName)`. `MapDefaultEndpoints` maps `/health` and `/alive` (tagged `"live"`) only under `Environment.IsDevelopment()`.

**Interface-compatibility note:** since `CoreBankDemo.Messaging` already compiles against `IDistributedLockService.ExecuteWithLockAsync`'s current signature, story 3.2 must either keep that exact signature (Messaging keeps compiling unmodified) or, if it changes, treat updating Messaging's call sites as this epic's own responsibility — never a silent break of the already-tested kernel.

## Cross-Story Dependencies

- Story 3.2 is a hard dependency for the rest of the epic and for `CoreBankDemo.Messaging`: `ExecuteWithLockAsync`'s external signature must stay compatible with Messaging's existing (merged, tested) callers in `InboxProcessorBase`/`OutboxProcessorBase`.
- Story 3.1's options rebuild has no consumer inside Messaging today — `InboxProcessorOptions`/`OutboxProcessorOptions` there are already locally-defined, decoupled records (story 2.4's deliberate choice) — so 3.1 only needs to satisfy the epic-4/epic-5 processors that will bind to these types later.
- Story 3.4 (`AddServiceDefaults`) depends on 3.1 (options registration) and 3.2 (lock service registration) landing first; `IEventPublisher` registration from 3.3 likely belongs in 3.4's wiring too even though not named explicitly in 3.4's acceptance criteria.
- Epics 4 and 5 (CoreBankAPI, PaymentsAPI) consume this epic's `IDistributedLockService`, `IEventPublisher`, and options types when they wire their own processors.
