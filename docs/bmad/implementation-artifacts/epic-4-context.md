# Epic 4 (E3) Context — CoreBankAPI

## Goal

Rebuild `CoreBankDemo.CoreBankAPI` (the ledger service) from scratch, test-first: domain model + DbContext + seeding, pure transaction validation, account repository + transaction executor, idempotent transaction intake, account endpoints, atomic inbox execution with event enqueue, and an event-publishing processor on the epic-2 kernel. Demolition at epic start (FR-9..FR-16; AD-2, AD-4, AD-5, AD-11).

## Stories

- Story 4.1: Domain model, DbContext, and seeding
- Story 4.2: Transaction validation
- Story 4.3: Account repository and transaction executor
- Story 4.4: Idempotent transaction intake
- Story 4.5: Account endpoints
- Story 4.6: Atomic inbox execution with event enqueue
- Story 4.7: Event publishing processor

## Requirements & Constraints

- Controllers contain no business logic (AD-2): bind → call handler → map. Application classes depend only on ports + `TimeProvider` + `ILogger<T>`.
- Idempotency key equals `TransactionId` at CoreBankAPI (AD-4); `PartitionId = FNV-1a(key) % PartitionCount`, `PartitionCount = 4`.
- Ledger mutation, inbox completion (with cached response), and domain-event enqueue commit in **one** DB transaction; no network I/O inside a DB transaction (AD-5).
- Message `Status` values are transport states only; a business rejection (invalid account, insufficient funds) is a **successfully processed** message — inbox row completes with a cached failure `ResponsePayload` and a `TransactionFailed` event, never `Failed` (AD-11).
- Repository implementations are provider-agnostic (LINQ, shared unique-violation helper) except minimal `[ExcludeFromCodeCoverage]` pass-throughs for provider-specific SQL (`FOR UPDATE`) (AD-9).
- Coverage gate (line, 90%) applies once the project enters `CoreBankDemo.Rebuild.slnf`; the test csproj's own `TODO(epic-4 story 4.1)` comment explicitly reserves removing the `Threshold=0` override for **this story**, not a later one (unlike epic 3's ServiceDefaults, which deferred it to its last story).
- One owner per seed dataset: CoreBankAPI startup seeds exactly the 3 demo accounts; the 10 `NL..LOAD` load-test accounts belong to `LoadTestSupport` (epic 7), out of scope here.
- All work targets `CoreBankDemo.Rebuild.slnf`, not the full solution.

## Technical Decisions

- **Demolition at epic start:** all existing `CoreBankDemo.CoreBankAPI/*.cs` legacy sources (Account, Controllers, CoreBankDbContext, Inbox/, Models/, Outbox/, Program.cs) are deleted; the project is rebuilt story-by-story on top of the epic-2/epic-3 kernel and ports. `CoreBankDemo.CoreBankAPI.csproj` itself (not yet in the rebuild filter) enters `CoreBankDemo.Rebuild.slnf` at story 4.1.
- **Field rename `FromAccount` → `AccountNumber` on the messaging-outbox row:** legacy `MessagingOutboxMessage.FromAccount` was a misnomer — the field identifies *which account this particular outbox row's event concerns* (from-account or to-account, depending on which of the two `BalanceUpdated` events the row represents), not literally the transaction's source account. AD-4's own text names the composite dedupe key as `(TransactionId, EventType, AccountNumber?)` — the rebuild adopts that name.
- **`IdempotencyKey` vs `TransactionId` on `InboxMessage`:** the kernel's `IInboxMessage` interface requires an `IdempotencyKey` property; CoreBankAPI's domain-specific `TransactionId` is a separate property always populated with the same value (confirmed in legacy `TransactionsController.BuildInboxMessage`: `IdempotencyKey = request.TransactionId`). The DB unique index therefore lives on `IdempotencyKey` (the kernel-required column `StoreIfNewAsync` dedupes on), which is functionally equivalent to "unique on TransactionId" per epics.md's story 4.1 AC.
- **Seeding must be unit-testable:** Program.cs stays hosting-only/thin; the idempotent seed-3-demo-accounts logic is a separate, directly-testable component (SQLite in-memory, tier 2 per AD-9), not inlined into `Main`.

## Legacy Behavioral Reference

Old `CoreBankDemo.CoreBankAPI` sources are deleted at epic start; this is what existed and must be preserved (with the `AccountNumber` rename) or explicitly superseded.

**`Account`** (plain EF entity, no interface):
```csharp
public class Account
{
    public required string AccountNumber { get; set; }
    public required string AccountHolderName { get; set; }
    public decimal Balance { get; set; }
    public required string Currency { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
```

**`InboxMessage`** implements the kernel's `IInboxMessage` (from `CoreBankDemo.Messaging`) plus domain-specific fields:
```csharp
public class InboxMessage : IInboxMessage
{
    public Guid Id { get; set; }
    public required string IdempotencyKey { get; set; }
    public int PartitionId { get; set; }
    public string Status { get; set; } = MessageConstants.Status.Pending;
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string? TraceParent { get; set; }
    public string? TraceState { get; set; }
    // Domain-specific
    public required string FromAccount { get; set; }
    public required string ToAccount { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public required string TransactionId { get; set; }
    public string? ResponsePayload { get; set; }
}
```

**`MessagingOutboxMessage`** — implements the kernel's `IOutboxMessage`; legacy `FromAccount` renamed to `AccountNumber` per the ruling above:
```csharp
public class MessagingOutboxMessage : IOutboxMessage
{
    public Guid Id { get; set; }
    public int PartitionId { get; set; }
    public required string IdempotencyKey { get; set; } // = TransactionId, kernel-required
    public required string TransactionId { get; set; }
    public required string Status { get; set; }
    public required string EventType { get; set; }
    public required string EventSource { get; set; }
    public required string AccountNumber { get; set; } // renamed from legacy FromAccount
    public required string ToAccount { get; set; }
    public decimal Amount { get; set; }
    public decimal? NewBalance { get; set; }
    public required string Currency { get; set; }
    public required string TransactionStatus { get; set; }
    public string? ErrorReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string? TraceParent { get; set; }
    public string? TraceState { get; set; }
}
```
(Story 4.1 writes the entity shape and DbContext mapping; whether `IOutboxMessage` requires an explicit `IdempotencyKey` member distinct from `TransactionId` is confirmed against the kernel's actual interface — see Boundaries.)

**`CoreBankDbContext.OnModelCreating`** (target shape, `AccountNumber` renamed as above):
- `InboxMessage`: PK `Id`; unique index on `IdempotencyKey`; composite index on `(PartitionId, Status, ReceivedAt)`; index on `Status`; index on `ReceivedAt`; `MaxLength` constraints (`IdempotencyKey` 100, `FromAccount`/`ToAccount` 50, `Currency` 3, `TransactionId` 100, `Status` 20, `TraceParent` 55, `TraceState` 512).
- `Account`: PK `AccountNumber` (MaxLength 50); `AccountHolderName` required (MaxLength 200); `Currency` required (MaxLength 3); index on `IsActive`.
- `MessagingOutboxMessage`: PK `Id`; composite index on `(PartitionId, Status, CreatedAt)`; **unique** composite index on `(TransactionId, EventType, AccountNumber)`; index on `Status`; `MaxLength` constraints (`TransactionId` 100, `Status` 20, `EventType` 100, `EventSource` 200, `TraceParent` 55, `TraceState` 512).

**Startup seeding** (legacy `Program.InitializeDatabaseWithSeedAccounts`, target behavior — component extracted for testability, not inlined in `Main`):
```csharp
if (db.Accounts.Any()) return; // idempotent: second run adds nothing

var accounts = new[] {
    new Account { AccountNumber = "NL91ABNA0417164300", AccountHolderName = "John Doe",     Balance = 5000.00m,  Currency = "EUR", IsActive = true, CreatedAt = now },
    new Account { AccountNumber = "NL20INGB0001234567", AccountHolderName = "Jane Smith",   Balance = 10000.00m, Currency = "EUR", IsActive = true, CreatedAt = now },
    new Account { AccountNumber = "NL39RABO0300065264", AccountHolderName = "Bob Johnson",  Balance = 2500.00m,  Currency = "EUR", IsActive = true, CreatedAt = now },
};
db.Accounts.AddRange(accounts);
db.SaveChanges();
```
Exact account numbers, holder names, balances, and currency must be preserved byte-for-byte (external demo narrative depends on these).

## Cross-Story Dependencies

- Story 4.1 is a hard dependency for the rest of the epic: `CoreBankDbContext`, `Account`, `InboxMessage`, `MessagingOutboxMessage` are the shapes every later story's repository/executor/controller/processor builds on.
- Story 4.1 also does the one-time project-filter admission work (`CoreBankDemo.Rebuild.slnf`, test csproj `ProjectReference`+`Include`, `Threshold=0` removal) that the rest of the epic depends on for its coverage gate to mean anything.
- Story 4.3 (account repository + transaction executor) is the first consumer of `IAccountRepository`'s `FOR UPDATE` pass-through pattern (AD-9) — story 4.1 does not need to anticipate that repository, only the entity shapes it will operate on.
- Story 4.6 (atomic inbox execution) is where `MessagingOutboxMessage` rows actually get created inside the same transaction as the ledger mutation (AD-5) — story 4.1 only owns the schema, not the write path.
- Story 4.7 depends on epic 3's `IEventPublisher`/`DaprEventPublisher` (done) — and inherits epic 3's carry-forward obligation that `CoreBankAPI/Program.cs` must register `AddDaprClient()` **before** `AddServiceDefaults()`, or `IEventPublisher` will silently never register (see epic-3-retrospective.md and `deferred-work.md`). Story 4.1's minimal `Program.cs` should get this ordering right from the start even though `IEventPublisher` isn't consumed until 4.7, so later stories don't inherit the landmine.
