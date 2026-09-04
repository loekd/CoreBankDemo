# Rebuild Constraints — Binding Contract

This document is the guardrail contract for every BMAD workflow invocation (PRD, architecture, epics, stories, builds) during the CoreBankDemo rebuild. It is hand-maintained, not generated. When a BMAD artifact contradicts this file, this file wins until amended by an ADR.

## 1. System invariants (asserted by the load-test harness — non-negotiable)

1. **Exactly-once processing** — no idempotency key is ever processed twice; duplicates replay the cached response.
2. **Zero message loss** — every accepted payment reaches a terminal state; total submitted == total processed.
3. **Balance conservation** — the sum of the 10 load-test account balances is constant (10 × €10,000,000).
4. **Terminal-state completeness** — zero `Failed` and zero `Pending`/`Processing` messages after drain.
5. **Per-key ordering** — messages with the same idempotency key partition are processed in order; one partition is processed by at most one worker at a time.

## 2. External contract (same externally observable behavior as `main`)

- **PaymentsAPI** (ports 5294, load-test 5295)
  - `POST /api/payments` → validate → store in Outbox (idempotent on `Idempotency-Key` header, GUID generated if absent) → `202 Accepted`; duplicate key → `202` referencing the existing record.
  - Consumes Dapr CloudEvents from topic `transaction-events` at `/events/transactions/{completed|failed|balance-updated|unknown}` into an Inbox.
- **CoreBankAPI** (port 5032)
  - `POST /api/transactions/process` → validate → dedupe by `TransactionId` → Inbox row → `202`; duplicates replay cached `ResponsePayload`.
  - `GET /api/transactions/{idempotencyKey}`, `POST /api/accounts/validate`, `GET /api/accounts/{accountNumber}`.
  - Publishes `TransactionCompleted`/`TransactionFailed` + 2× `BalanceUpdated` CloudEvents per transaction via Dapr pubsub `pubsub`, topic `transaction-events`.
- **LoadTestSupport** (port 5181): reset/drain/assert API + MCP server (`reset_database`, `poll_until_drained`, `get_assertion_results`, inbox/outbox inspection).
- Payments→CoreBank hop is **HTTP**; CoreBank→Payments hop is **Dapr pub/sub**. Trace context (`traceparent`/`tracestate`) propagates across both hops and through message-store rows.

## 3. Conventions (see `.claude/skills` — binding)

- Skills: `conventions`, `messaging-patterns`, `observability`.
- EF Core + Npgsql; `Database.EnsureCreated()` only — **never EF migrations**.
- `TimeProvider` injected everywhere; never `DateTime.Now`/`UtcNow` directly.
- Thin controllers; business logic in handlers/executors/validators returning domain types.
- Options classes validated with DataAnnotations, bound at startup with fail-fast validation.
- `ILogger<T>` structured logging including `IdempotencyKey` and `PartitionId`.
- No magic strings — `MessageConstants` and CloudEvent type constants.
- New NuGet packages only via `Directory.Packages.props` (central package management).

## 4. Test constraints

- Stack: **xUnit + AwesomeAssertions + Moq**, coverage via coverlet.
- Gate: **≥90% line coverage** on logic projects (Messaging; ServiceDefaults options/lock; API handlers, executors, validators, repositories). Enforced by `tests/Directory.Build.props` so plain `dotnet test` fails below threshold.
- Excluded from the gate: `Program.cs` wiring, AppHost, generated code — via `[ExcludeFromCodeCoverage]` + coverlet filters. **No blanket exclusion of persistence code.**
- **Test tiers (ADR-016, supersedes the SQLite parts of ADR-012/AD-9):**
  - **Tier 1 — unit**, `CoreBankDemo.UnitTests.slnf`: pure logic and adapters through mocked/fake ports. Must run with **no Docker**.
  - **Tier 2 — persistence integration**, `CoreBankDemo.IntegrationTests.slnf`: EF Core models, repositories, durable stores, seeding, transactions, `SELECT … FOR UPDATE`, SQLSTATE, ordering/claiming and data-type round trips against **real PostgreSQL via Testcontainers** (`postgres:18.3`, same major as the AppHost). Docker required; never silently skipped.
  - **Tier 3 — distributed acceptance**: Aspire + k6 with real Postgres, Redis, Dapr, replicas, fault injection.
  - **SQLite, EF Core InMemory, or any other relational engine as a PostgreSQL substitute is forbidden.** One container per test assembly (never per test); one freshly created database per test method; generated host ports only; bounded startup and lock waits.
  - Coverage is partitioned by tier via `$(PersistenceTierFilters)` in `tests/Directory.Build.props`: unit projects exclude that list, the persistence project includes exactly it, and both apply the same 90% threshold — so the combined gate stays ≥90% without blanket exclusions.
- TDD per story: failing tests first, then implementation.
- Build/test gate runs against `CoreBankDemo.Rebuild.slnf` (both tiers) until the rebuild completes.
- If load tests and main code conflict, load tests adapt — unless an invariant in §1 is genuinely violated.

## 5. Known cruft — Architect must rule explicitly on each (recommended default in parentheses)

| # | Item | Recommendation |
|---|---|---|
| A1 | Phantom `Features:UseDapr` flag; `DaprCoreBankApiClient` never existed | Delete flag; single HTTP client behind mockable `ICoreBankApiClient` |
| A2 | `MessagingOutboxProcessor` bypasses `OutboxProcessorBase` | Must derive from base; base gains pluggable publish strategy |
| A3 | `PartitionCount` config = 2, docs/ADR-004 say 4 | 4 |
| A4 | `LockRenewIntervalSeconds` bound + validated but never used | Wire real renewal or delete the option; record in ADR |
| A5 | `ARCHITECTURE.md` lists controllers that don't exist | Regenerate doc from code at the end (epic E7) |
| A6 | Weak testability seams | Interfaces for distributed lock, Dapr publish, CoreBank client, repositories; raw SQL isolated in repository impls |
| A7 | Untested Postgres-specific SQL | **Resolved by ADR-016:** repository and `FOR UPDATE` semantics are proved on real PostgreSQL in the Testcontainers persistence tier; the load-test tier still owns distributed end-to-end proof. (Original ruling — SQLite-in-memory for repo logic — is superseded.) |
| A8 | No coverage tooling | Coverlet threshold in `tests/Directory.Build.props`; tier-partitioned filters per §4 |

## 6. Non-goals

- Production deployment (demo code for a conference talk).
- New features or behavior changes beyond the cruft rulings above.
- EF migrations, alternative brokers/databases, authentication.
