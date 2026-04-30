---
name: aspire-mcp
description: |
  Use Aspire MCP tools to inspect resource state, logs, and traces in CoreBankDemo.
  
  **When to use:**
  - When an AppHost is already running.
  - When you need to inspect the state, logs, or distributed traces of resources managed by an Aspire AppHost in CoreBankDemo.
  - When you want to use Aspire CLI or MCP tools to diagnose, monitor, or interact with running services.
  
  **When NOT to use:**
  - Do NOT use if no AppHost is running (start one with the aspire-launch skill first).
  - Do NOT use for direct database inspection, application-level debugging, or for actions outside the scope of Aspire-managed resources.
  - Do NOT use for running or stopping AppHosts themselves—use the aspire-launch skill for that purpose.
---
---

Requires an AppHost to be running. See **aspire-launch** skill.

## MCP tools (via MCP client)

- `aspire_list_apphosts` — list running AppHosts
- `aspire_select_apphost` — target an AppHost by `.csproj` path
- `aspire_list_resources` — resource names and health status
- `aspire_list_console_logs` — console output for a resource
- `aspire_execute_resource_command` — restart/start/stop a resource
- `aspire_list_traces` — distributed traces
- `aspire_list_trace_structured_logs` — structured logs per trace

## MCP setup (one-time, interactive)

```bash
aspire mcp init
```

Enable **Install Aspire MCP server**. MCP command: `{ "command": ["aspire", "agent", "mcp"] }`.

## Resource restart map

| Change | Restart |
|---|---|
| `CoreBankDemo.PaymentsAPI/*` | `payments-api` |
| `CoreBankDemo.CoreBankAPI/*` | `corebank-api` |
| `CoreBankDemo.LoadTestSupport/*` | `loadtest-support` |
| `CoreBankDemo.AppHost/AppHost.cs` or Dapr components | full AppHost |
