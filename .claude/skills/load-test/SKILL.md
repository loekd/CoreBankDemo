---
name: load-test
description: |
  Run a full CoreBankDemo load test, wait for drain, and assert results via the LoadTestSupport API.
  
  **When to use:**
  - When you want to run a complete end-to-end load test of CoreBankDemo, including reset, execution, drain, and assertion, via the LoadTestSupport MCP API.
  - When you need to validate system resilience, idempotency, and correctness under load.
  
  **When NOT to use:**
  - Do NOT use for unit testing, manual, ad-hoc, or partial testing of individual services.
  - Do NOT use if you only want to run k6 or inspect a single component—use the relevant skill or tool instead.
  - Do NOT use the Aspire MCP CLI for these endpoints (see warning below).
---
---

## Important!

- You are not allowed to create or run arbitrary bash commands. You must restrict yourself to running the 'aspire' cli and 'curl' for HTTP calls. No other tools are allowed.
- Never reset the database unless explicitly asked to do so, as this is a destructive operation!
- **Default transaction count is 100** (configured in `CoreBankDemo.LoadTests/appsettings.json`). To run a different number of transactions or VUs, use `--` to pass .NET configuration overrides to the AppHost (see below).

## 1. Start both AppHosts

The load test requires **two AppHosts**: the regular AppHost (services + Jaeger) and the LoadTests AppHost (k6 + load-test-support).

First, start the regular AppHost **with DevProxy disabled** (default for load tests):

```bash
aspire start --apphost CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj --non-interactive -- --Features:UseDevProxy=false
```

To run a **chaos/resilience test** with DevProxy fault injection enabled:

```bash
aspire start --apphost CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj --non-interactive -- --Features:UseDevProxy=true
```

DevProxy injects random HTTP errors (5% rate) and latency (20–200ms) on calls from PaymentsAPI to CoreBankAPI. This is useful for testing retry/resilience behavior but significantly slows throughput.

Wait for `payments-api` to be healthy:

```bash
aspire wait payments-api --apphost CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj --non-interactive
```

Then start the LoadTests AppHost:

```bash
aspire start --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --non-interactive
```

To override transaction count and/or VU count (e.g., for 1000 transactions with 8 VUs):

```bash
aspire start --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --non-interactive -- --LoadTest:TransactionCount=1000 --LoadTest:VuCount=8
```

Arguments after `--` are forwarded to the AppHost as .NET configuration overrides and take precedence over `appsettings.json`. Both keys are optional — omit either to use the `appsettings.json` default.

Wait for `loadtest-support` to be healthy:

```bash
aspire wait loadtest-support --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --non-interactive
```

The LoadTests AppHost automatically starts LoadTestSupport (MCP server on port 5181) and k6 (load generator). Do NOT run k6 manually.

## MCP Protocol

LoadTestSupport exposes a **Streamable HTTP MCP server** at the **root path** (`http://localhost:5181/`).

**Important**: The endpoint is `/` (root), NOT `/mcp`.

All requests use JSON-RPC 2.0 over HTTP POST. Responses use SSE format (`event: message\ndata: {json}`).

### Session lifecycle

Every MCP session requires initialization before calling tools:

```bash
# 1. Initialize — capture the Mcp-Session-Id directly into a variable (no temp file)
INIT_RESPONSE=$(curl -s -i -X POST http://localhost:5181/ \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"copilot-cli","version":"1.0"}}}')

SESSION_ID=$(echo "$INIT_RESPONSE" | grep -i "mcp-session-id" | tr -d '\r' | awk '{print $2}')

# 2. Send initialized notification
curl -s -X POST http://localhost:5181/ \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -H "Mcp-Session-Id: $SESSION_ID" \
  -d '{"jsonrpc":"2.0","method":"notifications/initialized"}'
```

All subsequent tool calls must include the `Mcp-Session-Id` header.


## 2. Run k6 load test

The k6 container runs automatically when the LoadTests AppHost starts. DO NOT RUN IT MANUALLY.

## 3. Wait for drain (MCP tool: `poll_until_drained`)

Polls the inbox/outbox every 2 seconds until all messages are processed or timeout is reached.
**Streams progress notifications** via SSE during polling — each poll emits a `notifications/progress`
event with percentage complete and message counts.

To receive and display progress notifications in real-time:

```bash
# Helper function to parse and display progress
parse_drain_progress() {
  local session_id="$1"
  curl -sN --max-time 180 -X POST http://localhost:5181/ \
    -H "Content-Type: application/json" \
    -H "Accept: application/json, text/event-stream" \
    -H "Mcp-Session-Id: $session_id" \
    -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"poll_until_drained","arguments":{"timeoutSeconds":120,"minimumExpectedCompleted":100},"_meta":{"progressToken":"drain-1"}}}' | \
  tee /tmp/drain_response.txt | \
  grep -o '"message":"[^"]*"' | \
  sed 's/"message":"\(.*\)"/\1/' | \
  while read line; do echo "  ▶ $line"; done
}

# Call it with session ID
DRAIN_RESPONSE=$(cat /tmp/drain_response.txt)
parse_drain_progress "$SESSION_ID"
```

**Key points:**
- The `-N` (`--no-buffer`) flag is required so curl streams SSE events as they arrive
- `tee /tmp/drain_response.txt` captures the full response while displaying progress
- The grep/sed pipeline extracts progress messages and displays them with formatting
- **Always pass `minimumExpectedCompleted`** (typically 100 for default, 1000 for large runs) to prevent false drain detection while k6 is still submitting

Intermediate progress notifications (SSE events during polling):
```
event: message
data: {"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":"drain-1","progress":45,"total":100,"message":"Poll 3: 450/1000 processed (45%), outbox pending: 50, inbox pending: 500 [waiting for 550 more]"}}
```

Final response when drained:
```
event: message
data: {"result":{"content":[{"type":"text","text":"{\"isDrained\":true,\"pollCount\":15,\"outboxPending\":0,\"inboxPending\":0,\"completed\":1000,\"failed\":0}"}]},"id":3,"jsonrpc":"2.0"}
```

## 4. Assert results (MCP tool: `get_assertion_results`)

Runs the full assertion suite once drained. Call only after `poll_until_drained` returns `isDrained: true`.

Pass `expectedUnique` equal to the `TransactionCount` that was used (default: 100).

```bash
curl -s -X POST http://localhost:5181/ \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -H "Mcp-Session-Id: $SESSION_ID" \
  -d '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"get_assertion_results","arguments":{"expectedUnique":100}}}'
```

Assert that the response content contains `"allPassed":true`. Inspect individual checks on failure.

## 5. Inspect on failure (MCP tools: `get_*_inbox`, `get_*_outbox`)

If assertions fail, use these tools to inspect message state:

- `get_corebank_inbox` — CoreBank inbox messages (received transactions)
- `get_corebank_outbox` — CoreBank outbox messages (domain events published)
- `get_payments_inbox` — Payments inbox messages (received domain events)
- `get_payments_outbox` — Payments outbox messages (sent payment requests)

Example:
```bash
curl -s -X POST http://localhost:5181/ \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -H "Mcp-Session-Id: $SESSION_ID" \
  -d '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"get_corebank_inbox","arguments":{"limit":20,"status":"Failed"}}}'
```

All `get_*` tools accept `limit` (1–100, default 20) and optional `status` filter (Pending/Processing/Completed/Failed).

## 6. Analyze traces

After assertions complete (pass or fail), invoke the **corebank-trace-analysis** skill to analyze OpenTelemetry traces from this run. Pass the test start/end timestamps so the skill can scope its queries correctly.

## 7. Stop

See **aspire-launch** skill. Stop both AppHosts (LoadTests first, then regular):

```bash
aspire stop --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --non-interactive
aspire stop --apphost CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj --non-interactive
```

## MCP Server Implementation

LoadTestSupport implements a **Streamable HTTP MCP server** using `ModelContextProtocol.AspNetCore` v1.2:
- Registered in Program.cs with `.AddMcpServer().WithHttpTransport().WithTools<LoadTestTools>()`
- Mapped at root path via `app.MapMcp()` — the endpoint is `http://localhost:5181/`
- Available only when LoadTestSupport is running (started automatically by the LoadTests AppHost)
- Responses use SSE format: `event: message\ndata: {json-rpc response}`
- Requires session: send `initialize` first, then include `Mcp-Session-Id` header on all calls

### Why NOT to use Aspire MCP CLI

The `aspire mcp` CLI command interacts with the Aspire control plane's MCP server, NOT with individual service endpoints like LoadTestSupport. Commands like `aspire mcp call loadtest-support reset_database` will fail. Always use HTTP POST to `http://localhost:5181/` as shown above.
