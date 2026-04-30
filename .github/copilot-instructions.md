# Copilot Instructions for CoreBankDemo

Mission-critical banking demo for a conference talk. Shows resilient, observable, exactly-once payment processing using .NET 10, Aspire, Dapr, and PostgreSQL.

## Projects

- **PaymentsAPI** — accepts payments; Outbox for reliable forwarding, Inbox for event consumption
- **CoreBankAPI** — processes transactions; Inbox for idempotent handling, Messaging Outbox for domain events
- **AppHost** — Aspire orchestration: Postgres, Redis, Jaeger, Dapr sidecars, optional Dev Proxy for fault injection
- **ServiceDefaults** — shared OpenTelemetry, health checks, distributed locking
- **Messaging** — Inbox/Outbox base classes, MessageConstants, PartitionHelper

## Design Patterns

Uses Inbox/Outbox with partitioned ordering, distributed locking, exactly-once processing, and end-to-end distributed tracing.

Skills are defined in `.claude/skills/`. The sections below are the canonical content of each skill.

---

## Skill: aspire-launch

Start and stop CoreBankDemo AppHosts using the Aspire CLI.

| AppHost | Project path |
|---|---|
| Regular dev | `CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj` |
| Load testing | `CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj` |

### Start

```bash
aspire start --apphost <project-path> --no-build --non-interactive
```

Use `--isolated` in worktrees or shared environments.

### Stop

```bash
aspire stop --apphost <project-path> --non-interactive
```

### Rules

- Never use `dotnet run` to start AppHosts — use `aspire start`.
- Always specify `--apphost` — this repo has two AppHosts.
- Use `aspire wait <resource>` before probing APIs or running assertions.

---

## Skill: aspire-mcp

Use Aspire MCP tools to inspect resource state, logs, and traces. Requires an AppHost to be running (see aspire-launch).

### CLI inspection

```bash
aspire ps --non-interactive
aspire describe --non-interactive
aspire logs <resource> --non-interactive
aspire otel logs <resource> --non-interactive
aspire otel traces <resource> --non-interactive
aspire mcp tools --non-interactive
aspire mcp call <resource> <tool> --input '{}' --non-interactive
```

### Resource restart map

| Change | Restart |
|---|---|
| `CoreBankDemo.PaymentsAPI/*` | `payments-api` |
| `CoreBankDemo.CoreBankAPI/*` | `corebank-api` |
| `CoreBankDemo.LoadTestSupport/*` | `loadtest-support` |
| `CoreBankDemo.AppHost/AppHost.cs` or Dapr components | full AppHost |

---

## Skill: conventions

Coding conventions for CoreBankDemo.

### Database

Use `EnsureCreated()` only — no EF migrations, ever. Aspire recreates the database from scratch when needed.

### Time

Inject and use `TimeProvider` (registered as `TimeProvider.System`). Never call `DateTime.Now` or `DateTimeOffset.UtcNow` directly.

### HTTP vs business logic

Controller actions must be thin. Business logic lives in handler/executor classes returning domain types (`Task`, `Task<T>`), not `IActionResult`.

### Validation

Return all validation errors in a single response:

```csharp
return BadRequest(new { Errors = errors });
```

### Feature flags

- `Features:UseDapr` — switches PaymentsAPI between `DaprCoreBankApiClient` and `HttpCoreBankApiClient`
- `Features:UseDevProxy` — AppHost conditionally starts Dev Proxy; forces `UseDapr=false` (Dapr bypasses the proxy)

### Logging

Use `ILogger<T>` with structured logging. Include correlation identifiers (e.g. `IdempotencyKey`, `PartitionId`) in log scopes.

---

## Skill: messaging-patterns

Inbox/Outbox implementation rules, MessageConstants usage, and partition assignment.

### Inbox and Outbox processors

Always inherit from the base classes in `CoreBankDemo.Messaging`:
- `InboxProcessorBase<TMessage, TDbContext>`
- `OutboxProcessorBase<TMessage, TDbContext>`

Override `LockNamePrefix` and `ProcessMessageAsync`. Reference implementation: `CoreBankAPI/Inbox/InboxProcessor.cs`.

Never bypass the base classes and reimplement polling or locking logic.

### MessageConstants — no magic strings

```csharp
MessageConstants.Status.Pending / Processing / Completed / Failed

MessageConstants.Defaults.MaxRetryCount       // 5
MessageConstants.Defaults.BatchSize           // 10
MessageConstants.Defaults.PollingInterval     // 5 s
MessageConstants.Defaults.ProcessingTimeout  // 5 min
```

### Partition assignment

```csharp
int partitionId = PartitionHelper.GetPartitionId(idempotencyKey, partitionCount);
```

Always use `PartitionHelper` — never write a second implementation.

### Key files

| File | Purpose |
|---|---|
| `CoreBankDemo.Messaging/MessageConstants.cs` | All status strings and defaults |
| `CoreBankDemo.Messaging/PartitionHelper.cs` | FNV-1a partition hashing |
| `CoreBankDemo.Messaging/Inbox/InboxProcessorBase.cs` | Base inbox service |
| `CoreBankDemo.Messaging/Outbox/OutboxProcessorBase.cs` | Base outbox service |
| `CoreBankDemo.CoreBankAPI/Inbox/InboxProcessor.cs` | Reference inbox implementation |
| `CoreBankDemo.PaymentsAPI/Outbox/OutboxProcessor.cs` | Reference outbox implementation |

---

## Skill: observability

OpenTelemetry tracing rules: ActivitySource registration, span creation, and trace context propagation.

### Register ActivitySource

Add every new `ActivitySource` name when calling `AddServiceDefaults`:

```csharp
builder.AddServiceDefaults(serviceName, new[] { nameof(MyProcessor), ... });
```

### Custom spans

```csharp
using var activity = _activitySource.StartActivity("OperationName");
activity?.SetTag("key", value);
```

### Trace context propagation

Persist `TraceParent` and `TraceState` on outbox/inbox rows when the message is created. Restore them when processing begins to re-attach to the originating trace.

Never swallow or break the trace chain — every background processor must propagate context to its children.

### Key file

`CoreBankDemo.ServiceDefaults/Extensions.cs` — `AddServiceDefaults`, OTEL pipeline configuration.

---

## Skill: load-test

Run a full CoreBankDemo load test, wait for drain, and assert results via the LoadTestSupport API.

### 1. Start the load-test AppHost

```bash
aspire start --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --no-build --non-interactive
aspire wait loadtest-support --non-interactive
```

### 2. Reset state

```
POST /reset
```

Truncates all inbox/outbox tables and resets the 10 test accounts to 10,000,000 each.

### 3. Wait for drain

Poll every 2–5 seconds until `isDrained == true`:

```
GET /assert/drain
```

```json
{ "isDrained": true, "outboxPending": 0, "inboxPending": 0, "completed": 1000, "failed": 0 }
```

### 4. Assert results

```
GET /assert/results?expectedUnique=1000
```

Assert `allPassed == true`. Inspect individual checks and their `detail` field on failure.

### 5. Stop

```bash
aspire stop --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --non-interactive
```
