---
name: aspire
description: "Use for CoreBankDemo Aspire orchestration and MCP. This repo has two AppHosts: CoreBankDemo.AppHost (regular runs) and CoreBankDemo.LoadTests (automated load testing)."
---

# Aspire MCP Skill (CoreBankDemo)

Use Aspire CLI + MCP for distributed-app run/debug tasks in this repository.

## AppHosts (choose the right one)

| Scenario | AppHost project | What it runs |
|---|---|---|
| Regular development run | `CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj` | PaymentsAPI, CoreBankAPI, postgres, redis, jaeger, optional devproxy |
| Automated load-testing run | `CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj` | PaymentsAPI, CoreBankAPI, LoadTestSupport, k6, disposable postgres/redis, optional devproxy |

## Start commands

```bash
# Regular run
aspire start --apphost CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj --no-build --non-interactive

# Automated load test run
aspire start --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --no-build --non-interactive
```

## Inspect and control

```bash
aspire ps --non-interactive
aspire describe --non-interactive
aspire logs <resource> --non-interactive
aspire otel logs <resource> --non-interactive
aspire otel traces <resource> --non-interactive
aspire mcp tools --non-interactive
aspire mcp call <resource> <tool> --input '{"key":"value"}' --non-interactive
```

## Stop command

```bash
aspire stop --apphost <apphost-csproj-path> --non-interactive
```

## MCP setup (interactive)

```bash
aspire mcp init
```

Enable **Install Aspire MCP server**. Agent command pattern:

```json
{ "command": ["aspire", "agent", "mcp"] }
```

## Rules for future agents

- Do **not** use `dotnet run` for AppHost lifecycle; use `aspire start/stop`.
- Always pass `--apphost` because this repo has two AppHosts.
- Use `--isolated` in shared environments/worktrees.
- Use `aspire wait <resource>` before load assertions or API probing.
- Prefer `aspire docs search` / `aspire docs get` for Aspire docs.
