# Architecture Overview

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
│  │  ┌─────────────────────────────────────────────────────┐ │  │
│  │  │  POST /api/payments                                  │ │  │
│  │  │  GET  /api/outbox                                    │ │  │
│  │  └─────────────────────────────────────────────────────┘ │  │
│  │                                                            │  │
│  │  Features:                                                 │  │
│  │  • Standard Resilience Handler (Retry, CB, Timeout)       │  │
│  │  • Outbox Pattern (SQLite: payments.db)                   │  │
│  │  • Message Ordering (Partition by FromAccount)            │  │
│  │  • OpenTelemetry Instrumentation                          │  │
│  │                                                            │  │
│  │  Background Services:                                      │  │
│  │  • OutboxProcessor (polls every 5 seconds)                │  │
│  └──────────────────────────────────────────────────────────┘  │
│                              │                                   │
│                              │ HTTP                              │
│                              ▼                                   │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │                    Dev Proxy                              │  │
│  │                  (Port 8000)                              │  │
│  │                                                            │  │
│  │  Chaos Engineering:                                        │  │
│  │  • Random Errors (503, 429, 500)                          │  │
│  │  • Latency Injection (200-2000ms)                         │  │
│  │  • Rate Limiting                                           │  │
│  └──────────────────────────────────────────────────────────┘  │
│                              │                                   │
│                              │ HTTP                              │
│                              ▼                                   │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │                Core Bank API                              │  │
│  │              (Port 5032)                                  │  │
│  │                                                            │  │
│  │  ┌─────────────────────────────────────────────────────┐ │  │
│  │  │  POST /api/accounts/validate                         │ │  │
│  │  │  POST /api/transactions/process                      │ │  │
│  │  │  GET  /api/inbox                                     │ │  │
│  │  └─────────────────────────────────────────────────────┘ │  │
│  │                                                            │  │
│  │  Features:                                                 │  │
│  │  • Inbox Pattern (SQLite: corebank.db)                    │  │
│  │  • Idempotency Key Handling                               │  │
│  │  • Response Caching                                        │  │
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

## Pattern Flow

### Stage 1: Retry & Circuit Breaker
```
Request → PaymentsAPI → DevProxy (failures) → Retry → Success
                ↓
            Jaeger (traces)
```

### Stage 2: Outbox Pattern
```
Request → PaymentsAPI → Outbox DB (pending)
                          ↓
                    OutboxProcessor (background)
                          ↓
                    DevProxy → CoreBankAPI
                          ↓
                    Outbox DB (completed)
```

### Stage 3: Inbox Pattern
```
PaymentsAPI → CoreBankAPI → Inbox DB (check)
                    ↓
              First time? Process
                    ↓
              Store in Inbox DB
                    ↓
              Duplicate? Return cached response
```

### Stage 4: Ordering
```
Multiple requests → Outbox DB (partitioned by FromAccount)
                          ↓
                    OutboxProcessor
                          ↓
              ┌─────────────┴─────────────┐
              ▼                            ▼
      Partition A (sequential)    Partition B (sequential)
      Account 1 msgs               Account 2 msgs
```

## Data Flow: Single Payment

```
1. Client Request
   POST /api/payments
   {
     "fromAccount": "NL91ABNA0417164300",
     "toAccount": "NL20INGB0001234567",
     "amount": 100.00,
     "currency": "EUR"
   }

2. Payments API (if UseOutbox=true)
   - Generate PaymentId
   - Store in Outbox table (Status: Pending)
   - Return 202 Accepted

3. OutboxProcessor (background, every 5s)
   - Query pending messages
   - For each message:
     a. Validate account via CoreBankAPI
     b. Process transaction via CoreBankAPI (with IdempotencyKey)
     c. Update status to Completed

4. Core Bank API (if UseInbox=true)
   - Check Inbox for IdempotencyKey
   - If found: return cached response
   - If not found:
     a. Process transaction
     b. Store in Inbox
     c. Return new response

5. OTEL Instrumentation
   - All HTTP calls traced
   - Metrics collected
   - Sent to Jaeger
```

## Database Schemas

### Outbox (payments.db)
```sql
OutboxMessages
- Id (GUID, PK)
- PaymentId (string)
- FromAccount (string)
- ToAccount (string)
- Amount (decimal)
- Currency (string)
- CreatedAt (datetime)
- ProcessedAt (datetime, nullable)
- RetryCount (int)
- LastError (string, nullable)
- Status (string: Pending|Processing|Completed|Failed)
- PartitionKey (string, nullable) -- for ordering
```

### Inbox (corebank.db)
```sql
InboxMessages
- IdempotencyKey (string, PK)
- TransactionId (string)
- Status (string)
- ProcessedAt (datetime)
- ResponsePayload (JSON string)
```

## Configuration Matrix

| Stage | UseOutbox | UseInbox | UseOrdering | CoreBankApi URL |
|-------|-----------|----------|-------------|-----------------|
| 0     | false     | false    | false       | localhost:5032  |
| 1     | false     | false    | false       | localhost:8000  |
| 2     | true      | false    | false       | localhost:8000  |
| 3     | true      | true     | false       | localhost:8000  |
| 4     | true      | true     | true        | localhost:8000  |

## Port Allocation

| Service       | Port  | Purpose                    |
|---------------|-------|----------------------------|
| PaymentsAPI   | 5294  | Main application API       |
| CoreBankAPI   | 5032  | Legacy core bank system    |
| DevProxy      | 8000  | Chaos proxy                |
| Jaeger UI     | 16686 | Tracing visualization      |
| Jaeger OTLP   | 4317  | OpenTelemetry collection   |

## File Structure

```
CoreBankDemo/
├── CoreBankDemo.PaymentsAPI/          # Main payment service
│   ├── Program.cs                      # API endpoints + config
│   ├── OutboxMessage.cs                # Outbox DB model
│   ├── OutboxProcessor.cs              # Background processor
│   ├── appsettings.json                # Production config
│   └── appsettings.Development.json    # Dev config (features enabled)
├── CoreBankDemo.CoreBankAPI/          # Legacy core bank
│   ├── Program.cs                      # API endpoints + inbox
│   ├── InboxMessage.cs                 # Inbox DB model
│   ├── appsettings.json                # Production config
│   └── appsettings.Development.json    # Dev config (inbox enabled)
├── docker-compose.yml                  # Jaeger infrastructure
├── devproxy.json                       # Chaos configuration
├── demo-requests.http                  # Test requests
├── README.md                           # Full documentation
├── DEMO-GUIDE.md                       # Talk structure
├── TALK-CHEATSHEET.md                  # Quick reference
├── ARCHITECTURE.md                     # This file
├── start-demo.sh                       # Startup script
└── reset-demo.sh                       # Clean state script
```
