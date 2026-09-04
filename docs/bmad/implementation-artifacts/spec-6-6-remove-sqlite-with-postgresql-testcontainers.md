---
title: 'Story 6.6: Remove SQLite with PostgreSQL Testcontainers'
type: 'refactor'
created: '2026-08-29'
status: 'done'
baseline_commit: 'd6d3b4c37853b9b3b9c845ad147bef25f24449f3'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/constraints.md'
  - '{project-root}/docs/bmad/planning-artifacts/architecture/architecture-CoreBankDemo-2026-08-21/ARCHITECTURE-SPINE.md'
  - '{project-root}/docs/adr/ADR-012-three-tier-testing-and-coverage-gate.md'
  - '{project-root}/docs/bmad/planning-artifacts/epics.md'
  - '{project-root}/docs/bmad/implementation-artifacts/epic-6-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Production uses PostgreSQL, but persistence-oriented tests use SQLite in memory. That second database engine cannot prove PostgreSQL SQL translation, `SELECT ... FOR UPDATE` behavior, transaction/isolation behavior, Npgsql SQLSTATE handling, or PostgreSQL data-type round trips. It has also leaked a SQLite-specific reflective exception branch into production code. The devcontainer already provides Docker, and cold-start latency is acceptable when unit and integration tests can be selected independently.

**Approach:** Retain a fast Docker-free unit-test target for pure logic and mocked ports. Move every provider-sensitive EF Core, repository, seed, Inbox/Outbox, and transaction test to a dedicated PostgreSQL Testcontainers integration-test assembly and target. Use an amortized container fixture, isolated test databases or schemas, real Npgsql exceptions and competing connections, and an explicitly pinned PostgreSQL image. Make the full rebuild gate run both tiers and keep combined line coverage at or above 90%.

## Boundaries & Constraints

**Always:** Test persistence behavior on PostgreSQL; keep pure unit tests runnable without Docker; pin the Testcontainers packages and PostgreSQL image; use generated host ports and fixture-provided connection strings; isolate mutable database state between concurrently runnable tests; use multiple independent `DbContext` instances/connections for concurrency and locking assertions; use bounded startup and lock waits; clean up containers and databases; preserve production schema, repository, and transaction semantics; keep the full combined coverage gate at >=90%.

**Ask First:** A PostgreSQL major-version change; introducing migrations where the application currently uses `EnsureCreatedAsync`; weakening or removing the coverage threshold; splitting production projects or changing public contracts; adding a second persistence provider; changing runtime database initialization or transaction isolation; making the unit target require Docker.

**Never:** Use SQLite, EF Core InMemory, or another relational engine as a PostgreSQL substitute; use a container per test case; bind a fixed host port; use the `latest` image tag; serialize the whole suite merely to hide shared state; test `FOR UPDATE` through a mocked or provider-neutral path; catch exceptions by message text; blanket-exclude persistence code from coverage; silently skip integration tests when Docker is unavailable.

## Required Architecture Decision

ADR-016 must be accepted before implementation. It supersedes only the SQLite-specific portions of ADR-012 and AD-9; it does not discard the three-tier strategy or the coverage gate. It must record:

1. Tier 1 is fast, Docker-free unit testing of domain/application logic and adapters through their ports.
2. Tier 2 is PostgreSQL Testcontainers integration testing of EF Core models, repositories, durable stores, seeding, transactions, locking, and provider error semantics.
3. Tier 3 is distributed acceptance through Aspire/k6 with real PostgreSQL, Redis, Dapr, replication, and fault injection.
4. Unit, persistence-integration, and full rebuild targets are independently named and runnable.
5. The full gate combines the relevant coverage from unit and integration tiers and enforces >=90% without blanket exclusions.
6. The PostgreSQL image is explicitly pinned to the same major version used by the AppHost. If the AppHost currently relies on an implicit tag, ADR-016 selects a major and this story pins both locations without changing majors later by accident.
7. SQLite packages, fixtures, provider-specific branches, and forward-looking documentation are removed; frozen completed stories remain historical.

## Test Topology

| Target | Runtime dependency | Scope | Expected use |
|---|---|---|---|
| `CoreBankDemo.UnitTests.slnf` | .NET only | Pure domain, handlers, processors, options, telemetry, and adapter orchestration through mocked/fake ports | Fast inner loop and environments without Docker |
| `CoreBankDemo.IntegrationTests.slnf` | Docker + pinned PostgreSQL image | EF Core mappings, repositories, durable stores, seeding, transactions, concurrency, SQLSTATE, and locking | Provider-fidelity persistence validation |
| `CoreBankDemo.Rebuild.slnf` | Docker + .NET | All rebuilt production projects and both test tiers | Required full story/epic/CI gate |

The integration tier should be owned by a dedicated `tests/CoreBankDemo.Persistence.IntegrationTests` project unless implementation discovery proves multiple assemblies materially improve isolation. Prefer one PostgreSQL container per integration-test assembly or xUnit collection, not per test. Each concurrently runnable test class/collection receives a unique database or schema. Tests within one concurrency scenario deliberately share only that isolated database and use separate connections/contexts.

`Testcontainers.PostgreSql` and the matching xUnit v3 integration package are pinned centrally. Container startup uses the fixture's generated connection string, never a known host port. Schema setup follows the application's current `EnsureCreatedAsync` strategy; this story does not introduce migrations.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Result | Failure Signal |
|---|---|---|---|
| Unit-only run | Docker stopped/unavailable | All unit tests run; no container is contacted | Any Docker dependency is a failure |
| Integration cold start | Image absent locally | Pinned image is pulled and one amortized fixture starts | Bounded, diagnostic-rich startup failure |
| Container unavailable | Integration target selected without usable Docker | Target fails clearly with remediation context | Never silently skipped or reported green |
| Parallel test classes | Distinct isolated databases/schemas | No cross-test rows, locks, or cleanup races | State leakage fails the owning tests |
| Duplicate insert | Two connections race on a real unique index | One insert wins; loser exposes SQLSTATE `23505`; repository contract reports duplicate | Message-text or SQLite exception matching is forbidden |
| Non-unique database error | Constraint/connection failure other than `23505` | Original exception propagates | No false duplicate result |
| Row lock contention | Connection A holds `SELECT ... FOR UPDATE`; B requests same row | B waits until A commits/rolls back, then observes the correct state | B must not pass immediately via a provider-neutral load |
| Transaction rollback | Failure occurs after tracked mutations and before commit | No partial balance, Inbox, or Outbox change persists | Any partial row/state is a failure |
| Concurrent `StoreIfNewAsync` | Same dedupe identity from independent contexts | Exactly one durable row; one added result and one duplicate result | No unhandled unique violation |
| Durable ordering/claim | Tied timestamps and competing claim attempts | PostgreSQL query/index ordering and claim behavior match the repository contract | Provider-specific ordering drift is exposed |
| Data-type round trip | Representative `decimal` and `DateTimeOffset` values | Values round-trip with production precision/normalization | SQLite-compatible but PostgreSQL-wrong mappings fail |
| Concurrent seeding | Two startup paths initialize/seed an empty database | Schema and fixed demo data exist exactly once | Startup race is visible, not serialized away by the fixture |
| Full gate | Unit and integration suites both pass | Combined applicable line coverage is >=90% | Moving code between tiers cannot weaken coverage |
| Cleanup | Passing, failing, or cancelled run | Fixture and isolated databases are disposed without fixed-port residue | Cleanup diagnostics remain visible |

</frozen-after-approval>

## Code Map

- `docs/adr/ADR-016-postgresql-testcontainers-persistence-testing.md` (new); `docs/adr/ADR-012-three-tier-testing-and-coverage-gate.md`; `docs/bmad/constraints.md`; architecture spine -- accept and propagate the superseding test-tier decision without rewriting frozen completed stories.
- `Directory.Packages.props` -- remove `Microsoft.EntityFrameworkCore.Sqlite`; pin `Testcontainers.PostgreSql` and the xUnit v3 Testcontainers integration package.
- `tests/Directory.Build.props` and coverage configuration -- distinguish unit and integration execution while preserving a combined >=90% full-gate threshold. Do not use broad `ExcludeFromCodeCoverage` attributes to make the split pass.
- `CoreBankDemo.UnitTests.slnf` and `CoreBankDemo.IntegrationTests.slnf` (new); `CoreBankDemo.Rebuild.slnf`; solution metadata -- provide explicit test entry points and include both tiers in the full gate.
- `tests/CoreBankDemo.Persistence.IntegrationTests/` (new) -- shared PostgreSQL fixture, isolated database/schema lifecycle, EF model/repository/store/seed/transaction/provider tests, and targeted concurrency helpers.
- `tests/CoreBankDemo.Messaging.Tests`, `tests/CoreBankDemo.CoreBankAPI.Tests`, `tests/CoreBankDemo.PaymentsAPI.Tests` -- retain pure unit tests and move provider-sensitive cases/support out; remove all SQLite package references, fixtures, and `UseSqlite` calls.
- `CoreBankDemo.Messaging/UniqueViolation.cs` -- recognize PostgreSQL unique violations via typed Npgsql `PostgresException.SqlState == PostgresErrorCodes.UniqueViolation`; remove SQLite/reflection handling; all other errors propagate.
- `CoreBankDemo.CoreBankAPI/Inbox/AccountRepository.cs` -- directly prove its existing PostgreSQL `FOR UPDATE` query with competing real connections; do not change runtime locking semantics to make testing easier.
- Forward-looking planning/specification documents -- replace promises of future SQLite tests with the PostgreSQL integration tier; preserve completed/frozen story text as historical evidence.

## Tasks & Acceptance

**Execution:**
- [x] Write and accept ADR-016, then update constraints/architecture guidance so the approved tiers and gates have one unambiguous definition. [`ADR-016`](../../adr/ADR-016-postgresql-testcontainers-persistence-testing.md) is Accepted and records the three tiers, the named targets, the coverage partition, the version pins, and the production cleanup. `ADR-012` is marked "superseded in part" (three-tier split and >=90% gate retained; its SQLite text is now historical). `docs/bmad/constraints.md` §4 and ruling A7, the architecture spine's AD-9 + pinned-versions table, `epic-6-context.md`, `epics.md`, `AGENTS.md`, `README.md`, and `ARCHITECTURE.md` all now describe the same tiers.
- [x] Add centrally pinned Testcontainers PostgreSQL/xUnit v3 dependencies and pin the container image to the AppHost PostgreSQL major version in both locations when the AppHost tag is currently implicit; record the chosen versions in ADR-016. `Directory.Packages.props` drops `Microsoft.EntityFrameworkCore.Sqlite` and pins `Testcontainers.PostgreSql` 4.14.0 + `Testcontainers.XunitV3` 4.14.0 (the xUnit v3 integration package; `Testcontainers.Xunit` targets xUnit v2 and is incompatible with this repo's `xunit.v3` 4.0.0). The AppHost previously inherited Aspire 13.4.0's implicit default tag, verified to be `18.3`; `AppHost.cs` now pins `.WithImageTag("18.3")` and `Infrastructure/PostgresImage.cs` pins `postgres:18.3`, each pointing at the other.
- [x] Add explicit unit-only and persistence-integration solution filters/commands; make the full rebuild gate run both. New `CoreBankDemo.UnitTests.slnf` and `CoreBankDemo.IntegrationTests.slnf`; `CoreBankDemo.Rebuild.slnf` and `CoreBankDemo.sln` both gained `tests/CoreBankDemo.Persistence.IntegrationTests`.
- [x] Create the shared PostgreSQL fixture with amortized lifecycle, generated connection details, bounded waits, actionable diagnostics, isolated databases/schemas, and reliable cleanup. `PostgresContainerFixture` derives from `Testcontainers.Xunit.DbContainerFixture` and is registered once via `[assembly: AssemblyFixture(...)]`, so one container serves all 143 tests. It uses Testcontainers' generated host port (never 5432), bounds startup with `StartupTimeout` (5 min) and contention with `LockWaitTimeout` (30 s), wraps any startup failure in a remediation message (`docker info` / `docker pull postgres:18.3` / run the unit target), and has deterministic regression coverage for the timeout, no-skip promise, remediation commands, and underlying error detail. Each test receives its own `CREATE DATABASE`, which is dropped `WITH (FORCE)` on teardown — reporting, never swallowing, cleanup failures.
- [x] Move all provider-sensitive EF Core, repository, Inbox/Outbox, seeding, transaction, and concurrency tests into the integration tier; keep pure logic tests in their current unit projects. 22 files moved with history (`git mv`) into `tests/CoreBankDemo.Persistence.IntegrationTests/{Messaging,CoreBankApi,PaymentsApi}`; `UniqueViolationTests` and `PaymentStorageHandlerTests` were split so only their real-provider cases moved. The two `WebApplicationFactory` wiring tests now boot the real `Program` against the container database via `ConnectionStrings__paymentsdb`, so `AddNpgsqlDbContext` and Program.cs's `EnsureCreatedAsync` run unmodified instead of having their provider swapped.
- [x] Add real-provider coverage for SQLSTATE `23505`, non-unique error propagation, `SELECT ... FOR UPDATE`, rollback/atomicity, concurrent deduplication, durable ordering/claiming, data-type round trips, and concurrent seeding. `Messaging/PostgresErrorSemanticsTests` (real 23505 from competing connections, real 23514 check violation, non-unique propagation out of `StoreIfNewAsync`, connection failure never reported as duplicate); `CoreBankApi/AccountRowLockTests` (waiter blocks on a held `FOR UPDATE` then observes the committed value, blocks then observes a rolled-back value, two concurrent transfers serialize and conserve balance, missing row returns null); `CoreBankApi/DataTypeRoundTripTests` (decimal scale to 99999999999999.99, `Balance` column asserted as `numeric`, UTC `DateTime` kind/precision, `DateTimeOffset` instant, real `varchar` truncation `22001`); `CoreBankApi/ConcurrentSchemaInitializationTests` (two replicas racing `EnsureCreatedAsync` + seeding on one empty database); plus the migrated rollback/atomicity, concurrent-dedupe and ordering/claim suites now running on PostgreSQL.
- [x] Remove SQLite packages, fixtures, helpers, `UseSqlite` calls, provider-neutral workarounds, and the SQLite branch in `UniqueViolation`; do not replace SQLite with EF Core InMemory. Package references removed from all three unit-test projects; `SqliteMessagingTestBase`, `SqliteCoreBankApiTestBase`, and `SqlitePaymentsStore` deleted; the three DI-placeholder `UseSqlite("Data Source=:memory:")` registrations became the production Npgsql provider against a never-opened connection string. `UniqueViolation` is now a typed `PostgresException.SqlState == PostgresErrorCodes.UniqueViolation` check with no reflection, no type-name sniffing, and no message matching, and `AccountRepository.LockForUpdateAsync` lost its `[ExcludeFromCodeCoverage]`. No EF Core InMemory package exists anywhere.
- [x] Adapt coverage collection/merging so the full unit+integration gate remains >=90%, while the unit-only target remains useful and does not fail merely because persistence adapters intentionally execute in the integration tier. `tests/Directory.Build.props` defines one `$(PersistenceTierFilters)` list (the 17 persistence-adapter type filters); unit projects append it to `<Exclude>`, the persistence project uses it as `<Include>`. Both tiers keep `Threshold=90`, so every applicable type is measured exactly once with no ordering dependency, no merge step, and no blanket `[ExcludeFromCodeCoverage]`. Measured: Messaging 93.92%, CoreBankAPI 98.32%, PaymentsAPI 100%, ServiceDefaults 96.44%, DemoRunner 98.84%, persistence tier 99.27% (combined 1810/1840 lines = 98.4%).
- [x] Update active docs and developer commands; run the independent targets and full gate from the devcontainer. `README.md` gained an **Automated Tests** section with all three commands; `AGENTS.md`, `constraints.md`, the architecture spine, `epic-6-context.md`, `epics.md`, `epic-1-context.md`, and `deferred-work.md` were reconciled. All three targets were run in the devcontainer — see Verification below.

**Acceptance Criteria:**
- Given Docker is unavailable, when `CoreBankDemo.UnitTests.slnf` runs, then it passes without contacting a container runtime.
- Given Docker is available, when `CoreBankDemo.IntegrationTests.slnf` runs, then it provisions the pinned PostgreSQL image without fixed host ports and all persistence tests run against real PostgreSQL.
- Given the integration matrix above, when each scenario is exercised, then assertions observe production-equivalent Npgsql, locking, transaction, ordering, mapping, and startup behavior with bounded waits and isolated state.
- Given the full rebuild target, when it runs, then unit and persistence-integration tests both execute and combined applicable line coverage remains >=90%.
- Given a repository-wide search of active source, projects, tests, package configuration, and forward-looking docs, when the story is complete, then no SQLite dependency, fixture, `UseSqlite`, or SQLite-specific runtime branch remains; only frozen historical records may still mention it.
- Given any failure other than PostgreSQL unique violation SQLSTATE `23505`, when a store operation executes, then the original exception propagates and is never translated to duplicate.

## Design Notes

This is a fidelity change, not a request to turn every unit test into an integration test. Business rules and orchestration logic should stay behind ports and remain fast. The persistence tier owns only behavior that depends on EF translation, the PostgreSQL provider, database constraints, transactions, or concurrent connections.

Do not pay container startup cost per test. A long-lived fixture plus isolated databases/schemas gives fidelity without making the suite needlessly slow. Isolation is preferable to globally disabling test parallelism because the latter can conceal shared-state bugs and lengthen the suite.

Coverage must be handled deliberately. Persistence adapters should receive coverage from the integration run and pure logic from the unit run, with reports merged or equivalently aggregated for the full gate. The unit-only command may enforce a scoped threshold, but it must not lower the repository's full >=90% commitment or create blanket exclusions.

## Verification

**Commands:**
- `dotnet test CoreBankDemo.UnitTests.slnf` — green: DemoRunner 120/120 (98.84%), Messaging 106/106 (93.92%), CoreBankAPI 79/79 (98.32%), PaymentsAPI 109/109 (100%), ServiceDefaults 107/108 + 1 designed skip (96.44%). Also re-run with `DOCKER_HOST` pointed at a non-existent socket — still green, and no test in this target references Testcontainers.
- `dotnet test CoreBankDemo.IntegrationTests.slnf` — green: 143/143, at least 97.08% line coverage across repeated runs, ~3 s once `postgres:18.3` is local (~8 s including container start). The container-failure remediation formatter also passes as a focused test.
- `dotnet test CoreBankDemo.Rebuild.slnf` — green: all six test projects, both tiers, every coverage threshold met.
- `dotnet build CoreBankDemo.sln` — green (no warnings).
- `rg -n --glob '!docs/bmad/implementation-artifacts/spec-[1-5]-*.md' --glob '!docs/adr/ADR-012-three-tier-testing-and-coverage-gate.md' 'Microsoft.EntityFrameworkCore.Sqlite|UseSqlite|SqliteConnection|SqliteException' .` — no hits in source, projects, or package configuration. Remaining hits are this spec itself, its own acceptance criterion in `epics.md`, the new ADR-016-referencing prose in `epic-6-context.md`, the frozen `review-version-verification.md` planning artifact, and `epic-1-context.md`'s package-pin list (annotated as removed by this story).
- `git diff --check` — clean.

**Known limitation:** the "container runtime unavailable" path could not be exercised end to end in this devcontainer — Testcontainers probes several endpoints and always rediscovered the working local socket, even with `DOCKER_HOST` and `~/.testcontainers.properties` pointed at an unreachable address. The failure remains structural: no test or fixture in the persistence tier is `Skip`-able or catches a container failure, and `PostgresContainerFixture.InitializeAsync` converts any startup failure or timeout into a thrown `InvalidOperationException`, which fails the whole assembly. `PostgresContainerFixtureTests` deterministically protects the bounded timeout text, never-skipped promise, Docker/image/unit-tier remediation, and preservation of the underlying failure detail.

## Suggested Review Order

**Architecture decision**

- Establishes PostgreSQL as the sole persistence-test engine and preserves the three-tier gate.
  [`ADR-016:22`](../../adr/ADR-016-postgresql-testcontainers-persistence-testing.md#L22)

- Pins production PostgreSQL to the same version exercised by integration tests.
  [`AppHost.cs:31`](../../../CoreBankDemo.AppHost/AppHost.cs#L31)

**Tier boundaries and lifecycle**

- Assigns every persistence adapter to exactly one coverage-owning test tier.
  [`Directory.Build.props:27`](../../../tests/Directory.Build.props#L27)

- Amortizes one container while isolating each test in a generated database.
  [`PostgresContainerFixture.cs:32`](../../../tests/CoreBankDemo.Persistence.IntegrationTests/Infrastructure/PostgresContainerFixture.cs#L32)

- Keeps unit, integration, and full rebuild entry points independently runnable.
  [`CoreBankDemo.Rebuild.slnf:10`](../../../CoreBankDemo.Rebuild.slnf#L10)

**Production semantics**

- Recognizes duplicates only through PostgreSQL SQLSTATE `23505`.
  [`UniqueViolation.cs:25`](../../../CoreBankDemo.Messaging/UniqueViolation.cs#L25)

**Real-provider proofs**

- Proves row-lock blocking, commit, rollback, and serialized transfer behavior.
  [`AccountRowLockTests.cs:17`](../../../tests/CoreBankDemo.Persistence.IntegrationTests/CoreBankApi/AccountRowLockTests.cs#L17)

- Proves unique and non-unique PostgreSQL failures through actual constraints.
  [`PostgresErrorSemanticsTests.cs:16`](../../../tests/CoreBankDemo.Persistence.IntegrationTests/Messaging/PostgresErrorSemanticsTests.cs#L16)

- Documents the Docker-free and PostgreSQL-backed developer commands.
  [`README.md:349`](../../../README.md#L349)
