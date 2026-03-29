# AGENTS.md — CoreBankDemo

## Architecture

Five projects talk to each other in a fixed topology:

```
PaymentsAPI → (HTTP via Dev Proxy) → CoreBankAPI
CoreBankAPI → (Dapr pub/sub) → PaymentsAPI (events back)
```

- **PaymentsAPI** (`CoreBankDemo.PaymentsAPI`) — accepts payments, stores to Outbox, consumes transaction events via Inbox.
- **CoreBankAPI** (`CoreBankDemo.CoreBankAPI`) — processes transactions via Inbox, publishes domain events via Messaging Outbox.
- **AppHost** (`CoreBankDemo.AppHost`) — Aspire orchestration: Postgres, Redis, Jaeger, Dapr sidecars, optional Dev Proxy.
- **ServiceDefaults** (`CoreBankDemo.ServiceDefaults`) — shared OpenTelemetry, health checks, `IDistributedLockService`, processing options.
- **Messaging** (`CoreBankDemo.Messaging`) — base classes for Inbox/Outbox patterns, `MessageConstants`, `PartitionHelper`.

## Developer Commands

```bash
# Start everything (Aspire + all infrastructure)
cd CoreBankDemo.AppHost && dotnet run

# Run load tests (disposable Postgres + Redis, k6 container)
dotnet run --project CoreBankDemo.LoadTests

# Send sample requests
# Use demo-requests.http in the repo root
```

No manual Dapr, Redis, or Dev Proxy setup — Aspire manages all of it.

## Key Patterns

### Inbox / Outbox base classes
Always inherit from `InboxProcessorBase<TMessage, TDbContext>` and `OutboxProcessorBase<TMessage, TDbContext>` in `CoreBankDemo.Messaging`. Override `LockNamePrefix` and `ProcessMessageAsync`. See `CoreBankAPI/Inbox/InboxProcessor.cs` for a concrete example.

### Constants — never use magic strings
```csharp
// Status
MessageConstants.Status.Pending / Processing / Completed / Failed

// Defaults
MessageConstants.Defaults.MaxRetryCount   // 5
MessageConstants.Defaults.BatchSize       // 10
MessageConstants.Defaults.PollingInterval // 5 s
MessageConstants.Defaults.ProcessingTimeout // 5 min
```

### Partition assignment — always use the shared helper
```csharp
int partitionId = PartitionHelper.GetPartitionId(idempotencyKey, partitionCount);
```

### Database
Use `EnsureCreated()` only — no EF migrations, ever. CoreBankAPI seeds accounts on startup if the table is empty (`Program.cs → InitializeDatabaseWithSeedAccounts`).

### Tracing
- Register every new `ActivitySource` name in `AddServiceDefaults(serviceName, new[] { nameof(MyProcessor), ... })`.
- Persist `TraceParent`/`TraceState` on outbox/inbox rows and restore them when processing to maintain the distributed trace chain.
- Use `ActivitySource.StartActivity(...)` for custom spans; never swallow or break the trace.

### Time
Always inject and use `TimeProvider` (registered as `TimeProvider.System`). Never call `DateTime.Now` or `DateTimeOffset.UtcNow` directly.

### Feature flags
- `Features:UseDapr` — switches PaymentsAPI between `DaprCoreBankApiClient` and `HttpCoreBankApiClient`.
- `Features:UseDevProxy` — AppHost conditionally starts Dev Proxy and forces `UseDapr=false` because Dapr bypasses the proxy.

### HTTP vs business logic
Controller actions must stay thin. Business logic lives in handler/executor classes that return domain types (`Task`, `Task<T>`), not `IActionResult`.

### Validation responses
Return all validation errors in one response: `return BadRequest(new { Errors = errors });`

## Critical Files

| File | Purpose |
|------|---------|
| `CoreBankDemo.Messaging/MessageConstants.cs` | All status strings and default values |
| `CoreBankDemo.Messaging/PartitionHelper.cs` | FNV-1a partition hashing |
| `CoreBankDemo.Messaging/Inbox/InboxProcessorBase.cs` | Base inbox background service |
| `CoreBankDemo.Messaging/Outbox/OutboxProcessorBase.cs` | Base outbox background service |
| `CoreBankDemo.ServiceDefaults/Extensions.cs` | `AddServiceDefaults`, OTEL config, lock service wiring |
| `CoreBankDemo.AppHost/AppHost.cs` | Full infrastructure topology |
| `CoreBankDemo.CoreBankAPI/Inbox/InboxProcessor.cs` | Reference implementation of inbox pattern |
| `CoreBankDemo.PaymentsAPI/Outbox/OutboxProcessor.cs` | Reference implementation of outbox pattern |
