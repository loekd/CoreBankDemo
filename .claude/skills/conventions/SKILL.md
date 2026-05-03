---
name: conventions
description: |
  Coding conventions for CoreBankDemo: database, time, HTTP/business logic separation, validation, and logging.
  
  **When to use:**
  - When writing or reviewing code for CoreBankDemo to ensure consistency in database usage, time handling, controller/business logic separation, validation, and logging.
  - When you need to check or enforce project-wide coding standards.
  
  **When NOT to use:**
  - Do NOT use for framework-agnostic or non-CoreBankDemo projects.
  - Do NOT use for runtime troubleshooting or debugging—use the relevant skills for those tasks.
---
---

## Code Navigation and Editing

Use **Serena** tools for all symbol-based work. Prefer them over `grep`/`glob`/`bash` for:

- Finding symbols: `serena-find_symbol`, `serena-get_symbols_overview`
- Finding references and implementations: `serena-find_referencing_symbols`, `serena-find_implementations`
- Editing: `serena-replace_symbol_body`, `serena-insert_before_symbol`, `serena-insert_after_symbol`, `serena-rename_symbol`
- Diagnostics: `serena-get_diagnostics_for_file`

Fall back to `grep`/`glob` only for non-code files (YAML, JSON, `.http`, etc.) or pattern matching that isn't symbol-based.

Always call `serena-check_onboarding_performed` at the start of a coding session.

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
