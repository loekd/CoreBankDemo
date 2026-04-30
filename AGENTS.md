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

## Design Patterns

Uses Inbox/Outbox with partitioned ordering, distributed locking, exactly-once processing, and end-to-end distributed tracing.

→ **messaging-patterns** skill: Inbox/Outbox base classes, MessageConstants, PartitionHelper.
→ **observability** skill: ActivitySource registration, span creation, trace context propagation.
→ **conventions** skill: database, TimeProvider, HTTP/business logic separation, validation.
