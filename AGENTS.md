# CoreBankDemo

Mission-critical banking demo for a conference talk. Shows resilient, observable, exactly-once payment processing using .NET 10, Aspire, Dapr, and PostgreSQL.

## Projects

- **PaymentsAPI** — accepts payments; Outbox for reliable forwarding, Inbox for event consumption
- **CoreBankAPI** — processes transactions; Inbox for idempotent handling, Messaging Outbox for domain events
- **AppHost** — Aspire orchestration: Postgres, Redis, Jaeger, Dapr sidecars, optional Dev Proxy for fault injection
- **ServiceDefaults** — shared OpenTelemetry, health checks, distributed locking
- **Messaging** — Inbox/Outbox base classes, MessageConstants, PartitionHelper

## AppHosts

| AppHost | Use for |
|---|---|
| `CoreBankDemo.AppHost` | Regular development; Dev Proxy for fault injection |
| `CoreBankDemo.LoadTests` | Automated load testing; disposable infra, k6, LoadTestSupport API |

→ **aspire-launch** skill: start and stop AppHosts via Aspire CLI.
→ **aspire-mcp** skill: inspect resource state, logs, and traces via Aspire MCP.
→ **load-test** skill: run a full load test and assert results via the LoadTestSupport API.

## Workflow

Development follows the [superpowers](https://github.com/obra/superpowers) plugin: brainstorm the change, write a design spec, write an implementation plan, implement test-first, land through a pull request. Install it once per machine with `/plugin install superpowers@claude-plugins-official`; `.claude/settings.json` enables it for this repository.

- **Documents:** `docs/superpowers/specs/YYYY-MM-DD-<slug>-design.md` holds design specs, including every migrated PRD, brief, architecture spine and story spec from the 2026 rebuild; `docs/superpowers/plans/` holds implementation plans; `docs/backlog.md` lists open stories and deferred work; `docs/constraints.md` is the binding contract (invariants, external API surface, ports, test rules). `ARCHITECTURE.md` and `docs/adr/` describe the *system*. Contradictions are resolved by writing a new ADR, never by silently diverging.
- **Branch and pull-request policy:** every change, code or documentation, is made on a branch cut from a freshly fetched `origin/main` and lands on `main` through a pull request. Run `git fetch origin main && git switch -c feature/<slug> origin/main` (`docs/<slug>` for documentation-only work) before touching any file. If `git status --porcelain` prints anything before you branch, stop and report the uncommitted changes; never stash, discard, or commit them on your own. Never commit to `main`, and never build on whatever branch happens to be checked out: if `git log --oneline origin/main..HEAD` shows commits, that is someone else's unmerged work. The `.claude/hooks/branch-gate.sh` hook blocks writes while `main` is checked out. Push and open the PR only when asked.
- **Guardrails:** follow the `conventions`, `messaging-patterns`, and `observability` skills.
- **Test bar:** xUnit + AwesomeAssertions + Moq; ≥90% line coverage (coverlet-enforced) on logic projects; hosting boilerplate excluded. Three tiers (ADR-016): `dotnet test CoreBankDemo.UnitTests.slnf` (Docker-free), `dotnet test CoreBankDemo.IntegrationTests.slnf` (persistence on a pinned `postgres:18.3` Testcontainer), and the k6/Aspire acceptance harness. The build/test gate runs `CoreBankDemo.Rebuild.slnf` (both .NET tiers); story 6.1 in `docs/backlog.md` tracks making the full `.sln` the gate. Never use SQLite or EF Core InMemory as a PostgreSQL substitute.
- **Acceptance harness:** the k6 load test + LoadTestSupport assertions (exactly-once, no message loss, balance conservation, drain, per-key ordering). If code and load tests conflict, the load tests adapt — unless a real invariant is violated.

## Design Patterns

Uses Inbox/Outbox with partitioned ordering, distributed locking, exactly-once processing, and end-to-end distributed tracing.

→ **messaging-patterns** skill: Inbox/Outbox base classes, MessageConstants, PartitionHelper.
→ **observability** skill: ActivitySource registration, span creation, trace context propagation.
→ **conventions** skill: database, TimeProvider, HTTP/business logic separation, validation.
