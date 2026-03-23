# CoreBankDemo - Project Overview

A .NET 10 demonstration project showing resilience patterns for mission-critical banking systems, built for a conference talk. Orchestrated with .NET Aspire.

## Purpose
Demonstrates layered resilience patterns:
1. **Retry & Circuit Breaker** - Standard Resilience Handler
2. **Outbox Pattern** - Store-and-forward for sustained outages
3. **Inbox Pattern** - Idempotency / exactly-once processing
4. **Message Ordering** - Per-account partition ordering via FNV-1a hashing

## Services
- **PaymentsAPI** (port 5294) - Payment submission, outbox pattern, inbox for events
- **CoreBankAPI** (port 5032) - Transaction processing, inbox deduplication, messaging outbox
- **Dev Proxy** (port 8000) - Chaos engineering (random errors, latency, rate limiting)
- **Aspire Dashboard** (port 15888) - Observability
- **Jaeger** (port 16686) - Distributed tracing

## Databases
- `paymentsdb` - PostgreSQL for PaymentsAPI (outbox + inbox messages)
- `corebankdb` - PostgreSQL for CoreBankAPI (inbox + messaging outbox + accounts)

## Key Design Patterns
- Transactional Outbox/Inbox with PostgreSQL
- Distributed locking via Dapr
- FNV-1a consistent hashing for partition assignment
- OpenTelemetry tracing end-to-end
- Generic base classes in CoreBankDemo.Messaging shared library
