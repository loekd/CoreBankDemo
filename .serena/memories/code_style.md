# Code Style and Conventions

## Language / Naming
- C# standard conventions: PascalCase for types/methods/properties, camelCase with underscore prefix for private fields (`_serviceProvider`)
- File-scoped namespaces (`namespace CoreBankDemo.Messaging.Outbox;`)
- XML doc comments on public abstract members and important classes

## Patterns
- Generic base classes for reuse: `OutboxProcessorBase<TMessage, TDbContext>`, `InboxProcessorBase<TMessage, TDbContext>`
- Abstract methods for domain-specific logic (Template Method pattern)
- Constants in `MessageConstants` class (nested static classes: `Status`, `Defaults`)
- Feature flags via `appsettings.json` `Features` section
- Options pattern (`IOptions<T>`) for configuration

## Error Handling
- Background services loop with `stoppingToken.IsCancellationRequested`
- Structured logging with `ILogger` and message templates
- OpenTelemetry `ActivitySource` for tracing in processors

## Project Organization
- Controllers in `/Controllers/`
- Inbox logic in `/Inbox/`
- Outbox logic in `/Outbox/`
- No magic strings — use `MessageConstants.Status.*` and `MessageConstants.Defaults.*`

## No special linting/formatting tools configured (standard .NET/Roslyn analyzers)
