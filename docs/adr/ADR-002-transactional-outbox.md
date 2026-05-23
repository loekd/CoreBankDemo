# ADR-002: Transactional Outbox for guaranteed message delivery

**Date:** 2026-05-23  
**Status:** Accepted  
**Deciders:** Architecture team  

## Context

When PaymentsAPI accepts a payment and needs to forward it to CoreBankAPI, a crash between committing local state and sending the HTTP request would lose the message. Publishing first and committing second risks sending a message for a payment that was never persisted. Neither order is safe on its own.

## Decision

Use the Transactional Outbox pattern: write the business state and the outbound message in a single database transaction. A separate background processor polls for pending messages and delivers them.

## Implementation

- `OutboxProcessorBase<TMessage, TDbContext>` in `CoreBankDemo.Messaging/Outbox/` defines the polling loop, retry handling, and activity tracing.
- `PaymentsAPI.PaymentsController` writes an `OutboxMessage` atomically when accepting a payment — the HTTP response is `202 Accepted` immediately.
- `PaymentsAPI.OutboxProcessor` picks up pending messages and calls CoreBankAPI's `/api/transactions/process`.
- `CoreBankAPI.MessagingOutboxProcessor` publishes domain events (TransactionCompleted, BalanceUpdated) to Dapr pub/sub after committing the transaction.
- Failed deliveries increment `RetryCount`; messages exceeding `MaxRetryCount` (5) are marked Failed.

## Consequences

### Positive
- Zero message loss: if the row is committed, the message will eventually be delivered.
- Delivery is decoupled from the request path — the API responds fast.

### Negative / Trade-offs
- At-least-once delivery: the downstream receiver must be idempotent (see ADR-001).
- Delivery latency depends on the polling interval (default 5 s).

## Key takeaway

> Never send a message outside of the transaction that creates the state it represents — use the Outbox.
