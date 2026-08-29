# ADR-009: One messaging kernel with pluggable delivery strategies

**Date:** 2026-08-29
**Status:** Accepted
**Deciders:** Architecture team
**Supersedes:** Transport-specific processor loops described or implied by earlier implementation notes

## Context

PaymentsAPI and CoreBankAPI need four background processors: payment forwarding, payment event consumption, CoreBank command processing, and CoreBank event publishing. The brownfield `MessagingOutboxProcessor` duplicated polling and delivery behavior instead of using the shared processor machinery. Duplicated loops inevitably diverge on locking, claiming, retry, stale-message recovery, ordering, cancellation, and trace restoration.

## Decision

`CoreBankDemo.Messaging` owns the only Inbox and Outbox processing loops. Every concrete processor derives from `InboxProcessorBase<TMessage>` or `OutboxProcessorBase<TMessage>`.

Transport-specific delivery is a port, `IOutboxDeliveryStrategy<TMessage>`. PaymentsAPI supplies an HTTP-forward strategy; CoreBankAPI supplies a Dapr-publish strategy. A normally completed `DeliverAsync` call means transport success; an exception means transport failure and is classified by the kernel. Strategies never return a separate outcome object and never implement polling, partition fan-out, locking, batching, claiming, retry transitions, poison handling, stale-claim reclaim, or trace restoration.

Inbox business handling similarly enters through `IInboxMessageHandler<TMessage>`. Processor bases resolve scoped repositories, handlers, and strategies from a fresh scope rather than holding scoped infrastructure in singleton hosted services.

## Implementation

- `CoreBankDemo.Messaging/InboxProcessorBase.cs` owns Inbox polling and dispatch.
- `CoreBankDemo.Messaging/OutboxProcessorBase.cs` owns Outbox polling and delivery.
- `CoreBankDemo.Messaging/IOutboxDeliveryStrategy.cs` is the Outbox transport seam.
- `CoreBankDemo.Messaging/IInboxMessageHandler.cs` is the Inbox application seam.
- `CoreBankDemo.CoreBankAPI/Outbox/DaprOutboxDeliveryStrategy.cs` adapts the CoreBank messaging outbox to `IEventPublisher`.
- PaymentsAPI's forwarding strategy is added by Story 5.4 behind the same port.
- Kernel tests prove partition isolation, retry/poison transitions, cancellation, repository failures, and scope disposal without live infrastructure.

## Consequences

### Positive
- Locking, ordering, retry, and tracing behavior have one implementation.
- HTTP and Dapr delivery remain independently mockable.
- Failure-path tests protect every processor rather than one concrete service.

### Negative / Trade-offs
- Transport adapters must fit the kernel's delivery-outcome contract.
- A kernel change affects all four processors and therefore requires broad regression testing.

## Key takeaway

> Transports are strategies; polling, locking, ordering, retry, and trace restoration belong to one shared kernel.
