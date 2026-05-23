# ADR-001: Idempotent command receivers using the Inbox pattern

**Date:** 2026-05-23  
**Status:** Accepted  
**Deciders:** Architecture team  

## Context

In a distributed payment system, any HTTP call or message delivery may be retried — by the network, a load balancer, or an upstream service. If the CoreBankAPI processes the same transaction twice, money is debited or credited twice. We need a guarantee that reprocessing the same command produces no additional side-effects.

## Decision

Use the Inbox pattern: every incoming command is stored with a unique idempotency key before processing. Duplicate deliveries are detected by key lookup and silently discarded.

## Implementation

- `InboxProcessorBase<TMessage, TDbContext>` in `CoreBankDemo.Messaging/Inbox/` provides the background processing loop.
- `InboxMessageRepositoryBase` exposes `StoreIfNewAsync` which performs an atomic insert-if-not-exists check on the idempotency key.
- `CoreBankAPI.TransactionsController.ProcessTransaction` stores incoming commands in the Inbox before returning `202 Accepted`.
- `PaymentsAPI.TransactionEventsController` stores inbound CloudEvents the same way, keyed by `{TransactionId}-{EventType}`.
- Each message carries `TraceParent`/`TraceState` so duplicate detection is visible in distributed traces.

## Consequences

### Positive
- Exactly-once processing semantics regardless of how many times a command is retried.
- Callers can safely retry on timeout without risk of double-processing.

### Negative / Trade-offs
- Every service needs an Inbox table and a background processor — more moving parts.
- Idempotency key storage grows forever unless a TTL/archival policy is added.

## Key takeaway

> Every command must be stored and de-duplicated by idempotency key before any business logic executes.
