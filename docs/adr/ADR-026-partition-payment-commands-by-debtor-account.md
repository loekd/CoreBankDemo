# ADR-026: Payment commands are partitioned by debtor account

**Date:** 2026-10-07
**Status:** Proposed
**Deciders:** Architecture team
**Supersedes in part:**
- ADR-004's "partition messages using a consistent hash of the idempotency key", for the two payment-command stores (the payments outbox and the CoreBank inbox). The other two stores keep their keys.
- The architecture spine's AD-4 rule that the idempotency key is "the **ordering identity** everywhere" (`docs/superpowers/specs/2026-08-21-architecture-spine-design.md`). Dedupe identity is unchanged.
- `docs/constraints.md` invariant 5, "Per-key ordering", for those two stores. This ADR replaces it with per-debtor ordering.
- The `PartitionHelper` doc comment's rule that the mapping "must never change". Its hash gains a finalizer (see Decision 3).

## Context

The demo story is that two debits from the same account never overtake each other. If they could, a later payment might use up funds an earlier one was counting on: the later one settles and the earlier one is rejected with `Insufficient funds`, even though the earlier one arrived first. Instead, the payments outbox and the CoreBank inbox both partition on the payment's idempotency key (`PaymentStorageHandler.cs:74`, `TransactionIntakeHandler.cs:119`). A burst from one account therefore spreads across all four partitions, and those partitions run in parallel.

This was observed on the running Regular AppHost on 2026-10-07. Burst `demo-burst-b538…` sent 20 standard €1.00 payments from NL91ABNA0417164300 to NL20INGB0001234567. Its rows landed in partitions 0–3 in both stores. In CoreBank's inbox, 64 pairs of these payments executed in the reverse of their creation order, and every one of those pairs spanned two partitions. No pair inside a single partition was inverted. For example, `…-000019`, the last one created, executed at 13:47:55.748, almost a second before `…-000003` at 13:47:56.657.

Balances stayed correct. `AccountRepository` takes a `SELECT … FOR UPDATE` row lock, so concurrent debits are serialised and money is conserved. Nothing was rejected only because the account held far more than €20. The defect is ordering: a per-account guarantee the demo claims, and the code never provided.

The git history shows this has been the design from the start. Payment commands have partitioned on a message id or idempotency key since 2026-02-15 (`70b6a70`). Only `balance.updated` events partition on an account number (since `06809c0`).

Measuring this turned up a second defect. **FNV-1a modulo 4 is nearly blind to account numbers.** The FNV prime is odd, so the low two bits of the hash depend only on the low two bits of each character. Strings with the same structure collapse into the same partitions:

| Accounts | Partition with today's `PartitionHelper` |
|---|---|
| NL91ABNA0417164300, NL20INGB0001234567, NL39RABO0300065264 (Regular demo) | 3, 3, 3 |
| NL01LOAD…01 through NL10LOAD…10 (load test) | only 1 and 3 |

Switching the key to the account without fixing the hash would put the entire Regular demo in one partition. The load test would use only two of the four. The same weakness already affects today's `balance.updated` partitions.

## Decision

1. **The partition key of a payment command is its debtor account (`FromAccount`)**, in exactly two stores:
   - the PaymentsAPI outbox (`PaymentStorageHandler`),
   - the CoreBankAPI inbox. That covers every row it writes: intake (`TransactionIntakeHandler`), the cancellation tombstone (`TransactionCancellationHandler`) and the recorded rejection (`TransactionRejectionHandler`, which partitions on the clamped `FromAccount` it stores).

   `PartitionId = PartitionHelper.GetPartitionId(FromAccount, PartitionCount)`. The value is used byte-for-byte, with case kept and no normalisation, matching how CoreBank looks up the account.

2. **Dedupe identity does not change.** Both stores still dedupe on `IdempotencyKey` through the global unique index and `StoreIfNewAsync`. `FindByIdempotencyKeyAsync` is not partition-scoped and stays that way. A duplicate with a different `FromAccount` still hits the unique index and replays the original. It never creates a second row in another partition.

3. **`PartitionHelper` adds a finalizer to its hash.** It runs the 32-bit FNV-1a hash through MurmurHash3's `fmix32` finalizer before taking the modulus. The partition is `fmix32(fnv1a(key)) % partitionCount`. The finalizer works on unsigned values, so the `Math.Abs`/`int.MinValue` repair goes away.

   The finalizer applies to every key and every store. It is still the one helper the `messaging-patterns` skill requires; there is no second implementation. The new partitions:

   | Accounts | Partition with the finalizer |
   |---|---|
   | Regular demo accounts | 1, 3, 2 |
   | Load-test accounts NL01…NL10 | 0, 1, 0, 1, 0, 1, 2, 0, 1, 2 |

   The known-vector tests get new vectors.

4. **Unchanged stores:**
   - The CoreBank messaging outbox keeps `TransactionId` for `transaction.*` events and the account for `balance.updated`. The latter now benefits from the finalizer.
   - The PaymentsAPI inbox keeps `TransactionId`.

   These stores carry outcomes, not debits. Their order across transactions cannot cause an overdraft.

5. **Priority classes keep their meaning (ADR-018).** Within a partition, rows are still claimed by priority first and arrival second. The guarantee is therefore **FIFO per debtor account within a priority class**: an instant debit may still overtake a queued standard debit from the same account. That matches the rails being modelled. An SCT Inst settles immediately, while an SCT is batch work debited later, and it is rejected if the funds have gone by then. A demo of the never-overtake story should use a single rail.

6. **The acceptance gate asserts the new invariant.** `OrderingObservation` gains the row's `FromAccount` for the two command stores. LoadTestSupport adds two checks:
   - every command row's `PartitionId` equals `PartitionHelper.GetPartitionId(FromAccount, 4)`;
   - within each `(Store, FromAccount, Priority)`, rows are processed in enqueue order.

   The existing per-partition FIFO check stays. Per-partition FIFO plus correct routing together imply per-account FIFO, and the explicit check makes a routing regression show up as a named violation.

## Implementation notes

- The change is confined to two `PartitionId =` assignments in PaymentsAPI, three in CoreBankAPI, `PartitionHelper` with its tests, and LoadTestSupport. Inline claims (`TryClaimByIdIfOldestAsync`), partition locks, `HoldUntil`, the claim queries and the batch-stop rule of ADR-023 already work per partition and do not change.
- PaymentsAPI's outbox processor delivers a partition's rows one at a time and waits for each response. Once both hops are partitioned by debtor, CoreBank therefore receives a debtor's commands in order. Per-debtor order holds end to end, not just inside each store.
- Doc follow-ups in the same pull request as the code: amend `docs/constraints.md` invariant 5, the spine's AD-4 paragraph, ADR-004 and ADR-010 (`Amended by: ADR-026`), the `PartitionHelper` and `IMessage.PartitionId` doc comments, and the `messaging-patterns` skill ("partition on the debtor account for payment commands, on the idempotency key elsewhere").

## Consequences

### Positive
- Payments from one account execute in the order they were accepted. A later debit can no longer take the funds an earlier one of the same rail relied on.
- Partitioning spreads account-shaped keys across all four partitions instead of collapsing them, for `balance.updated` events as well.
- The guarantee the talk makes becomes one the load test asserts.

### Negative / Trade-offs
- **A busy account runs in a single lane.** A burst from one account is processed serially, at one partition's throughput, instead of across four. Before this change, the 20-payment burst above executed across four partitions in about 1.1 s. In the worst case it now takes roughly four times as long. The ordering guarantee costs exactly this, and the demo should say so.
- **Head-of-line blocking now follows the debtor.** Under ADR-023 a row stuck on infrastructure failure blocks its partition. That now deterministically includes every later payment from the same account, which ordering requires, plus any accounts that hash to the same partition (as before).
- **More instant payments get deferred in single-account bursts.** Both inline paths claim only when their row is first in its partition at its priority. A burst of instant payments from one account therefore queues behind itself, and more of it ends `202 Pending`.
- **Existing rows are not re-partitioned.** A pending row created under the old mapping could overtake a newer row from the same account. Drain both command stores before deploying, or reset the database. The load-test AppHost's disposable infrastructure needs nothing. A persistent Regular database must be drained first (`poll_until_drained` or the operator console). There is no schema change.

## Alternatives considered

- **Partition on `FromAccount` but keep the bare FNV-1a mapping.** Rejected. Every Regular-demo account would land in partition 3, so the system would be effectively single-threaded and the demo's parallelism would vanish.
- **Rely on the `FOR UPDATE` row lock alone.** Rejected. It serialises debits, but in whatever order they reach the lock, so it gives no ordering.
- **More partitions.** That would not fix ordering, and ADR-010 fixes the count at four.
- **Order across priority classes as well (strict per-account FIFO).** Rejected for now. An instant payment would then wait behind any queued batch payment from the same account, the head-of-line block ADR-018's priority addendum removed on purpose. It can be revisited if the demo needs mixed-rail per-account order.

## Key takeaway

> Order where money moves: payment commands queue per debtor account. And a hash that only reads the low two bits of each character is not a partitioner.
