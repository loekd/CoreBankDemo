# ADR-027: Payment status is read from the local projection of CoreBank's outcome

**Date:** 2026-10-07
**Status:** Accepted
**Deciders:** Architecture team

Implementation spec: [`2026-10-07-payment-status-get-design.md`](../superpowers/specs/2026-10-07-payment-status-get-design.md).

## Context

`POST /api/payments` answers `202` with `Location: /api/payments/{transactionId}`, but PaymentsAPI had
no `GET` there. On the instant rail the `202` is the honest answer to an ambiguous timeout (ADR-020),
so a caller must be able to learn the outcome later. Resending the `POST` with the same key was the
only way, and on the standard rail it echoes the outbox row's transport status.

## Decision

1. **`GET /api/payments/{transactionId}` reads the PaymentsAPI outbox row and never calls CoreBank.**
   The row already holds CoreBank's committed outcome: the delivery path caches CoreBank's answer and
   `TransactionEventHandler` records `transaction.completed`/`.failed`/`.cancelled` on it. Reading it
   is a projection of CoreBank's decision, so ADR-023's single source of outcomes holds. An inline call
   would fail in exactly the conditions that produce a `202`.
2. **Always `200` for a known payment, `404` otherwise.** The outcome travels in the frozen
   `PaymentResponse.Status`: `Pending`, `Completed`, `Failed` or `Cancelled`.
3. **The business status, never the transport status.** The outbox row's `Status` is transport state
   (AD-11): its `Completed` means CoreBank has the command in its inbox, not that it executed it. The
   `GET` reports `Completed`/`Failed`/`Cancelled` only from CoreBank's cached outcome, a `Cancelled`
   or legacy `Failed` row as such, and everything else as `Pending`.
4. **The route takes `TransactionId`,** the value the `Location` header carries. PaymentsAPI stores
   `TransactionId = IdempotencyKey`, so the lookup uses the indexed idempotency key.

## Consequences

- The `Location` header of every `202` now resolves.
- The answer lags CoreBank by the event pipeline: a payment CoreBank has decided reads `Pending` until
  its event is processed, and stays `Pending` while the pipeline is down. Late, never wrong.
- `Location` escapes `/` as `%2F`, which ASP.NET Core routing leaves encoded; the action restores it.
  A key that literally contains the text `%2F` therefore cannot be looked up. Accepted: no client in
  this repository generates such keys.
- The standard rail's duplicate `POST` still reports the transport status; `docs/backlog.md` tracks it.
