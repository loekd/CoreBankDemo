# Copilot Instructions for CoreBankDemo

## General Principles

### Code Quality
- Most important: this is a demo project featuring mission critical code. Always review and create code from this point of view.
- Ask for permission before making code changes, unless they have minimal impact.
- Keep this document up to date.
- **Don't overdo refactoring** - Keep changes minimal and focused.
- **Separate HTTP logic from business logic** - Private methods should return business types (e.g., `Task`, `Task<T>`), not HTTP results (`IActionResult`)
- **Always use proper validation** - Use data annotations and ModelState validation
- **Return all validation errors at once** - Use `{ Errors: [...] }` format with all problems in a single response

## Project Overview

This is **demo code** for a conference talk on building resilient mission-critical banking systems. It uses .NET 10, .NET Aspire, Dapr, and PostgreSQL. Despite being a demo, all code should reflect **mission-critical quality** — it must be correct, resilient, and observable.

## Architecture

- **Payments API** — accepts payment requests, uses the Outbox pattern to reliably forward them. Also consumes events via Inbox pattern.
- **Core Bank API** — processes transactions using Inbox pattern for idempotent message handling. Publishes domain events via Messaging Outbox pattern.
- **Aspire AppHost** — orchestrates both APIs, Dev Proxy, PostgreSQL databases, and Jaeger.
- **ServiceDefaults** — shared library for OpenTelemetry, health checks, resilience configuration, and distributed locking.
- **Messaging Library** — shared base classes for Inbox/Outbox patterns with partitioning and retry logic.
- **Dapr** — used for pub/sub messaging and distributed locking (optional, can use HTTP client).

## Key Principles

1. **Mission-critical mindset.** Treat every code change as if it runs in production banking infrastructure. Correctness and reliability are non-negotiable.
2. **Concurrency is always a concern.** Multiple instances of both APIs run simultaneously. All data access, outbox/inbox processing, and state mutations must be safe under concurrent execution. Use distributed locks (Dapr) or database-level concurrency controls where needed.
3. **No database migrations.** PostgreSQL databases (`paymentsdb`, `corebankdb`) use `EnsureCreated()` — never EF migrations for this demo. Databases can be recreated via Aspire.
4. **End-to-end traceability with OpenTelemetry.** Every operation must be traceable across service boundaries. Propagate trace context through HTTP calls and Dapr pub/sub messages. Use `Activity` / `ActivitySource` for custom spans. Never swallow or break the trace chain.
5. **Readability over comments.** Favor clear naming, small methods, and a logical file/folder structure. Avoid excessive inline comments — the code should speak for itself.
6. **Minimize complexity.** Choose the simplest correct solution. Don't introduce abstractions, patterns, or dependencies unless they earn their keep.
7. **Ask before large refactors.** Before making changes that affect multiple files or alter the project structure, ask for permission first.

## Tech Stack & Conventions

- **.NET 10** with C# latest features (including extensions, primary constructors, etc.)
- **ASP.NET Core** minimal hosting with controllers
- **Entity Framework Core** with **PostgreSQL** — no migrations, `EnsureCreated()` only
- **.NET Aspire** for orchestration and service defaults
- **Dapr** for pub/sub and distributed locks (optional HTTP client available)
- **Dev Proxy** for chaos engineering (orchestrated by Aspire)
- **OpenTelemetry** via Aspire ServiceDefaults — traces exported to Jaeger
- **Standard Resilience Handler** (`AddStandardResilienceHandler`) for HTTP retry/circuit breaker

## Patterns in Use

- **Outbox Pattern** (Payments API) — persist messages to a local outbox table, then process via background processor with HTTP/Dapr client.
- **Inbox Pattern** (Both APIs) — deduplicate incoming messages using idempotency keys. Base classes in `CoreBankDemo.Messaging`.
- **Messaging Outbox Pattern** (Core Bank API) — publish domain events reliably via outbox pattern with Dapr pub/sub.
- **Message Ordering** — partition messages by idempotency key using FNV-1a hashing to preserve ordering guarantees per entity.
- **Distributed Locking** — Dapr lock store to coordinate background processors across instances.
- **Generic Base Classes** — `InboxProcessorBase`, `OutboxProcessorBase`, `InboxMessageRepositoryBase`, `OutboxMessageRepositoryBase` in `CoreBankDemo.Messaging` for reusable patterns.

## Shared Constants

All status values and configuration defaults are centralized in `CoreBankDemo.Messaging.MessageConstants`:

```csharp
// Status values
MessageConstants.Status.Pending
MessageConstants.Status.Processing
MessageConstants.Status.Completed
MessageConstants.Status.Failed

// Configuration defaults
MessageConstants.Defaults.MaxRetryCount        // 5 attempts
MessageConstants.Defaults.BatchSize            // 10 messages
MessageConstants.Defaults.ProcessingTimeout    // 5 minutes
MessageConstants.Defaults.PollingInterval      // 5 seconds
```

**Never use magic strings for status values.** Always reference the constants.

## When Writing Code

- Ensure all new HTTP endpoints or background processors propagate `Activity` context.
- When adding database operations, consider concurrent access from multiple instances.
- Keep controller actions thin — delegate business logic to dedicated handler/service classes.
- Use `ILogger<T>` for structured logging; include correlation identifiers where relevant.
- Register new `ActivitySource` names in the ServiceDefaults configuration so they are captured by the OTEL pipeline.
- Use `TimeProvider` (already registered) instead of `DateTime.Now` / `DateTimeOffset.UtcNow` for testability.
- **Always use `MessageConstants` instead of hardcoded status strings or retry counts.**
- When implementing inbox/outbox patterns, inherit from the base classes in `CoreBankDemo.Messaging`.
- Use `PartitionHelper.GetPartitionId()` for consistent partition assignment.

## When Writing Documentation

- Don't overdo it. Keep it concise.
- Don't use emojis or excessive formatting.
- Use mermaid diagrams for complex flows.
- Keep it simple and focused on key points.
- Technical details go in ARCHITECTURE.md, user-facing instructions in README.md.

## Project Structure

```
CoreBankDemo/
├── CoreBankDemo.Messaging/        # Shared inbox/outbox base classes
│   ├── MessageConstants.cs        # Status values and defaults
│   ├── PartitionHelper.cs         # FNV-1a hashing for partitions
│   ├── Inbox/                     # Base classes for inbox pattern
│   └── Outbox/                    # Base classes for outbox pattern
├── CoreBankDemo.ServiceDefaults/  # Aspire shared config
├── CoreBankDemo.PaymentsAPI/      # Payment service
├── CoreBankDemo.CoreBankAPI/      # Core banking service
└── CoreBankDemo.AppHost/          # Aspire orchestration
```

## Common Mistakes to Avoid

1. **Don't use hardcoded status strings** - Always use `MessageConstants.Status.*`
2. **Don't use hardcoded retry counts or timeouts** - Use `MessageConstants.Defaults.*`
3. **Don't create duplicate PartitionHelper implementations** - Use `CoreBankDemo.Messaging.PartitionHelper`
4. **Don't forget to propagate TraceParent/TraceState** - Required for distributed tracing
5. **Don't bypass the base classes** - Inherit from `InboxProcessorBase` and `OutboxProcessorBase` for consistency
6. **Don't add SQLite references** - This project uses PostgreSQL orchestrated by Aspire
7. **Don't manually start Dev Proxy** - It's orchestrated by Aspire automatically

## Testing Considerations

- Background processors poll every 5 seconds (configurable via `MessageConstants.Defaults.PollingInterval`)
- Partition count is configurable in appsettings (default: 4)
- Distributed locks expire after 30 seconds (configurable via `LockExpirySeconds`)
- Messages retry up to 5 times before failing (configurable via `MessageConstants.Defaults.MaxRetryCount`)
- Processing timeout for stale messages is 5 minutes (configurable via `MessageConstants.Defaults.ProcessingTimeout`)

## Load Testing

The project includes comprehensive load tests in `CoreBankDemo.LoadTests`:

**Run load tests:**
```bash
dotnet run --project CoreBankDemo.LoadTests
```

**What it validates:**
- Exactly-once processing under concurrent load (default: 1000 transactions, 10 VUs)
- Idempotency guarantees (~10% deliberate retry attempts)
- No message loss (all submitted transactions processed)
- No duplicate processing (each idempotency key processed once)
- No failed messages (error handling works correctly)

**Test infrastructure:**
- Disposable PostgreSQL and Redis instances
- k6 container for load generation
- LoadTestSupport API for assertions
- 10 seeded test accounts (NL01LOAD0000000001 → NL10LOAD0000000010)

See `CoreBankDemo.LoadTests/README.md` and ARCHITECTURE.md for details.
