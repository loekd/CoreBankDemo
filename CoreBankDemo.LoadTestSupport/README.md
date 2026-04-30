# CoreBank LoadTestSupport

This project contains support infrastructure for load testing and diagnostics. **It does not contain any production code** and should only be used in test environments.

## Purpose

LoadTestSupport provides read-only access to both CoreBank and Payments databases for monitoring, assertions, and test orchestration. It exposes endpoints that allow load testing tools to:

- Reset database state to a clean baseline
- Monitor inbox/outbox message processing
- Verify exactly-once delivery guarantees
- Assert correctness of account balances and transaction processing

## Usage with k6

The k6 load tests (`k6/script.js`) use LoadTestSupport endpoints to orchestrate end-to-end test scenarios:

1. **Setup phase**: Calls `/reset` to truncate inbox/outbox tables and reset all load test accounts to their initial balance
2. **Load phase**: Generates payment traffic against the Payments API (not LoadTestSupport)
3. **Teardown phase**: Polls `/assert/drain` until all messages are processed, then calls `/assert/results` (optionally with `?expectedUnique=<n>`) to verify exactly-once semantics, balance conservation, and correctness

## Available REST Endpoints

- `/reset` - Reset database to clean state
- `/assert/drain` - Poll until inbox/outbox are fully drained
- `/assert/results` - Run full assertion suite (supports optional `expectedUnique` query parameter)
- `/corebank/inbox` - View CoreBank inbox messages
- `/corebank/outbox` - View CoreBank outbox messages
- `/payments/inbox` - View Payments inbox messages
- `/payments/outbox` - View Payments outbox messages

## MCP Server (AI Agent Interface)

LoadTestSupport embeds an MCP (Model Context Protocol) server that exposes the same capabilities as the REST endpoints, optimized for AI agent consumption. The MCP server runs on the same process via **Streamable HTTP** transport.

### Connecting an AI Agent

The MCP endpoint is available at `{LoadTestSupport_URL}/mcp`. When running via the Load Test AppHost, the URL is typically `http://localhost:5181/mcp`.

**Claude Code / VS Code / Visual Studio** — add to your MCP configuration:

```json
{
  "servers": {
    "corebank-loadtest": {
      "url": "http://localhost:5181/mcp"
    }
  }
}
```

### Available Tools

| Tool | Description |
|------|-------------|
| `reset_database` | ⚠️ Destructive: truncates all inbox/outbox tables and resets account balances to 10M EUR |
| `poll_until_drained` | Waits until all messages are processed (polls internally every 2s, configurable timeout) |
| `get_assertion_results` | Runs the full assertion suite: exactly-once, no duplicates, balance conservation |
| `get_corebank_inbox` | Returns recent CoreBank inbox messages with optional status filter |
| `get_corebank_outbox` | Returns recent CoreBank outbox messages (domain events) |
| `get_payments_inbox` | Returns recent Payments inbox messages (received events) |
| `get_payments_outbox` | Returns recent Payments outbox messages (queued payments) |

### Intended Agent Workflow

1. Call `reset_database` to ensure clean state
2. Run k6 load test via bash/shell tool
3. Call `poll_until_drained` (timeout 60–120s) — tool handles polling internally
4. Call `get_assertion_results` with `expectedUnique` = number of unique payments from k6
5. If assertions fail, inspect inbox/outbox using the inspection tools
