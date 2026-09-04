# ADR-016: PostgreSQL Testcontainers for persistence testing

**Date:** 2026-08-30
**Status:** Accepted
**Deciders:** Architecture team
**Supersedes:** the SQLite-specific portions of [ADR-012](ADR-012-three-tier-testing-and-coverage-gate.md) and of AD-9 (architecture spine). ADR-012's three-tier strategy and its enforced ≥90% line-coverage gate remain in force.

## Context

Production runs on PostgreSQL, but the store test tier ran on SQLite in memory. A second relational engine cannot prove what this system actually depends on:

- PostgreSQL SQL translation and index/constraint behavior as EF Core emits it;
- `SELECT ... FOR UPDATE` row locking, which the ledger's balance conservation rests on;
- transaction and isolation behavior under genuinely competing connections;
- Npgsql SQLSTATE classification (`23505` unique violation vs. everything else);
- PostgreSQL data-type round trips (`numeric` money, `timestamp` precision, `varchar` truncation).

It also leaked into production: `UniqueViolation` carried a reflective, SQLite-shaped branch that existed purely to satisfy the test tier.

The devcontainer already provides Docker. Container cold start is acceptable **provided** the fast unit loop stays runnable without Docker and the two tiers are independently selectable.

## Decision

### 1. Three tiers, restated

1. **Tier 1 — unit.** Fast, Docker-free testing of domain and application logic, options, telemetry, and adapter orchestration through mocked or fake ports.
2. **Tier 2 — persistence integration.** PostgreSQL Testcontainers testing of EF Core models, repositories, durable stores, seeding, transactions, locking, ordering/claiming, and provider error semantics.
3. **Tier 3 — distributed acceptance.** Aspire + k6 against real PostgreSQL, Redis, Dapr, replicated topology, and fault injection.

SQLite is removed entirely. EF Core InMemory is **not** a substitute for either tier — no second relational engine and no provider-neutral double may stand in for PostgreSQL.

### 2. Independently runnable targets

| Target | Runtime dependency | Command |
|---|---|---|
| `CoreBankDemo.UnitTests.slnf` | .NET only | `dotnet test CoreBankDemo.UnitTests.slnf` |
| `CoreBankDemo.IntegrationTests.slnf` | Docker + pinned PostgreSQL image | `dotnet test CoreBankDemo.IntegrationTests.slnf` |
| `CoreBankDemo.Rebuild.slnf` | Docker + .NET | `dotnet test CoreBankDemo.Rebuild.slnf` |

The full rebuild gate runs both tiers. Tier 2 is never skipped when Docker is unavailable: the fixture fails with remediation context instead of reporting green.

### 3. Coverage

The combined line-coverage gate stays at **≥90%**, enforced by coverlet from a plain `dotnet test`, with no blanket exclusions.

Coverage is *partitioned by tier ownership*, not merged after the fact: `tests/Directory.Build.props` holds one `$(PersistenceTierFilters)` list naming the persistence adapters (EF contexts, repositories/durable stores, the demo seeder, and the hosted Inbox/Outbox processors). The unit projects exclude exactly that list; `tests/CoreBankDemo.Persistence.IntegrationTests` includes exactly that list. Every applicable type is therefore measured once, by the tier that can genuinely exercise it, and both tiers apply the same 90% threshold. Omitting a new adapter from the list leaves the unit tier measuring it, so the gate can only get stricter by accident, never weaker.

`AccountRepository.LockForUpdateAsync` loses its `[ExcludeFromCodeCoverage]`: it is now proved directly, on real competing connections.

### 4. Container topology

- One PostgreSQL container per test assembly, held by an xUnit v3 **assembly fixture** — never one container per test class or per test case.
- Each test method gets its own freshly created database inside that container, so classes run in parallel without shared state. Test parallelism is **not** globally disabled; isolation is the mechanism, because serializing the suite would hide shared-state bugs.
- Testcontainers' generated host port is always used. No fixed host port is ever bound, so a developer's own PostgreSQL on 5432, a running AppHost, and a test run cannot collide, and a cancelled run leaves no port residue.
- Container startup and every lock/contention wait are bounded (`PostgresContainerFixture.StartupTimeout`, `LockWaitTimeout`). Databases are dropped `WITH (FORCE)` on teardown, on green and red runs alike.
- Schema comes from the application's own `EnsureCreatedAsync` path. This story does **not** introduce EF migrations (constraints §3).

### 5. Pinned versions

| Item | Pin | Where |
|---|---|---|
| PostgreSQL image | `postgres:18.3` | `CoreBankDemo.AppHost/AppHost.cs` (`.WithImageTag("18.3")`) and `tests/CoreBankDemo.Persistence.IntegrationTests/Infrastructure/PostgresImage.cs` |
| `Testcontainers.PostgreSql` | 4.14.0 | `Directory.Packages.props` |
| `Testcontainers.XunitV3` | 4.14.0 | `Directory.Packages.props` |

The AppHost previously relied on Aspire 13.4.0's implicit default tag (`18.3`). That major is now selected explicitly and pinned in both places, so the two can never drift apart silently. Changing the PostgreSQL major version requires a new ADR.

### 6. Production cleanup

`UniqueViolation.IsUniqueViolation` recognizes duplicates through a typed `PostgresException.SqlState == PostgresErrorCodes.UniqueViolation` comparison only. The SQLite reflection branch is deleted. No exception is ever classified by message text, and any failure other than `23505` propagates unchanged.

Frozen, already-completed stories (Epics 1–5) keep their historical SQLite wording; they are evidence of what was done at the time and are not rewritten.

## Consequences

### Positive
- Provider-specific guarantees are proved by the only engine that can prove them, with real Npgsql exceptions, real row locks, and real competing connections.
- The inner loop stays fast and Docker-free; the persistence tier is a separate, explicit command.
- Production code no longer carries a test-only provider branch.
- `FOR UPDATE`, rollback atomicity, durable ordering, and concurrent seeding are covered before the k6 tier, so failures are diagnosed at unit-test granularity.

### Negative / Trade-offs
- The persistence tier requires Docker and pays a container cold start (amortized across the assembly, seconds in practice once the image is local).
- Coverage accounting is now a deliberate two-tier partition; adding a persistence adapter means deciding which tier owns it.

## Key takeaway

> Mocks prove logic, **real PostgreSQL proves persistence**, and Aspire + k6 prove distributed behavior. No second database engine exists in this repository.
