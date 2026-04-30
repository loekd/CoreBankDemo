---
name: conventions
description: "Coding conventions for CoreBankDemo: database, time, HTTP/business logic separation, and validation."
---

## Database

Use `EnsureCreated()` only — no EF migrations, ever. Aspire recreates the database from scratch when needed.

## Time

Inject and use `TimeProvider` (registered as `TimeProvider.System`). Never call `DateTime.Now` or `DateTimeOffset.UtcNow` directly.

## HTTP vs business logic

Controller actions must be thin. Business logic lives in handler/executor classes returning domain types (`Task`, `Task<T>`), not `IActionResult`.

## Validation

Return all validation errors in a single response:

```csharp
return BadRequest(new { Errors = errors });
```

## Feature flags

- `Features:UseDapr` — switches PaymentsAPI between `DaprCoreBankApiClient` and `HttpCoreBankApiClient`
- `Features:UseDevProxy` — AppHost conditionally starts Dev Proxy; forces `UseDapr=false` (Dapr bypasses the proxy)

## Logging

Use `ILogger<T>` with structured logging. Include correlation identifiers (e.g. `IdempotencyKey`, `PartitionId`) in log scopes.
