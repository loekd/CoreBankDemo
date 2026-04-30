---
name: load-test
description: "Run a full CoreBankDemo load test, wait for drain, and assert results via the LoadTestSupport API."
---

## 1. Start the load-test AppHost

See **aspire-launch** skill:

```bash
aspire start --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --no-build --non-interactive
```

Wait for `loadtest-support` to be healthy before calling its endpoints:

```bash
aspire wait loadtest-support --non-interactive
```

## 2. Reset state (MCP tool: `reset_database`)

Truncates all inbox/outbox tables and resets the 10 test accounts to 10,000,000 each.

**Via MCP tool** (preferred when connected to `http://localhost:5181/mcp`):
Call the `reset_database` tool — no parameters needed.

**Via REST** (fallback):
```
POST http://localhost:5181/reset
```

## 3. Run k6 load test

The k6 container runs automatically when started via AppHost. If running manually:

```bash
k6 run --env TRANSACTION_COUNT=1000 --env VU_COUNT=10 \
       --env PAYMENTS_API_URL=http://localhost:5295 \
       --env LOAD_TEST_SUPPORT_URL=http://localhost:5181 \
       k6/script.js
```

## 4. Wait for drain (MCP tool: `poll_until_drained`)

**Via MCP tool** (preferred): Call `poll_until_drained` with `timeoutSeconds: 120`.
The tool polls internally every 2 seconds and returns only when drained or timed out. Do NOT call repeatedly.

**Via REST** (fallback): Poll every 2–5 seconds until `isDrained == true`:
```
GET http://localhost:5181/assert/drain
```

```json
{ "isDrained": true, "outboxPending": 0, "inboxPending": 0, "completed": 1000, "failed": 0 }
```

## 5. Assert results (MCP tool: `get_assertion_results`)

**Via MCP tool** (preferred): Call `get_assertion_results` with `expectedUnique` = number of unique payments submitted.

**Via REST** (fallback):
```
GET http://localhost:5181/assert/results?expectedUnique=1000
```

Assert `allPassed == true`. Inspect individual checks and their `detail` field on failure.

```json
{
  "allPassed": true,
  "checks": {
    "noFailedMessages":        { "passed": true },
    "noPendingMessages":       { "passed": true },
    "noDuplicateProcessing":   { "passed": true },
    "expectedUniqueProcessed": { "passed": true },
    "allSubmittedProcessed":   { "passed": true },
    "balanceConservation":     { "passed": true },
    "balancesCorrect":         { "passed": true }
  }
}
```

## 6. Inspect on failure (MCP tools: `get_*_inbox`, `get_*_outbox`)

If assertions fail, inspect message state:

- `get_corebank_inbox` — filter by `status: "Failed"` to see errors
- `get_payments_outbox` — check for stuck outbox messages
- `get_corebank_outbox` / `get_payments_inbox` — trace event flow

Each tool supports `limit` (default 20) and `status` filter (Pending/Processing/Completed/Failed).

## 7. Stop

See **aspire-launch** skill:

```bash
aspire stop --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --non-interactive
```

## MCP Server Connection

LoadTestSupport exposes an MCP server at `http://localhost:5181/mcp` (Streamable HTTP).
Connect your MCP client to this URL after the AppHost is running and `loadtest-support` is healthy.
