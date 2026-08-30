# ADR-012: Three test tiers with an enforced logic-coverage gate

**Date:** 2026-08-29
**Status:** Accepted — tier 2 superseded in part by [ADR-016](ADR-016-postgresql-testcontainers-persistence-testing.md)
**Deciders:** Architecture team
**Supersedes:** Unenforced coverage expectations and ambiguous SQLite/PostgreSQL test claims
**Superseded in part by:** ADR-016 (2026-08-30) — tier 2 now runs on PostgreSQL Testcontainers, not SQLite in memory. The three-tier split and the enforced >=90% line-coverage gate below remain in force; every SQLite reference below is historical.

## Context

The demo depends on concurrency, idempotency, PostgreSQL locking, Redis coordination, and generated HTTP integration. Treating all tests as one tier either makes ordinary development require infrastructure or creates false confidence by pretending SQLite and mocks prove PostgreSQL/Redis semantics. The previous repository also had no locally enforced coverage threshold.

## Decision

Tests are separated into three explicit tiers:

1. Pure logic and adapter-contract tests use xUnit, AwesomeAssertions, and Moq. Infrastructure is represented by ports or in-memory HTTP handlers.
2. Repository/store behavior uses EF Core SQLite in-memory for provider-neutral LINQ, indexes, idempotency, and state transitions.
3. PostgreSQL-, Redis-, replicated-topology-, and end-to-end semantics run against real local infrastructure through Aspire and the k6 acceptance harness.

SQLite tests must not claim to prove PostgreSQL `SELECT ... FOR UPDATE` behavior. Provider-specific SQL is isolated in minimal repository methods and is verified at tier 3.

Plain `dotnet test CoreBankDemo.Rebuild.slnf` runs in VSTest mode and enforces at least 90% line coverage on logic projects through coverlet. Hosting-only wiring and generated sources may be excluded narrowly; application logic, adapters with classification logic, handlers, validators, repositories, processor code, and lock behavior may not be blanket-excluded.

## Implementation

- `tests/Directory.Build.props` centrally configures coverlet, the 90% line threshold, VSTest mode, and justified exclusions.
- `CoreBankDemo.Rebuild.slnf` is the rebuild gate until Epic 6 restores the full solution gate.
- Test projects under `tests/` own tier-1 and tier-2 suites.
- `CoreBankDemo.LoadTests`, `CoreBankDemo.LoadTestSupport`, and `k6/` own tier-3 acceptance checks.
- Generated Kiota compilation and adapter mapping tests remain in the PaymentsAPI unit-test project; generated source itself is excluded.
- The real Redis renewal proof from Story 6.2 is infrastructure-tagged and does not masquerade as a unit test.

## Consequences

### Positive
- Fast local unit tests remain infrastructure-free.
- Provider-specific guarantees are tested at the only tier capable of proving them.
- The coverage gate is active from an ordinary developer command, not merely CI convention.

### Negative / Trade-offs
- Full confidence requires Docker/Aspire and takes longer than the unit gate.
- Coverage percentage is not a substitute for the named pattern and invariant tests required by the PRD.

## Key takeaway

> Mocks prove logic, SQLite proves provider-neutral stores, and real Postgres/Redis plus k6 prove distributed behavior.
