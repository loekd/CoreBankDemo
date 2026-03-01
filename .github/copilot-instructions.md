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

This is **demo code** for a conference talk on building resilient mission-critical banking systems. It uses .NET 10, .NET Aspire, Dapr, and SQLite. Despite being a demo, all code should reflect **mission-critical quality** — it must be correct, resilient, and observable.

## Architecture

- **Payments API** — accepts payment requests, uses the Outbox pattern to reliably forward them via Dapr pub/sub.
- **Core Bank API** — processes transactions, uses the Inbox pattern for idempotent message handling.
- **Aspire AppHost** — orchestrates both APIs, Jaeger, Redis, and Dapr sidecars.
- **ServiceDefaults** — shared library for OpenTelemetry, health checks, and resilience configuration.
- **Dapr** — used for pub/sub messaging (Redis-backed) and distributed locking.

## Key Principles

1. **Mission-critical mindset.** Treat every code change as if it runs in production banking infrastructure. Correctness and reliability are non-negotiable.
2. **Concurrency is always a concern.** Multiple instances of both APIs run simultaneously. All data access, outbox/inbox processing, and state mutations must be safe under concurrent execution. Use distributed locks (Dapr) or database-level concurrency controls where needed.
3. **No database migrations.** SQLite databases (`payments.db`, `corebank.db`) are disposable. Use `EnsureCreated()` — never EF migrations. Databases can always be deleted and recreated.
4. **End-to-end traceability with OpenTelemetry.** Every operation must be traceable across service boundaries. Propagate trace context through HTTP calls and Dapr pub/sub messages. Use `Activity` / `ActivitySource` for custom spans. Never swallow or break the trace chain.
5. **Readability over comments.** Favor clear naming, small methods, and a logical file/folder structure. Avoid excessive inline comments — the code should speak for itself.
6. **Minimize complexity.** Choose the simplest correct solution. Don't introduce abstractions, patterns, or dependencies unless they earn their keep.
7. **Ask before large refactors.** Before making changes that affect multiple files or alter the project structure, ask for permission first.

## Tech Stack & Conventions

- **.NET 10** with C# latest features (including extensions, primary constructors, etc.)
- **ASP.NET Core** minimal hosting with controllers
- **Entity Framework Core** with **SQLite** — no migrations, `EnsureCreated()` only
- **.NET Aspire** for orchestration and service defaults
- **Dapr** for pub/sub (Redis) and distributed locks
- **OpenTelemetry** via Aspire ServiceDefaults — traces exported to Jaeger
- **Standard Resilience Handler** (`AddStandardResilienceHandler`) for HTTP retry/circuit breaker

## Patterns in Use

- **Outbox Pattern** (Payments API) — persist messages to a local outbox table, then publish via a background processor.
- **Inbox Pattern** (Core Bank API) — deduplicate incoming messages using idempotency keys.
- **Message Ordering** — partition messages by account to preserve ordering guarantees.
- **Distributed Locking** — Dapr lock store (Redis) to coordinate across instances.

## When Writing Code

- Ensure all new HTTP endpoints or background processors propagate `Activity` context.
- When adding database operations, consider concurrent access from multiple instances.
- Keep controller actions thin — delegate business logic to dedicated handler/service classes.
- Use `ILogger<T>` for structured logging; include correlation identifiers where relevant.
- Register new `ActivitySource` names in the ServiceDefaults configuration so they are captured by the OTEL pipeline.
- Use `TimeProvider` (already registered) instead of `DateTime.Now` / `DateTimeOffset.UtcNow` for testability.

