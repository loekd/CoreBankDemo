# Copilot Instructions for CoreBankDemo

Mission-critical banking demo for a conference talk. Shows resilient, observable, exactly-once payment processing using .NET 10, Aspire, Dapr, and PostgreSQL.

## Projects

- **PaymentsAPI** — accepts payments; Outbox for reliable forwarding, Inbox for event consumption
- **CoreBankAPI** — processes transactions; Inbox for idempotent handling, Messaging Outbox for domain events
- **AppHost** — Aspire orchestration: Postgres, Redis, Jaeger, Dapr sidecars, optional Dev Proxy for fault injection
- **ServiceDefaults** — shared OpenTelemetry, health checks, distributed locking
- **Messaging** — Inbox/Outbox base classes, MessageConstants, PartitionHelper

## Design Patterns

Uses Inbox/Outbox with partitioned ordering, distributed locking, exactly-once processing, and end-to-end distributed tracing.

## Skills

Detailed instructions for common workflows live in `.claude/skills/`. Read the relevant file when performing these tasks:

| Skill | File | Use when… |
|-------|------|-----------|
| aspire-launch | `.claude/skills/aspire-launch/SKILL.md` | Starting or stopping AppHosts |
| aspire-mcp | `.claude/skills/aspire-mcp/SKILL.md` | Inspecting resource state, logs, and traces |
| conventions | `.claude/skills/conventions/SKILL.md` | Writing or reviewing code |
| load-test | `.claude/skills/load-test/SKILL.md` | Running load tests and asserting results |
| messaging-patterns | `.claude/skills/messaging-patterns/SKILL.md` | Implementing Inbox/Outbox processors |
| observability | `.claude/skills/observability/SKILL.md` | Adding tracing or ActivitySources |
