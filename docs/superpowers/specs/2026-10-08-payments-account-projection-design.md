# PaymentsAPI keeps a local account projection and refuses debits it can see are unfunded

> **Status:** Implemented
> **Kind:** design spec
> **Original date:** 2026-10-08
> **Related:** [ADR-001](../../adr/ADR-001-idempotent-inbox.md); [ADR-002](../../adr/ADR-002-transactional-outbox.md); [ADR-023](../../adr/ADR-023-corebank-sole-outcome-source.md); [ADR-026](../../adr/ADR-026-partition-payment-commands-by-debtor-account.md); [ADR-027](../../adr/ADR-027-payment-status-local-projection.md); [constraints](../../constraints.md); story 4.6 ([atomic inbox execution](2026-08-27-story-4-6-atomic-inbox-execution-with-event-enqueue-design.md))

## Intent

**Problem:** PaymentsAPI accepts every well-formed payment, including one from an account it has
just watched run dry. CoreBank rejects it later with `Insufficient funds`, the caller learns that
through a `transaction.failed` event, and the whole round trip was wasted. The talk also lacks a
concrete *transactional outbox* example on the PaymentsAPI side (today the outbox row is the only
thing the accept path writes, so "atomic with the business state" has nothing to be atomic with) and
a *transactional inbox* example there (the event handler only logs and caches an outcome).

**Approach:** PaymentsAPI keeps its own `ProjectedAccounts` table, built up only from what already
flows through it: the payments it accepts and the `transaction-events` CloudEvents CoreBank publishes.
Each row holds the account's last **settled** balance as CoreBank reported it and the amount
PaymentsAPI has **reserved** for accepted debits CoreBank has not settled yet. Accepting a payment
inserts the outbox row and bumps the debtor's reservation in one database transaction (the outbox
demo). Handling an event updates the projection and marks the inbox row `Completed` in one
transaction (the inbox demo). Before accepting a debit, PaymentsAPI checks `settled − reserved`
against the amount and refuses at the door with `422` when the account is *known* to be short.

**The check is best effort.** PaymentsAPI never calls CoreBank for it and never seeds the table. An
account it has not seen a `balance.updated` for has no settled balance and is never refused. The
projection lags CoreBank by the event pipeline, and a settled balance can briefly be stale (see
*Ordering*). CoreBank still executes every accepted payment against the real ledger and still decides
its outcome; the projection only stops payments that are certain to be rejected from getting in line.

**Relationship to ADR-023.** ADR-023's "no pre-validation" was written against the remote call
PaymentsAPI used to make to `POST /api/accounts/validate`, which failed in exactly the conditions
the demo injects and left payments without an outcome. A local check has neither problem: it uses
no network, and a payment it refuses was never accepted, so there is no outcome to announce and no
second source of outcomes. ADR-028 (written with the implementation) records this reading: *no
remote pre-validation; CoreBank alone decides the outcome of every accepted payment; PaymentsAPI
may refuse at the door from local state.* ADR-023 is marked "Amended by: ADR-028".

## Boundaries & Constraints

**Always:**
- Build the projection from the accept path and the `transaction-events` inbox only. No call to
  CoreBank, no seeding, no backfill.
- Make every projection write atomic with the message-store write it belongs to: the outbox insert
  and the reservation share one transaction; the event's effect and the inbox row's completion share
  one transaction.
- Refuse only when the debtor's settled balance is **known** and `settled − reserved < amount`.
  Unknown account, or known account with no settled balance yet, accepts exactly as today.
- Keep insert-first dedupe (AD-4): a duplicate `Idempotency-Key` replays the stored payment whatever
  the balance says. Dedupe is checked by the insert, before the balance.
- Keep the controller thin; the handler decides, the repository owns the transaction and the raw SQL.
- `TimeProvider` for every timestamp; `MessageConstants` and the CloudEvent `Constants` for every
  status and event type.

**Ask First:**
- Any change to `PaymentResponse`, to the `200`/`202`/`504` answers, or to the `Location` header.
- Any change to what CoreBank publishes (the four event contracts are frozen).
- Changing the payments inbox partition key (ADR-026 §4) to fix *Ordering* below.

**Never:**
- Refuse from a stale or absent settled balance (`Settled = null` means "cannot tell").
- Let a refused payment leave a row in any table or consume its idempotency key.
- Write the outbox row's `Status` from the inbox path (AD-11: the kernel owns transport state).
- Expose the projection over HTTP (no `GET /api/accounts` on PaymentsAPI) — DemoRunner and the
  load tests consume the system as it is.

## The projection

### `ProjectedAccount` row (PaymentsAPI database, table `ProjectedAccounts`)

| Column | Type | Meaning |
|---|---|---|
| `AccountNumber` | `varchar(50)`, primary key | byte-for-byte as it appears in payments and events (same rule as ADR-026) |
| `SettledBalance` | `numeric(18,2)`, nullable | `NewBalance` of the last `balance.updated` applied for this account; `NULL` until the first one |
| `Reserved` | `numeric(18,2)`, not null, default 0 | sum of accepted debits from this account whose outcome PaymentsAPI has not yet seen; never negative |
| `Currency` | `varchar(3)`, nullable | from the last `balance.updated`; informational only |
| `UpdatedAt` | `timestamp`, not null | `TimeProvider` at the last write |

**Available** = `SettledBalance − Reserved`, defined only when `SettledBalance` is not `NULL`.
Only the debtor side is reserved: an incoming credit that CoreBank has not settled does not raise the
creditor's available balance.

The database is `EnsureCreated()` only (constraints §3). The new table appears in fresh databases;
an existing development database must be recreated, as ADR-026 already requires for its own change.
A projection created after payments were already in flight can see releases for reservations it never
made; those clamp to zero (see *Inbox path*).

### Accept path (transactional outbox)

`PaymentStorageHandler.StoreAsync` keeps its validation, partitioning, rounding and snapshot logic
and swaps `IOutboxRepository.StoreIfNewAsync` for a new `IOutboxRepository.AcceptAsync(OutboxMessage,
CancellationToken)` that returns `PaymentAcceptance { Stored, Duplicate, InsufficientFunds }`. Inside
one transaction (`ExecuteInTransactionAsync`, so the Npgsql execution strategy composes), in this
order:

1. **Insert the outbox row** (`Messages.Add` + `SaveChanges`). A unique violation means a duplicate
   key: PostgreSQL has aborted the transaction, so the method rolls back, detaches the entity and
   returns `Duplicate`. The handler then loads the winner and replays it exactly as today.
2. **Lock the debtor's projection row**: `INSERT … ON CONFLICT DO NOTHING` on `AccountNumber` (so
   two first-time debits from one account cannot both fail to create it), then
   `SELECT … FOR UPDATE` on the row. Two concurrent debits from one account now serialise here, so
   they cannot both pass on the same funds.
3. **Check**: if `SettledBalance` is not `NULL` and `SettledBalance − Reserved < Amount`, roll back
   (the outbox row disappears with the transaction) and return `InsufficientFunds`.
4. **Reserve**: `Reserved += Amount`, stamp `UpdatedAt`, `SaveChanges`, commit. Return `Stored`.

The handler maps `InsufficientFunds` to a new `PaymentStorageOutcome.InsufficientFunds` with the
single error `"Insufficient funds"`; `PaymentsController` answers `422 Unprocessable Content` with
the same `{ Errors: [...] }` body shape the `400` uses. Both rails behave identically: the instant
rail's inline attempt never starts because nothing was stored, so there is no CoreBank call and no
`HoldUntil`. The refused key is not consumed; a later resend with the same key is a fresh payment.

The amounts involved (available, requested) go to the structured log and the span, never to the
caller.

### Inbox path (transactional inbox)

`TransactionEventHandler` keeps its dispatch, deserialization, tagging and logging, and gains state.
For every event type it runs one `ExecuteInTransactionAsync` that:

1. Attaches the inbox `message` if detached and applies the event to the projection (table below).
2. Calls the existing `IOutboxRepository.RecordCommittedOutcomeAsync` for `completed`/`failed`/
   `cancelled` (its own `SaveChanges` now runs inside this transaction).
3. Sets `message.Status = Completed` and `message.ProcessedAt`, then `SaveChanges`.

This is the pattern CoreBank's `TransactionExecutionHandler` already uses (story 4.6): the kernel's
`MarkAsCompletedAsync` afterwards finds the row terminal (`AlreadyTerminal`) and does nothing. Any
exception rolls everything back and propagates, so the kernel records a retry and the row returns to
`Pending` (ADR-023: retried without limit). A `DbUpdateConcurrencyException` from
`RecordCommittedOutcomeAsync` (the outbox processor flipping the row's `Status`) therefore still
retries the whole event, as it does today.

| Event | Projection effect |
|---|---|
| `balance.updated` for account *A* | upsert *A*; `SettledBalance = NewBalance`, `Currency = Currency`. If the payments outbox holds a row with `TransactionId = event.TransactionId` **and** `FromAccount = A`, this is the debtor's settlement: `Reserved −= row.Amount`. Otherwise (creditor side, or a transaction PaymentsAPI never saw) nothing else. |
| `transaction.failed`, `transaction.cancelled` | if the outbox holds a row for the transaction: upsert its `FromAccount` and `Reserved −= row.Amount` (no `balance.updated` will come). `SettledBalance` untouched. |
| `transaction.completed` | no projection effect (the two `balance.updated` events carry the balances). |

`Reserved` is clamped at zero; a release that would go below zero sets it to zero and logs a warning
with the shortfall. It means the reservation predates the projection (recreated database) and is not
worth blocking the partition for. The outbox row is only *read* here; its `Status` concurrency token
is not written, so this path adds no new conflict with the outbox processor.

A reservation whose payment never settles (a row stuck on infrastructure failure, ADR-023) stays
reserved. That is correct: the funds are committed until CoreBank says otherwise.

### Ordering

The payments inbox partitions all four event types by `TransactionId` (ADR-026 §4), so two
`balance.updated` events for one account from different transactions can be handled in either order.
`SettledBalance` is therefore last-write-wins and can be briefly stale after a reorder; the next
event for that account corrects it. Accepted for this spec: the check is best effort by definition
and CoreBank remains the judge. Partitioning `balance.updated` by account number in the payments
inbox would make the projection per-account ordered; it is a one-line change in
`TransactionEventIntakeHandler` plus an ADR-026 amendment and is left out (Ask First).

Releases are not affected by ordering: each is a subtraction of a known amount applied exactly once
(the inbox dedupe index on `(TransactionId, EventType, AccountNumber)` guarantees once; the
transaction guarantees all-or-nothing).

## Edge cases

| Input | Behaviour |
|---|---|
| Debtor never seen | row created with `SettledBalance = NULL`, reservation recorded, payment accepted |
| Debtor seen only as a creditor (has `SettledBalance`) | checked and refused if short, like any known account |
| Duplicate key, debtor short | `Duplicate` replay exactly as today; the balance is never consulted |
| Two concurrent first debits, same account | both `ON CONFLICT DO NOTHING`, both lock the one row in turn; the second sees the first's reservation |
| Insufficient funds on the instant rail | `422`, no inline attempt, no outbox row, no `HoldUntil` |
| `balance.updated` for a transaction with no payments outbox row | `SettledBalance` set, nothing released |
| `balance.updated` where `FromAccount = ToAccount` | cannot occur: CoreBank's `TransactionValidator` rejects same-account transfers before any `balance.updated` |
| Release below zero | clamp to 0, warning |
| `transaction.failed` from a door rejection (ADR-023 §5) | released like any failure |
| Redelivered event | deduped by the inbox intake; never reaches the handler twice |
| Handler retry after a rolled-back attempt | re-applies from scratch; nothing of the failed attempt persisted |
| Load-test reset | `ProjectedAccounts` truncated with the other PaymentsAPI tables, or reservations would accumulate across runs |

## Observability

Standard OpenTelemetry mechanisms only (observability skill); no new `ActivitySource`.

- `BusinessMetrics.PaymentOutcome` gains `InsufficientFunds`; `PaymentStorageHandler` records it
  through the existing `RecordPaymentIntake(outcome, scheme)`.
- The refusal tags the current request span with `FailedPaymentTags.Outcome = rejected` and
  `FailedPaymentTags.FailureReason = "insufficient_funds"`, so it lands on the failed-payments
  dashboard beside CoreBank's rejections. The structured log carries `IdempotencyKey`,
  `PartitionId`, the account and the requested amount.
- `TransactionEventHandler` adds `account.settled_balance` and `account.released` (the amount
  released by this event) tags on `balance.updated` next to the tags it already sets, and logs
  each release.

## Testing

xUnit + AwesomeAssertions + Moq, ≥90 % line coverage, three tiers (ADR-016). Tests first.

**Tier 1 — `tests/CoreBankDemo.PaymentsAPI.Tests`**
- `PaymentStorageHandlerTests`: `AcceptAsync` → `InsufficientFunds` maps to the new outcome, the
  single error text, the `InsufficientFunds` metric (via `MetricsTestListener`) and the two span
  tags; `Stored` and `Duplicate` behave exactly as before (existing tests pass unmodified).
- `PaymentsControllerTests`: `InsufficientFunds` → `422` with `{ Errors }`; both schemes.
- `TransactionEventHandlerTests`: through a mocked `IAccountProjectionStore` and
  `IInboxMessageRepository` (whose `ExecuteInTransactionAsync` just runs the delegate), each event
  type's effect on the projection and on `message.Status`/`ProcessedAt`; debtor vs creditor
  `balance.updated`; clamp with warning; no-outbox-row case; an exception leaves `message.Status`
  untouched; `RecordCommittedOutcomeAsync` still called for the three outcome events.
- `PaymentStorageRegistrationTests` / wiring tests: new dependencies resolve.

**Tier 2 — `tests/CoreBankDemo.Persistence.IntegrationTests`** (PostgreSQL Testcontainer)
- `AcceptAsync`: `Stored` commits the outbox row and the reservation together; `InsufficientFunds`
  leaves no outbox row and no reservation change; a duplicate key rolls back and returns `Duplicate`
  with the context still usable; a `NULL` settled balance never refuses; two concurrent accepts for
  one account under `FOR UPDATE` cannot both pass when funds cover only one.
- Inbox transaction: the event's projection write and the inbox row's `Completed` land together; a
  forced failure after the projection write persists neither.
- Model: `ProjectedAccounts` schema (key, precision, nullability) round-trips.

**Tier 3 — acceptance**
- Load-test accounts hold €10 000 000 each, so no `422` occurs and every existing invariant and k6
  check is unchanged. `LoadTestDatabaseResetter` truncates `ProjectedAccounts`. No new assertion.

## Code Map

- **`CoreBankDemo.PaymentsAPI/Accounts/ProjectedAccount.cs`** (new): the entity.
- **`PaymentsDbContext.cs`**: `DbSet<ProjectedAccount> ProjectedAccounts` and its configuration.
- **`Outbox/IOutboxRepository.cs` / `OutboxRepository.cs`**: `AcceptAsync` and `PaymentAcceptance`;
  the upsert and `FOR UPDATE` SQL live here (ruling A6: raw SQL isolated in repository impls).
  `StoreIfNewAsync` stays for the kernel.
- **`Accounts/IAccountProjectionStore.cs` / `AccountProjectionStore.cs`** (new): the inbox side's
  upsert/settle/release operations against the tracked context, so `TransactionEventHandler` stays
  unit-testable through a mock.
- **`Handlers/PaymentStorageHandler.cs`**: calls `AcceptAsync`; `PaymentStorageOutcome.InsufficientFunds`;
  metric and span tags.
- **`Controllers/PaymentsController.cs`**: the `422` arm.
- **`Handlers/TransactionEventHandler.cs`**: the transaction, the projection calls, the completion
  stamp; gains `IInboxMessageRepository` (for `ExecuteInTransactionAsync` and the kernel's
  `MarkAsCompletedAsync`, which attaches the row and stamps completion itself) and
  `IAccountProjectionStore`; no direct `PaymentsDbContext` dependency, so it stays unit-testable
  through mocks.
- **`CoreBankDemo.ServiceDefaults/BusinessMetrics.cs`**: `PaymentOutcome.InsufficientFunds`.
- **`CoreBankDemo.LoadTestSupport/DatabaseResetCoordinator.cs`**: truncate `ProjectedAccounts`.
- **`demo-requests.http`**: an example that drains a demo account and shows the `422`.
- **Docs, same pull request:** `docs/adr/ADR-028-local-account-projection-door-check.md` (new);
  ADR-023 "Amended by: ADR-028"; `docs/constraints.md` §2 (`POST /api/payments` → `422` when the
  local projection knows the debtor is short); `messaging-patterns` skill (PaymentsAPI as the
  second example of the atomic-inbox pattern and the first of outbox-plus-state); `ARCHITECTURE.md`
  table list.

## Out of scope

- Reading the projection over HTTP or from DemoRunner.
- Currency checks or conversion; amounts are compared as numbers.
- Reserving on the creditor side, or counting pending credits as available.
- Re-partitioning the payments inbox (see *Ordering*).
- Backfilling existing databases.
