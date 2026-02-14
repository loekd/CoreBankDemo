# Conference Talk Cheat Sheet

Quick reference for the 55-minute presentation.

## Pre-Talk Setup (5 min before)

```bash
# 1. Start Aspire (launches everything!)
cd CoreBankDemo.AppHost
dotnet run

# 2. Terminal windows (2):
# Terminal 1: Aspire (already running)
# Terminal 2: Wait to start DevProxy during Stage 1

# 3. Open in browser tabs:
# - Aspire Dashboard: http://localhost:15888
# - Jaeger: http://localhost:16686
# - demo-requests.http in IDE
```

## Talk Structure (55 min total)

### Stage 0: Baseline (5 min) ⏱️ 0:00-0:05

**Say:**
- "Legacy core banking system, we need to add payment services"
- "Simple REST calls, no OAuth - that's not our focus"
- "Works perfectly... when everything is perfect"

**Show:**
- Architecture diagram
- Request #1 from `demo-requests.http` → Success
- Request #2 (invalid account) → Proper error

**Code:** `CoreBankDemo.PaymentsAPI/Program.cs:48-112`

---

### Stage 1: Retry & Circuit Breaker (10 min) ⏱️ 0:05-0:15

**Setup:**
```json
// devproxy.json - Enable
"GenericRandomErrorPlugin": { "enabled": true }
```

**Terminal 2:**
```bash
dotnet tool restore
dotnet devproxy --config-file devproxy.json
```

**Say:**
- "Networks have hiccups - let's simulate that"
- "Built-in resilience handler includes retry, circuit breaker, timeout"
- "Handles ~95% of real-world issues"

**Show:**
1. Request #3 → Show random failures in logs
2. Show retry attempts in logs
3. **Open Jaeger** → Find trace with retries
4. Point out multiple HTTP client spans
5. Show eventual success

**Code:** `Program.cs:17` - `AddStandardResilienceHandler()`

**Key Point:** "This is your first line of defense"

---

### Stage 2: Outbox Pattern (15 min) ⏱️ 0:15-0:30

**Setup:**
- Already enabled: `appsettings.Development.json` → `UseOutbox: true`
- Increase DevProxy error rate or stop Core Bank API via Aspire Dashboard

**Say:**
- "But what about longer outages? Hours, not seconds?"
- "Can't just retry forever - need to store and forward"
- "This is the Transactional Outbox pattern"

**Show:**
1. Request #4 → Returns 202 Accepted, status "Pending"
2. Request #5 → Show outbox table with pending messages
3. **Restart Core Bank API** (via Aspire Dashboard or it restarts automatically)
4. Watch Terminal 2 logs → See OutboxProcessor wake up
5. Request #5 again → Show status "Completed"

**Code:**
- Storage: `Program.cs:53-79`
- Processor: `OutboxProcessor.cs:37-142`
- Model: `OutboxMessage.cs`

**Key Point:** "Don't lose customer requests!"

---

### Stage 3: Inbox Pattern (10 min) ⏱️ 0:30-0:40

**Setup:**
```json
// CoreBankDemo.CoreBankAPI/appsettings.Development.json
"UseInbox": true
```

**Restart Core Bank API** (via Aspire Dashboard or it restarts automatically)

**Say:**
- "But now we have a new problem - duplicates!"
- "Retry can cause double-charging"
- "Need idempotency - Inbox pattern"

**Show:**
1. Request #6 → Note the transactionId in response
2. Request #7 → **Same idempotency key** → Same transactionId!
3. Request #8 → Show inbox table
4. **Show Aspire Dashboard** → Structured logs showing idempotency detection
5. Explain: First creates, second returns cached response

**Code:** `CoreBankDemo.CoreBankAPI/Program.cs:36-90`

**Key Point:** "Critical for financial systems - exactly-once semantics"

---

### Stage 4: Ordering (10 min) ⏱️ 0:40-0:50

**Setup:**
- Already enabled: `UseOrdering: true`

**Say:**
- "One more problem - ordering"
- "Multiple payments from same account, processed out of order"
- "Need per-account ordering, but also need to scale"

**Show:**
1. Requests #9-11 → Rapid-fire from Account 1
2. Requests #12-13 → Rapid-fire from Account 2
3. Request #14 → Show outbox with `PartitionKey`
4. **Watch Aspire Dashboard logs** → Show parallel processing
5. Explain: One message per partition at a time

**Code:**
- Partition key: `Program.cs:73`
- Ordering logic: `OutboxProcessor.cs:44-79`

**Key Point:** "Balance scalability with consistency guarantees"

---

### Stage 5: Wrap-up (5 min) ⏱️ 0:50-0:55

**Say:**
- "Resilience is layered, not a single solution"
- "Each pattern solves a specific problem"

**Show Aspire Dashboard:**
- Resource map showing all services
- Live logs from all components
- Metrics and traces
- Point out how Aspire makes this easy

**Show Jaeger:**
- Full trace with retries, outbox, inbox
- Point out distributed nature

**Key Takeaways:**

1. **Layer your defenses:**
   - Retry/CB → transient failures (seconds)
   - Outbox → sustained outages (minutes/hours)
   - Inbox → data integrity
   - Ordering → business logic

2. **Observability is mandatory:**
   - Can't fix what you can't see
   - OpenTelemetry is the standard

3. **Test failure in dev:**
   - DevProxy, chaos engineering
   - Find bugs before production

4. **Tools exist:**
   - Don't build from scratch
   - .NET Aspire orchestration
   - Built-in resilience patterns
   - OpenTelemetry everywhere
   - DevProxy for chaos testing

**End:** "Questions? Ship resilient code next Monday!"

---

## Emergency Commands

**Reset everything:**
```bash
./reset-demo.sh
```

**Kill Aspire:**
```bash
# Just Ctrl+C in the Aspire terminal
# Everything stops cleanly
```

**Restart everything:**
```bash
cd CoreBankDemo.AppHost
dotnet run
```

**Check what's running:**
```bash
lsof -ti:5032,5294,8000,16686
```

## Key URLs (Have as browser tabs)

1. http://localhost:15888 (Aspire Dashboard) ⭐
2. http://localhost:16686 (Jaeger)
3. http://localhost:5294/api/outbox
4. http://localhost:5032/api/inbox

## Feature Flags Reference

| Feature | File | Line | Purpose |
|---------|------|------|---------|
| UseOutbox | PaymentsAPI/appsettings.Development.json | 14 | Store-and-forward |
| UseInbox | CoreBankAPI/appsettings.Development.json | 9 | Idempotency |
| UseOrdering | PaymentsAPI/appsettings.Development.json | 16 | Per-account ordering |

## Talking Points to Remember

- ✅ "Networks fail, it's not an 'if' but 'when'"
- ✅ "This is real-world code from banking/utilities"
- ✅ "Patterns are universal - not just .NET"
- ✅ "Start simple, add complexity only when needed"
- ✅ "DevProxy - your chaos monkey in a box"
- ✅ "Aspire - local development that matches production"

## Don't Forget

- 👀 **Look at the audience**, not just the screen
- 🎤 **Test audio** before starting
- ⏱️ **Check time** at each stage transition
- 💬 **Pause for questions** after Stage 3 (halfway)
- 😊 **Enjoy it** - you've got this!
