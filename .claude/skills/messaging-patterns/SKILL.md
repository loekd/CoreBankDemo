---
name: messaging-patterns
description: |
  Inbox/Outbox implementation rules, MessageConstants usage, and partition assignment for CoreBankDemo.
  
  **When to use:**
  - When implementing or reviewing Inbox/Outbox processors in CoreBankDemo.
  - When you need to ensure correct usage of MessageConstants or partition assignment logic.
  
  **When NOT to use:**
  - Do NOT use for messaging patterns outside CoreBankDemo or for unrelated architectures.
  - Do NOT use for ad-hoc message handling that does not use the provided base classes.
---
---

## Inbox and Outbox processors

Always inherit from the base classes in `CoreBankDemo.Messaging`:
- `InboxProcessorBase<TMessage, TDbContext>`
- `OutboxProcessorBase<TMessage, TDbContext>`

Override `LockNamePrefix` and `ProcessMessageAsync`. Reference implementation: `CoreBankAPI/Inbox/InboxProcessor.cs`.

Never bypass the base classes and reimplement polling or locking logic.

## MessageConstants — no magic strings

```csharp
MessageConstants.Status.Pending / Processing / Completed / Failed
// Failed is no longer written as a row status (ADR-023); it stays the wire word
// for a business rejection inside a response payload.

MessageConstants.Defaults.BatchSize           // 10
MessageConstants.Defaults.PollingInterval     // 5 s
MessageConstants.Defaults.ProcessingTimeout  // 5 min
```

There is no retry limit (`MaxRetryCount` was removed by ADR-023): a failed row returns to `Pending` and is retried on every poll tick without limit; a batch stops at its first failed row so nothing overtakes it. Never write `Failed` as a row status.

## Partition assignment

Partition on the debtor account for payment commands, on the idempotency key elsewhere (ADR-026):

```csharp
// Payments outbox and CoreBank inbox (every row: intake, cancellation tombstone, recorded rejection)
int partitionId = PartitionHelper.GetPartitionId(request.FromAccount, partitionCount);

// Messaging outbox (transaction.* events) and payments inbox
int partitionId = PartitionHelper.GetPartitionId(transactionId, partitionCount);
// balance.updated events
int partitionId = PartitionHelper.GetPartitionId(accountNumber, partitionCount);
```

Two debits from one account therefore share a lane and execute in arrival order, within a priority class. Dedupe stays on the idempotency key (`StoreIfNewAsync`, global unique index) and is never partition-scoped.

Always use `PartitionHelper` — never write a second implementation. Its mapping (`fmix32(fnv1a(key)) % count`) is pinned by known-vector tests; changing it needs an ADR and a drained database.

## Atomic handlers: business state and the message row in one transaction

A handler that owns business state commits it **with** the message-store row, through the kernel's own helpers, never with a second connection:

```csharp
// Inbox: apply the event, then complete the row yourself inside the transaction.
// The kernel's MarkAsCompletedAsync afterwards sees AlreadyTerminal and does nothing.
await repository.ExecuteInTransactionAsync(async () =>
{
    await accounts.SettleAsync(...);              // business state
    // AlreadyTerminal (not an exception) means another worker completed the row: roll back.
    if (await repository.MarkAsCompletedAsync(message, ct) != MessageTransitionOutcome.Applied)
        throw new InvalidOperationException($"Inbox message {message.Id} was completed by another worker; rolling back this attempt's projection effects.");
}, ct);

// Outbox: insert the row first (AD-4 dedupe), then the business state, one commit.
await ExecuteInTransactionAsync(async () =>
{
    OutboxMessages.Add(message); await DbContext.SaveChangesAsync(ct);   // unique violation => rollback => Duplicate
    var account = await ProjectedAccountRows.LockAsync(DbContext, message.FromAccount, now, ct);
    account.Reserved += message.Amount; await DbContext.SaveChangesAsync(ct);
}, ct);
```

Reference implementations: `CoreBankAPI/Inbox/TransactionExecutionHandler.cs` (inbox, story 4.6), `PaymentsAPI/Handlers/TransactionEventHandler.cs` (inbox, ADR-028), `PaymentsAPI/Outbox/OutboxRepository.AcceptAsync` (outbox + state, ADR-028). Any exception rolls everything back and the kernel records a retry (ADR-023); never catch inside the delegate to "save what you can".

## Key files

| File | Purpose |
|---|---|
| `CoreBankDemo.Messaging/MessageConstants.cs` | All status strings and defaults |
| `CoreBankDemo.Messaging/PartitionHelper.cs` | Partition hashing: FNV-1a + MurmurHash3 fmix32 |
| `CoreBankDemo.Messaging/Inbox/InboxProcessorBase.cs` | Base inbox service |
| `CoreBankDemo.Messaging/Outbox/OutboxProcessorBase.cs` | Base outbox service |
| `CoreBankDemo.CoreBankAPI/Inbox/InboxProcessor.cs` | Reference inbox implementation |
| `CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxProcessor.cs` | Reference outbox implementation; PaymentsAPI forwarding lands in story 5.4 |
| `CoreBankDemo.PaymentsAPI/Accounts/ProjectedAccountRows.cs` | Account projection row upsert + `FOR UPDATE` (ADR-028) |
