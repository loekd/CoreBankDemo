# Core Banking Demo - Complete Summary

## 🎯 What You Have

A production-ready conference demo showcasing resilience patterns for mission-critical banking systems, built with .NET 10 and orchestrated by .NET Aspire.

## 📦 Complete Package

### Applications (2)
1. **Payments API** - Your service with all resilience patterns
2. **Core Bank API** - Simulated legacy system with idempotency

### Orchestration
- **.NET Aspire AppHost** - One-command startup
- **Service Defaults** - Shared observability and resilience

### Infrastructure
- **Jaeger** - Distributed tracing (auto-started by Aspire)
- **Dev Proxy** - Chaos engineering tool
- **SQLite** - Outbox and Inbox persistence

### Documentation (7 files)
1. **README.md** - Complete project documentation
2. **DEMO-GUIDE.md** - Stage-by-stage talk script (55 min)
3. **TALK-CHEATSHEET.md** - Quick reference during presentation
4. **ARCHITECTURE.md** - Visual diagrams and technical details
5. **ASPIRE.md** - Deep dive on .NET Aspire integration
6. **demo-requests.http** - Test requests for each stage
7. **SUMMARY.md** - This file

### Scripts (3)
- `start-aspire.sh` - One-command Aspire startup
- `start-demo.sh` - Setup and instructions
- `reset-demo.sh` - Clean database state

## 🚀 Quick Start

### Fastest Way (Recommended)
```bash
./start-aspire.sh
```

This single command:
- ✅ Starts Aspire Dashboard (http://localhost:15888)
- ✅ Launches both APIs (5032, 5294)
- ✅ Starts Jaeger container (16686)
- ✅ Configures all telemetry
- ✅ Sets up service discovery

### For Chaos Testing
```bash
# Separate terminal
dotnet tool restore
dotnet devproxy --config-file devproxy.json
```

## 🎓 Resilience Patterns Implemented

### 1. Retry & Circuit Breaker ⚡
**Problem:** Transient network failures
**Solution:** `AddStandardResilienceHandler()`
**Demo:** Stage 1 (10 min)
**Code:** `CoreBankDemo.PaymentsAPI/Program.cs:17`

### 2. Outbox Pattern 📤
**Problem:** Losing requests during sustained outages
**Solution:** Store-and-forward with background processor
**Demo:** Stage 2 (15 min)
**Code:**
- `CoreBankDemo.PaymentsAPI/OutboxMessage.cs`
- `CoreBankDemo.PaymentsAPI/OutboxProcessor.cs`
- `CoreBankDemo.PaymentsAPI/Program.cs:53-79`

### 3. Inbox Pattern 📥
**Problem:** Duplicate processing from retries
**Solution:** Idempotency keys with response caching
**Demo:** Stage 3 (10 min)
**Code:** `CoreBankDemo.CoreBankAPI/Program.cs:36-90`

### 4. Message Ordering 🔢
**Problem:** Out-of-order processing breaking business logic
**Solution:** Partitioning by account with sequential per-partition processing
**Demo:** Stage 4 (10 min)
**Code:** `CoreBankDemo.PaymentsAPI/OutboxProcessor.cs:44-79`

## 🎭 Demo Flow (55 minutes)

| Stage | Time | Pattern | Key Points |
|-------|------|---------|------------|
| 0 | 5 min | Baseline | Shows architecture, works when perfect |
| 1 | 10 min | Retry/CB | Handles 95% of real-world issues |
| 2 | 15 min | Outbox | Don't lose customer requests |
| 3 | 10 min | Inbox | Exactly-once semantics |
| 4 | 10 min | Ordering | Scale with consistency |
| 5 | 5 min | Wrap-up | Layered defense + tooling |

## 🎬 Pre-Talk Checklist

### 5 Minutes Before
- [ ] Start Aspire: `cd CoreBankDemo.AppHost && dotnet run`
- [ ] Open browser tabs:
  - [ ] Aspire Dashboard (http://localhost:15888)
  - [ ] Jaeger (http://localhost:16686)
- [ ] Open in IDE:
  - [ ] `demo-requests.http`
  - [ ] `TALK-CHEATSHEET.md`
- [ ] Have Terminal 2 ready for DevProxy

### During Talk
- [ ] Stage 1: Start DevProxy
- [ ] Show Aspire Dashboard live logs
- [ ] Show Jaeger traces
- [ ] Use demo-requests.http for all HTTP calls

## 🔧 Feature Flags

Control patterns via `appsettings.json`:

```json
{
  "Features": {
    "UseOutbox": false,    // Stage 2+
    "UseInbox": false,     // Stage 3+
    "UseOrdering": false   // Stage 4
  }
}
```

Development mode has them all enabled for the full demo experience.

## 📊 Key Metrics to Show

### In Aspire Dashboard
1. **Resources** - All services healthy
2. **Console Logs** - Live streaming, filter by service
3. **Traces** - Distributed tracing integration
4. **Metrics** - HTTP requests, latency, throughput

### In Jaeger
1. **Trace with retries** - Multiple HTTP spans
2. **Latency visualization** - See delays
3. **Service dependencies** - Architecture map

### In HTTP Responses
1. **Outbox table** - Pending → Completed
2. **Inbox table** - Idempotency keys
3. **Status codes** - 202 Accepted for outbox

## 🛠️ Troubleshooting

### Quick Fixes
```bash
# Reset everything
./reset-demo.sh
Ctrl+C # Stop Aspire
./start-aspire.sh

# Clean Docker
docker container prune

# Kill specific ports
lsof -ti:5032,5294,8000,16686,15888 | xargs kill
```

### Common Issues

**"Port already in use"**
- Stop Aspire with Ctrl+C
- Run: `lsof -ti:5032,5294,16686,15888 | xargs kill`

**"Docker not running"**
- Start Docker Desktop
- Verify: `docker info`

**"Services not starting"**
- Check Aspire Dashboard console logs
- Verify all project references are correct
- Run: `dotnet restore` in solution root

## 💡 What Makes This Demo Great

### 1. Real-World Patterns
- Not toy examples - used in production banking systems
- Patterns are framework-agnostic
- Solves actual problems you'll face

### 2. Progressive Complexity
- Start simple (baseline)
- Add complexity only when needed
- Each stage builds on previous

### 3. Visual Feedback
- Live logs in Aspire Dashboard
- Traces in Jaeger
- Database tables showing state

### 4. Professional Setup
- Modern .NET stack (Aspire, OpenTelemetry)
- Container orchestration
- Production-ready observability

### 5. Reproducible
- DevContainer for consistent environment
- All dependencies declared
- Works on any machine with Docker + .NET

## 🎤 Talking Points

### Key Messages
1. **Resilience is layered** - No single solution fixes everything
2. **Observability is mandatory** - Can't fix what you can't see
3. **Test failures in dev** - DevProxy = chaos monkey in a box
4. **Use existing tools** - Don't build from scratch

### Aspire Benefits
1. **One command to rule them all** - No more terminal juggling
2. **Built-in observability** - OpenTelemetry everywhere
3. **Production-ready** - Same stack for dev and prod
4. **Visual feedback** - See everything in real-time

### Demo Differentiators
- "This is real-world code from banking landscapes"
- "Patterns work in any language - Java, Go, Python"
- "You can use these next Monday"

## 📚 Additional Resources

### In This Repo
- [ASPIRE.md](ASPIRE.md) - Deep dive on Aspire
- [ARCHITECTURE.md](ARCHITECTURE.md) - Technical diagrams
- [DEMO-GUIDE.md](DEMO-GUIDE.md) - Full talk script
- [TALK-CHEATSHEET.md](TALK-CHEATSHEET.md) - Quick reference

### External Links
- [.NET Resilience](https://learn.microsoft.com/en-us/dotnet/core/resilience/)
- [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/)
- [Transactional Outbox](https://microservices.io/patterns/data/transactional-outbox.html)
- [Idempotent Consumer](https://microservices.io/patterns/communication-style/idempotent-consumer.html)
- [Dev Proxy](https://learn.microsoft.com/en-us/microsoft-cloud/dev/dev-proxy/)

## 🎁 Bonus Features

### Health Checks
- `/health` - Overall health
- `/alive` - Liveness probe
- Auto-configured by ServiceDefaults

### Structured Logging
- JSON formatted logs
- Correlation IDs for tracing
- Log levels configurable

### Metrics
- HTTP request rates
- Response times
- Error rates
- Custom business metrics

### Service Discovery
- Services find each other automatically
- No hardcoded URLs in production
- Aspire manages configuration

## 🏁 Ready to Present!

You have everything you need:
- ✅ Working code with all patterns
- ✅ One-command startup via Aspire
- ✅ Professional observability setup
- ✅ Comprehensive documentation
- ✅ Talk script with timings
- ✅ Quick reference cheat sheet
- ✅ Test requests for live demo

### Final Check
```bash
# Test the full flow
./start-aspire.sh
# Wait for Aspire Dashboard to open
# Run through demo-requests.http
# Everything should work!
```

**Good luck with your talk! 🎤**

Ship resilient code next Monday! 🚀
