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

MessageConstants.Defaults.MaxRetryCount       // 5
MessageConstants.Defaults.BatchSize           // 10
MessageConstants.Defaults.PollingInterval     // 5 s
MessageConstants.Defaults.ProcessingTimeout  // 5 min
```

## Partition assignment

```csharp
int partitionId = PartitionHelper.GetPartitionId(idempotencyKey, partitionCount);
```

Always use `PartitionHelper` — never write a second implementation.

## Key files

| File | Purpose |
|---|---|
| `CoreBankDemo.Messaging/MessageConstants.cs` | All status strings and defaults |
| `CoreBankDemo.Messaging/PartitionHelper.cs` | FNV-1a partition hashing |
| `CoreBankDemo.Messaging/Inbox/InboxProcessorBase.cs` | Base inbox service |
| `CoreBankDemo.Messaging/Outbox/OutboxProcessorBase.cs` | Base outbox service |
| `CoreBankDemo.CoreBankAPI/Inbox/InboxProcessor.cs` | Reference inbox implementation |
| `CoreBankDemo.PaymentsAPI/Outbox/OutboxProcessor.cs` | Reference outbox implementation |
