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

## BMAD Rebuild (in progress)

This repo is being rebuilt from scratch on `feature/bmad` using [BMAD-METHOD v6.11](https://bmadcode.com/) (modules: BMM + TEA), story-driven and test-first. The last working pre-rebuild demo lives on `main`.

- **Source of truth:** `ARCHITECTURE.md` + `docs/adr/` describe the *system*; `docs/bmad/` describes the *rebuild process* (planning-artifacts, implementation-artifacts, test-artifacts). Contradictions are resolved by writing a new ADR, never by silently diverging.
- **Guardrails for every story:** follow the `conventions`, `messaging-patterns`, and `observability` skills; `docs/bmad/constraints.md` is the binding contract (invariants, external API surface, ports, test rules).
- **Test bar:** xUnit + AwesomeAssertions + Moq; ≥90% line coverage (coverlet-enforced) on logic projects; hosting boilerplate excluded. Build/test gate runs against `CoreBankDemo.Rebuild.slnf` until the rebuild completes — the full `.sln` may be red mid-flight, by design.
- **Acceptance harness:** the k6 load test + LoadTestSupport assertions (exactly-once, no message loss, balance conservation, drain, per-key ordering). If code and load tests conflict, the load tests adapt — unless a real invariant is violated.

## Design Patterns

Uses Inbox/Outbox with partitioned ordering, distributed locking, exactly-once processing, and end-to-end distributed tracing.

→ **messaging-patterns** skill: Inbox/Outbox base classes, MessageConstants, PartitionHelper.
→ **observability** skill: ActivitySource registration, span creation, trace context propagation.
→ **conventions** skill: database, TimeProvider, HTTP/business logic separation, validation.
