# ADR-028: PaymentsAPI keeps a local account projection and may refuse a debit at the door

**Date:** 2026-10-08
**Status:** Accepted
**Deciders:** Architecture team
**Amends:** ADR-023 (decision 1, "No pre-validation")

Implementation spec: [`2026-10-08-payments-account-projection-design.md`](../superpowers/specs/2026-10-08-payments-account-projection-design.md).

## Context

ADR-023 removed PaymentsAPI's call to `POST /api/accounts/validate` and wrote "no pre-validation".
That call failed in exactly the conditions the demo injects, left payments without an outcome, and
checked nothing CoreBank would not check again. The rule was about that remote call.

PaymentsAPI still accepted every well-formed debit, including one from an account it had just watched
run dry through `balance.updated`, and learned of the rejection only through `transaction.failed`.
It also lacked a concrete transactional-outbox example (its accept path wrote only the outbox row) and
a transactional-inbox example (its event handler only logged and cached an outcome).

## Decision

1. **PaymentsAPI keeps a `ProjectedAccounts` table** built only from the payments it accepts and the
   `transaction-events` it consumes: the last `NewBalance` CoreBank reported (`SettledBalance`,
   `NULL` until the first event) and the debits accepted but not yet settled (`Reserved`). It never
   calls CoreBank for it and never seeds it.
2. **Accepting a payment inserts the outbox row and raises the debtor's reservation in one database
   transaction** (`OutboxRepository.AcceptAsync`). The insert runs first, so dedupe (AD-4) is decided
   before any balance is read. The debtor row is locked `FOR UPDATE`, so concurrent debits serialise.
3. **Handling an event applies it to the projection and completes the inbox row in one transaction**
   (`TransactionEventHandler`, via the kernel's `ExecuteInTransactionAsync` and `MarkAsCompletedAsync`,
   as CoreBank's `TransactionExecutionHandler` already does). `balance.updated` sets `SettledBalance`
   and, for the debtor, releases the reservation; `transaction.failed`/`.cancelled` release it;
   `transaction.completed` has no effect. Releases clamp at zero.
4. **PaymentsAPI refuses a debit at the door, `422 Unprocessable Content`, only when the debtor's
   settled balance is known and `SettledBalance − Reserved < Amount`.** Nothing is stored, the key is
   not consumed, both rails answer the same. The body says `Insufficient funds` and nothing more.
5. **ADR-023's rule is restated:** *no remote pre-validation; CoreBank alone decides the outcome of
   every accepted payment.* A refused payment was never accepted, so there is no outcome to announce
   and no second source of outcomes.

## Consequences

- A payment certain to be rejected no longer travels to CoreBank and back.
- The check is best effort: the projection lags CoreBank by the event pipeline, and because the
  payments inbox partitions `balance.updated` by transaction id (ADR-026 §4), two settlements for one
  account can be applied in either order, leaving `SettledBalance` briefly stale until the next event.
  CoreBank still executes every accepted payment against the real ledger.
- A reservation whose payment never settles (a row stuck on infrastructure failure, ADR-023) stays
  reserved. That is correct: the funds are committed until CoreBank says otherwise.
- `EnsureCreated` adds the table to fresh databases; an existing development database must be
  recreated. `LoadTestDatabaseResetter` truncates it, or reservations would accumulate across runs.
- `BusinessMetrics.PaymentOutcome` gains `InsufficientFunds`; the refusal is tagged on the request
  span like CoreBank's rejections (`payment.outcome = rejected`, `payment.failure_reason =
  insufficient_funds`).
- The acceptance harness is unchanged: load-test accounts hold €10 000 000, so no `422` occurs.
- The accept path clears the EF change tracker at the start of every transaction attempt, and
  `ProjectedAccountRows.LockAsync`/`OutboxRepository.RecordCommittedOutcomeAsync` detach a stale
  tracked row before querying — both because Npgsql retry-on-failure (enabled by Aspire's
  `AddNpgsqlDbContext`) can re-run a rolled-back attempt on the same `DbContext`, and a tracked query
  would otherwise return attempt 1's in-memory values.
- The inbox handler restores the message's original `Status`/`ProcessedAt` at the start of each
  attempt and on failure so the kernel still records the retry.

## Alternatives considered

- **Accept and only flag a predicted shortfall.** No contract change, but nothing a caller can see.
- **Refuse only on the instant rail.** Closer to SCT vs SCT Inst, two behaviours to explain.
- **A running total from `Delta`.** Order-independent, but PaymentsAPI never learns the opening balance.
- **Replicate CoreBank's `Account` table.** More to keep in sync, nothing more to demonstrate.
