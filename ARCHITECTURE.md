# Architecture Overview

This document provides technical architecture details. For demo instructions and quick start, see [README.md](README.md).

## Component Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                    .NET Aspire AppHost                           │
│                  (Orchestrates Everything)                       │
│                                                                   │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │              Aspire Dashboard (Port 15888)               │  │
│  │  • Live logs from all services                           │  │
│  │  • Distributed tracing                                   │  │
│  │  • Metrics and health checks                             │  │
│  │  • Resource management                                   │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                   │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │                   Payments API                            │  │
│  │                 (Port 5294)                               │  │
│  │                                                            │  │
│  │  Endpoints:                                                │  │
│  │  • POST /api/payments                                      │  │
│  │  • GET  /api/outbox                                        │  │
│  │  • GET  /api/inbox                                         │  │
│  │  • POST /events/transactions/*                             │  │
│  │                                                            │  │
│  │  Features:                                                 │  │
│  │  • Standard Resilience Handler (Retry, CB, Timeout)       │  │
│  │  • Outbox Pattern (PostgreSQL: paymentsdb)                │  │
│  │  • Inbox Pattern (PostgreSQL: paymentsdb)                 │  │
│  │  • Message Ordering (Partition by IdempotencyKey)         │  │
│  │  • OpenTelemetry Instrumentation                          │  │
│  │  • Dapr Integration (optional HTTP/Dapr client)           │  │
│  │                                                            │  │
│  │  Background Services:                                      │  │
│  │  • OutboxProcessor (polls every 5s, processes partitions) │  │
│  │  • InboxProcessor (polls every 5s, processes events)      │  │
│  └──────────────────────────────────────────────────────────┘  │
│                              │                                   │
│                              │ HTTP                              │
│                              ▼                                   │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │                    Dev Proxy                              │  │
│  │                  (Port 8000)                              │  │
│  │           (Orchestrated by Aspire)                        │  │
│  │                                                            │  │
│  │  Chaos Engineering:                                        │  │
│  │  • Random Errors (503, 429, 500)                          │  │
│  │  • Latency Injection (200-2000ms)                         │  │
│  │  • Rate Limiting                                           │  │
│  │  • Configured via devproxyrc.json                         │  │
│  └──────────────────────────────────────────────────────────┘  │
│                              │                                   │
│                              │ HTTP                              │
│                              ▼                                   │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │                Core Bank API                              │  │
│  │              (Port 5032)                                  │  │
│  │                                                            │  │
│  │  Endpoints:                                                │  │
│  │  • POST /api/accounts/validate                             │  │
│  │  • POST /api/transactions/process                          │  │
│  │  • GET  /api/inbox                                         │  │
│  │  • GET  /api/accounts/{accountNumber}                      │  │
│  │                                                            │  │
│  │  Features:                                                 │  │
│  │  • Inbox Pattern (PostgreSQL: corebankdb)                 │  │
│  │  • Idempotency Key Handling                               │  │
│  │  • Transaction Processing with Validation                 │  │
│  │  • Messaging Outbox (publishes events via Dapr)           │  │
│  │  • Account Management                                      │  │
│  │                                                            │  │
│  │  Background Services:                                      │  │
│  │  • InboxProcessor (polls every 5s)                        │  │
│  │  • MessagingOutboxProcessor (publishes domain events)     │  │
│  └──────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘

                              │
                              │ OTLP (gRPC/HTTP)
                              ▼

┌─────────────────────────────────────────────────────────────────┐
│                     Observability Stack                          │
│                                                                   │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │                    Jaeger                                 │  │
│  │                (Port 16686 - UI)                          │  │
│  │                (Port 4317 - OTLP gRPC)                    │  │
│  │                                                            │  │
│  │  • Distributed Tracing                                     │  │
│  │  • Metrics Collection                                      │  │
│  │  • Service Dependencies                                    │  │
│  └──────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

## Shared Library Architecture

The solution uses a shared `CoreBankDemo.Messaging` library to provide reusable inbox/outbox patterns:

### CoreBankDemo.Messaging Library

**Base Classes:**
- `InboxProcessorBase<TMessage, TDbContext>` - Base background service for processing inbox messages
- `InboxMessageRepositoryBase<TMessage, TDbContext>` - Base repository for inbox pattern
- `OutboxProcessorBase<TMessage, TDbContext>` - Base background service for processing outbox messages
- `OutboxMessageRepositoryBase<TMessage, TDbContext>` - Base repository for outbox pattern
- `PartitionHelper` - Consistent FNV-1a hashing for partition assignment
- `MessageConstants` - Centralized constants for status values and defaults

**Key Features:**
- Generic implementations supporting any message type and DbContext
- Partitioned processing with distributed locking (via Dapr)
- Automatic retry logic with exponential backoff
- OpenTelemetry tracing integration
- Configurable via `InboxProcessingOptions` and `OutboxProcessingOptions`

**Configuration Constants (MessageConstants):**
```csharp
Status.Pending      // "Pending"
Status.Processing   // "Processing"
Status.Completed    // "Completed"
Status.Failed       // "Failed"

Defaults.MaxRetryCount       // 5 attempts
Defaults.BatchSize           // 10 messages per batch
Defaults.ProcessingTimeout   // 5 minutes
Defaults.PollingInterval     // 5 seconds
```

## Pattern Implementations

### Outbox Pattern (PaymentsAPI)

**Purpose:** Reliable message delivery even during downstream outages

**Flow:**
```
1. Client → POST /api/payments
2. Store in OutboxMessage table (Status: Pending)
3. Return 202 Accepted immediately
4. OutboxProcessor background service:
   - Polls every 5 seconds
   - Processes messages in partition order
   - Validates account with CoreBankAPI
   - Processes transaction with CoreBankAPI
   - Updates status to Completed/Failed
```

**Key Classes:**
- `OutboxProcessor` - Background service (uses base pattern)
- `OutboxMessage` - Entity with partitioning
- `OutboxRepository` - Data access
- `OutboxMessageHandler` - Business logic for processing
- `ICoreBankApiClient` - HTTP or Dapr client for Core Bank API

**Partitioning Strategy:**
- Partition by `IdempotencyKey` (payment ID)
- Ensures ordered processing per payment
- Allows parallel processing across different payments

### Inbox Pattern (CoreBankAPI)

**Purpose:** Idempotent message processing, exactly-once semantics

**Flow:**
```
1. PaymentsAPI → POST /api/transactions/process (with idempotency key)
2. Check InboxMessage table by IdempotencyKey
3. If exists: Return cached TransactionId
4. If new:
   a. Validate transaction
   b. Process transaction (debit/credit accounts)
   c. Store InboxMessage with TransactionId
   d. Publish domain events to MessagingOutbox
   e. Return TransactionId
```

**Key Classes:**
- `InboxProcessor` - Background service (inherits `InboxProcessorBase`)
- `InboxMessage` - Entity with idempotency
- `InboxMessageRepository` - Data access (inherits base)
- `TransactionExecutor` - Business logic for transactions
- `TransactionValidator` - Validation logic
- `AccountRepository` - Account data access

**Distributed Transaction:**
- Single database transaction includes:
  - Marking inbox message as completed
  - Updating account balances
  - Writing domain events to messaging outbox

### Inbox Pattern (PaymentsAPI)

**Purpose:** Deduplicate and process transaction events from CoreBankAPI

**Flow:**
```
1. CoreBankAPI publishes events via Dapr (TransactionCompleted, TransactionFailed, BalanceUpdated)
2. Dapr delivers to PaymentsAPI POST /events/transactions/*
3. Store in InboxMessage table by idempotency key
4. InboxProcessor background service:
   - Polls every 5 seconds
   - Processes events (e.g., update local state)
   - Marks as completed
```

**Key Classes:**
- `InboxProcessor` - Background service (uses base pattern)
- `InboxMessage` - Entity for transaction events
- `InboxMessageRepository` - Data access
- `TransactionEventHandler` - Business logic for event handling

### Messaging Outbox Pattern (CoreBankAPI)

**Purpose:** Publish domain events reliably using the outbox pattern

**Flow:**
```
1. Transaction processing writes domain events to MessagingOutbox table
2. MessagingOutboxProcessor background service:
   - Polls every 5 seconds
   - Publishes events via Dapr pub/sub
   - Marks events as completed
```

**Event Types:**
- `TransactionCompletedEvent` - Transaction processed successfully
- `TransactionFailedEvent` - Transaction validation failed
- `BalanceUpdatedEvent` - Account balance changed

## Data Flow: End-to-End Payment

```
1. Client Request
   POST /api/payments
   {
     "fromAccount": "NL91ABNA0417164300",
     "toAccount": "NL20INGB0001234567",
     "amount": 100.00,
     "currency": "EUR"
   }

2. PaymentsAPI - Immediate Response
   - Generate unique PaymentId (IdempotencyKey)
   - Calculate PartitionId using FNV-1a hash
   - Store OutboxMessage (Status: Pending)
   - Return 202 Accepted with PaymentId

3. OutboxProcessor - Background Processing
   - Query pending messages for all partitions
   - For each message (in order per partition):
     a. Validate toAccount via CoreBankAPI
     b. POST to CoreBankAPI /api/transactions/process
        (includes IdempotencyKey from outbox)
     c. Update OutboxMessage status to Completed

4. CoreBankAPI - Transaction Processing
   - Check InboxMessage for IdempotencyKey
   - If duplicate: Return cached TransactionId
   - If new:
     a. Begin database transaction
     b. Validate transaction (sufficient funds, active accounts)
     c. Debit fromAccount, credit toAccount
     d. Store InboxMessage (Status: Completed)
     e. Write events to MessagingOutbox:
        - TransactionCompletedEvent
        - BalanceUpdatedEvent (x2)
     f. Commit transaction
     g. Return TransactionId

5. MessagingOutboxProcessor - Event Publishing
   - Query pending domain events
   - Publish via Dapr pub/sub
   - Mark as completed

6. PaymentsAPI - Event Consumption
   - Receive events via Dapr subscription
   - Store in InboxMessage (deduplicate by event ID)
   - InboxProcessor handles events asynchronously
   - Update local payment status

7. Observability
   - All steps traced with OpenTelemetry
   - Parent trace context propagated via TraceParent/TraceState
   - End-to-end visibility in Jaeger UI
```

## Database Schemas

### Payments API (paymentsdb)

**OutboxMessages Table:**
```sql
- Id (GUID, PK)
- IdempotencyKey (string, unique index)
- PartitionId (int)
- TransactionId (string)
- FromAccount (string)
- ToAccount (string)
- Amount (decimal)
- Currency (string)
- CreatedAt (datetime)
- ProcessedAt (datetime, nullable)
- RetryCount (int)
- LastError (string, nullable)
- Status (string: Pending|Processing|Completed|Failed)
- TraceParent (string, nullable)
- TraceState (string, nullable)
```

**InboxMessages Table:**
```sql
- Id (GUID, PK)
- IdempotencyKey (string, unique index)
- PartitionId (int)
- EventType (string)
- EventPayload (JSON string)
- ReceivedAt (datetime)
- ProcessedAt (datetime, nullable)
- RetryCount (int)
- LastError (string, nullable)
- Status (string: Pending|Processing|Completed|Failed)
- TraceParent (string, nullable)
- TraceState (string, nullable)
```

### Core Bank API (corebankdb)

**Accounts Table:**
```sql
- Id (int, PK, auto-increment)
- AccountNumber (string, unique index)
- AccountHolderName (string)
- Balance (decimal)
- Currency (string)
- IsActive (bool)
- CreatedAt (datetime)
- UpdatedAt (datetime)
```

**InboxMessages Table:**
```sql
- Id (GUID, PK)
- IdempotencyKey (string, unique index)
- PartitionId (int)
- TransactionId (string)
- FromAccount (string)
- ToAccount (string)
- Amount (decimal)
- Currency (string)
- ReceivedAt (datetime)
- ProcessedAt (datetime, nullable)
- RetryCount (int)
- LastError (string, nullable)
- Status (string: Pending|Processing|Completed|Failed)
- TraceParent (string, nullable)
- TraceState (string, nullable)
```

**MessagingOutboxMessages Table:**
```sql
- Id (GUID, PK)
- PartitionId (int)
- EventType (string)
- EventPayload (JSON string)
- CreatedAt (datetime)
- ProcessedAt (datetime, nullable)
- RetryCount (int)
- LastError (string, nullable)
- Status (string: Pending|Processing|Completed|Failed)
- TraceParent (string, nullable)
- TraceState (string, nullable)
```

## Configuration

### Feature Flags (appsettings.json)

**PaymentsAPI:**
```json
{
  "Features": {
    "UseDapr": false  // Toggle between HTTP and Dapr client
  },
  "OutboxProcessing": {
    "PartitionCount": 4,
    "LockExpirySeconds": 30
  },
  "InboxProcessing": {
    "PartitionCount": 4,
    "LockExpirySeconds": 30
  }
}
```

**CoreBankAPI:**
```json
{
  "InboxProcessing": {
    "PartitionCount": 4,
    "LockExpirySeconds": 30
  },
  "MessagingOutboxProcessing": {
    "PartitionCount": 4,
    "LockExpirySeconds": 30
  }
}
```

### Port Allocation

| Service       | Port  | Purpose                    |
|---------------|-------|----------------------------|
| PaymentsAPI   | 5294  | Main application API       |
| CoreBankAPI   | 5032  | Legacy core bank system    |
| DevProxy      | 8000  | Chaos proxy                |
| Aspire        | 15888 | Aspire Dashboard           |
| Jaeger UI     | 16686 | Tracing visualization      |
| Jaeger OTLP   | 4317  | OpenTelemetry collection   |

## Project Structure

```
CoreBankDemo/
├── CoreBankDemo.Messaging/            # Shared messaging library
│   ├── MessageConstants.cs            # Status values and defaults
│   ├── PartitionHelper.cs             # FNV-1a hashing
│   ├── IMessage.cs                    # Message marker interface
│   ├── Inbox/
│   │   ├── InboxProcessorBase.cs      # Base background service
│   │   ├── InboxMessageRepositoryBase.cs
│   │   ├── InboxControllerBase.cs     # Base monitoring endpoint
│   │   ├── IInboxMessage.cs           # Message interface
│   │   └── ...
│   └── Outbox/
│       ├── OutboxProcessorBase.cs     # Base background service
│       ├── OutboxMessageRepositoryBase.cs
│       ├── IOutboxMessage.cs          # Message interface
│       └── ...
│
├── CoreBankDemo.ServiceDefaults/      # Aspire shared config
│   ├── Extensions.cs                  # Service defaults setup
│   ├── DaprDistributedLockService.cs  # Distributed locking
│   ├── Configuration/
│   │   ├── InboxProcessingOptions.cs
│   │   ├── OutboxProcessingOptions.cs
│   │   └── MessagingOutboxProcessingOptions.cs
│   └── CloudEventTypes/               # Domain event types
│       ├── TransactionCompletedEvent.cs
│       ├── TransactionFailedEvent.cs
│       └── BalanceUpdatedEvent.cs
│
├── CoreBankDemo.PaymentsAPI/          # Payment service
│   ├── Program.cs                     # API configuration
│   ├── Controllers/
│   │   ├── PaymentsController.cs      # Payment endpoints
│   │   ├── OutboxController.cs        # Monitoring endpoint
│   │   ├── InboxController.cs         # Monitoring endpoint
│   │   └── TransactionEventsController.cs  # Event subscriptions
│   ├── Outbox/
│   │   ├── OutboxProcessor.cs         # Background service
│   │   ├── OutboxMessage.cs           # Entity
│   │   ├── OutboxRepository.cs        # Data access
│   │   ├── OutboxMessageHandler.cs    # Business logic
│   │   └── ICoreBankApiClient.cs      # External API client
│   ├── Inbox/
│   │   ├── InboxProcessor.cs          # Background service
│   │   ├── InboxMessage.cs            # Entity
│   │   └── InboxMessageRepository.cs  # Data access
│   └── Handlers/
│       └── TransactionEventHandler.cs # Event processing logic
│
├── CoreBankDemo.CoreBankAPI/          # Core banking service
│   ├── Program.cs                     # API configuration
│   ├── Controllers/
│   │   ├── TransactionsController.cs  # Transaction endpoints
│   │   ├── AccountsController.cs      # Account endpoints
│   │   └── InboxController.cs         # Monitoring endpoint
│   ├── Inbox/
│   │   ├── InboxProcessor.cs          # Background service
│   │   ├── InboxMessage.cs            # Entity
│   │   ├── InboxMessageRepository.cs  # Data access
│   │   ├── TransactionExecutor.cs     # Transaction logic
│   │   ├── TransactionValidator.cs    # Validation logic
│   │   └── AccountRepository.cs       # Account data access
│   ├── Outbox/
│   │   ├── MessagingOutboxProcessor.cs  # Background service
│   │   ├── MessagingOutboxMessage.cs    # Entity
│   │   └── OutboxPublisher.cs           # Event publishing helper
│   ├── Account.cs                     # Account entity
│   └── CoreBankDbContext.cs           # EF Core context
│
├── CoreBankDemo.AppHost/              # Aspire orchestration
│   └── AppHost.cs                     # Service configuration
│
├── CoreBankDemo.LoadTests/            # Load testing with k6
└── CoreBankDemo.LoadTestSupport/      # Test support API
```

## Resilience Strategy Layers

1. **Network Layer** - Standard Resilience Handler
   - Retry with exponential backoff
   - Circuit breaker
   - Timeout policies
   - Handles ~95% of transient issues

2. **Application Layer** - Outbox Pattern
   - Store-and-forward for sustained outages
   - Guaranteed delivery
   - Background processing

3. **Data Layer** - Inbox Pattern
   - Idempotency/deduplication
   - Exactly-once processing
   - Prevents duplicate charges

4. **Ordering Layer** - Partitioning
   - Per-entity ordering guarantees
   - Scalable parallel processing
   - Consistent partition assignment via FNV-1a hashing

## Key Design Decisions

### Why PostgreSQL instead of SQLite?
- Production-ready database
- Better concurrency handling
- Supports distributed locking
- Easier to scale horizontally

### Why Generic Base Classes?
- Eliminates code duplication
- Consistent behavior across services
- Easier to test and maintain
- Encourages best practices

### Why Constants instead of Magic Strings?
- Type-safe references
- Single source of truth
- Easier refactoring
- Better IDE support

### Why Partitioning by IdempotencyKey?
- Consistent hashing ensures same messages go to same partition
- FNV-1a provides good distribution
- No external coordination needed
- Deterministic replay

## Load Testing Strategy

### Overview

The `CoreBankDemo.LoadTests` project is a complete Aspire-orchestrated load test that validates exactly-once processing semantics under concurrent load using k6.

### Architecture

```
┌─────────────────────────────────────────────────────┐
│  CoreBankDemo.LoadTests  (Aspire AppHost)            │
│                                                      │
│  PostgreSQL (disposable) ──► paymentsdb              │
│                          └──► corebankdb             │
│  Redis (disposable, port 6381)                       │
│  Dapr (components-loadtest/)                         │
│                                                      │
│  PaymentsAPI  ──────────────────────────────────┐   │
│  CoreBankAPI  ──────────────────────────────┐   │   │
│  LoadTestSupport  (assert API) ─────────┐   │   │   │
│                                         │   │   │   │
│  k6 container ──────────────────────────┘───┘───┘   │
└─────────────────────────────────────────────────────┘
```

### Test Flow

1. **Setup Phase**
   - k6 verifies all APIs are healthy
   - LoadTestSupport seeds 10 test accounts (NL01LOAD0000000001 → NL10LOAD0000000010)
   - Each account initialized with €10,000,000 balance

2. **Load Phase**
   - Configurable VUs (default: 10) submit transactions concurrently
   - Configurable transaction count (default: 1000 unique transactions)
   - ~10% are deliberate retries with duplicate idempotency keys
   - Total iterations = unique count + retry count

3. **Drain Phase**
   - k6 polls `GET /assert/drain` every 500ms
   - Waits for all inbox messages to complete processing
   - 5-minute timeout to prevent infinite wait

4. **Assert Phase**
   - k6 calls `GET /assert/results?expectedUnique=<transactionCount>` for validation
   - Runs comprehensive checks on processing results

### Validation Checks

| Check | Pass Condition | Validates |
|-------|---------------|-----------|
| `no failed inbox messages` | Zero `Failed` inbox messages | Error handling works correctly |
| `no pending inbox messages` | Zero `Pending`/`Processing` messages | All messages processed to completion |
| `no duplicate processing` | Each idempotency key processed exactly once | Idempotency guarantees hold |
| `expected unique count processed` | Completed unique idempotency keys == configured transaction count | Intended workload actually ran |
| `all submitted transactions processed` | Inbox completed count == outbox submitted count | No message loss |

### Configuration

**appsettings.json:**
```json
{
  "LoadTest": {
    "TransactionCount": "1000",  // Unique transactions to submit
    "VuCount": "10"               // Concurrent virtual users
  }
}
```

**Environment Variables (k6):**
- `PAYMENTS_API_URL` - Payments API endpoint
- `LOAD_TEST_SUPPORT_URL` - Assert API endpoint
- `TRANSACTION_COUNT` - Number of unique transactions
- `VU_COUNT` - Number of concurrent VUs

### Test Accounts

Ten pre-seeded accounts for load testing:
```
NL01LOAD0000000001 → €10,000,000
NL02LOAD0000000002 → €10,000,000
...
NL10LOAD0000000010 → €10,000,000
```

Accounts are inserted idempotently by LoadTestSupport on startup.

### What Gets Validated

**Outbox Pattern:**
- Messages stored reliably before acknowledgment
- Background processor picks up all messages
- Retry logic for failed deliveries
- Partition ordering maintained

**Inbox Pattern:**
- Duplicate requests rejected via idempotency key
- Same response returned for retries
- Exactly-once transaction processing
- No race conditions under concurrent load

**End-to-End Flow:**
- Trace context propagated throughout
- All layers coordinate correctly
- Distributed locking prevents conflicts
- PostgreSQL concurrency handling works

**Performance Characteristics:**
- System processes 1000+ transactions reliably
- Concurrent VUs don't cause failures
- Retry attempts don't create duplicates
- Processing completes within reasonable time

### Infrastructure

All infrastructure is **disposable**:
- PostgreSQL databases created fresh per run
- Redis instance isolated on port 6381
- No cleanup needed - torn down when Aspire exits
- Databases auto-created via `EnsureCreated()`

### Running the Tests

```bash
# Run with defaults (1000 transactions, 10 VUs)
dotnet run --project CoreBankDemo.LoadTests

# View results in Aspire Dashboard
# - k6 container logs show pass/fail
# - API logs show processing activity
# - Database resources show transaction throughput
```

### Expected Results

**Success Criteria:**
- All k6 checks pass (green in console output)
- Zero failed inbox messages
- Zero pending/processing messages remaining
- Inbox completed count matches outbox submitted count
- No duplicate transactions processed

**Typical Performance:**
- 1000 transactions with 10 VUs: ~30-60 seconds total
- Processing rate: ~15-30 transactions/second
- Zero errors under normal conditions

### Troubleshooting

**Test times out during drain phase:**
- Check background processor logs in Aspire Dashboard
- Verify OutboxProcessor and InboxProcessor are running
- Check for database connection issues
- Increase drain timeout in k6 script if needed

**Duplicate processing detected:**
- Verify PostgreSQL unique constraints on idempotency keys
- Check distributed locking is working (Dapr)
- Review InboxRepository `StoreIfNewAsync` logic

**Failed inbox messages:**
- Check CoreBankAPI logs for processing errors
- Verify account balances are sufficient
- Review validation logic in TransactionExecutor

### Integration with CI/CD

The load tests can be integrated into CI/CD pipelines:
```bash
dotnet run --project CoreBankDemo.LoadTests --no-build
# Exit code 0 = all tests passed
# Exit code 1 = one or more checks failed
```

See `CoreBankDemo.LoadTests/README.md` for additional details.

## Observability

All components instrument with OpenTelemetry:
- **Traces:** HTTP requests, database operations, message processing
- **Metrics:** Request rates, error rates, processing times
- **Logs:** Structured logging with trace correlation

View in:
- Aspire Dashboard: Real-time logs and metrics
- Jaeger UI: Distributed traces and service dependencies
