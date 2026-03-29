---
name: aspire-mcp
description: Run CoreBankDemo AppHosts with Aspire MCP and safely restart changed resources.
---

## Purpose

Use this skill when working on `CoreBankDemo` and you need to run either AppHost, make code changes, and restart only the impacted services without restarting everything.

## AppHosts

- Regular app: `CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj`
- Load test app: `CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj`

## Default workflow (recipe)

1. Start the intended AppHost if not already running:
   - Regular: `dotnet run --project CoreBankDemo.AppHost`
   - Load test: `dotnet run --project CoreBankDemo.LoadTests`
2. Select the AppHost in Aspire MCP:
   - Call `aspire_list_apphosts`
   - Call `aspire_select_apphost` with the target `.csproj` path
3. Inspect state:
   - Call `aspire_list_resources`
   - If a resource is not running/healthy, check `aspire_list_console_logs`
4. Make code changes.
5. Restart only affected resource(s):
   - API code change -> restart `payments-api` or `corebank-api`
   - Load test support change -> restart `loadtest-support`
   - AppHost wiring/config change -> restart the full AppHost process
6. Verify:
   - `aspire_list_resources` for running/healthy
   - `aspire_list_console_logs` for startup errors
   - Optionally run `dotnet build` or relevant load test

## Resource restart map

- `CoreBankDemo.PaymentsAPI/*` -> restart `payments-api`
- `CoreBankDemo.CoreBankAPI/*` -> restart `corebank-api`
- `CoreBankDemo.LoadTestSupport/*` -> restart `loadtest-support`
- `k6/script.js` or load-test scenario settings -> rerun the load-test AppHost or restart `k6`
- `CoreBankDemo.AppHost/AppHost.cs` or `CoreBankDemo.LoadTests/AppHost.cs` -> full AppHost restart
- `dapr/components/*` or `dapr/components-loadtest/*` -> restart affected API + sidecar resources (or restart AppHost if unsure)

## Fast paths

### Regular AppHost (chaos/resilience debugging)

1. Run `CoreBankDemo.AppHost`.
2. Select the regular AppHost.
3. Ensure `payments-api`, `corebank-api`, and `devproxy` are running.
4. After edits, restart only changed API.

### Load-test AppHost (k6 correctness/perf)

1. Run `CoreBankDemo.LoadTests`.
2. Select the load-test AppHost.
3. Ensure `payments-api`, `corebank-api`, `loadtest-support`, and `k6` are running as expected.
4. After edits, restart changed resource, then rerun load test if needed.

## Diagnostics shortcuts

- AppHost not visible in MCP:
  - Start AppHost with `dotnet run --project ...`.
  - Call `aspire_list_apphosts` again.
- Resource not healthy:
  - Check `aspire_list_console_logs` for that resource first.
  - Use `aspire_execute_resource_command` with `restart` (or `start` if stopped).
- Need deeper telemetry:
  - Use `aspire_list_traces` and `aspire_list_trace_structured_logs`.

## Guardrails

- Prefer targeted resource restarts over full AppHost restarts.
- Keep behavior identical between regular and load-test AppHosts unless explicitly testing differences.
- If a change touches cross-cutting infrastructure (AppHost, Dapr components, shared defaults), do a full AppHost restart.
