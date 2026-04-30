---
name: aspire-mcp
description: "Use Aspire MCP tools to inspect resource state, logs, and traces in CoreBankDemo."
---

Requires an AppHost to be running. See **aspire-launch** skill.

## CLI inspection

```bash
aspire ps --non-interactive
aspire describe --non-interactive
aspire logs <resource> --non-interactive
aspire otel logs <resource> --non-interactive
aspire otel traces <resource> --non-interactive
aspire mcp tools --non-interactive
aspire mcp call <resource> <tool> --input '{}' --non-interactive
```

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
