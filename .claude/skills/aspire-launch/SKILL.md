---
name: aspire-launch
description: |
  Start and stop CoreBankDemo AppHosts using the Aspire CLI.
  
  **When to use:**
  - When you need to start, stop, or wait for health of CoreBankDemo AppHosts using the Aspire CLI.
  - When you want to ensure services are running and healthy before interacting with APIs or running tests.
  
  **When NOT to use:**
  - Do NOT use for starting individual services outside of AppHosts.
  - Do NOT use `dotnet run` or `sleep` for orchestration or health checks—always use the Aspire CLI commands as described.
  - Do NOT use for inspecting logs, traces, or resource state—use the aspire-mcp skill for those tasks.
---
---

## AppHosts

| AppHost | Project path |
|---|---|
| Regular dev | `CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj` |
| Load testing | `CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj` |

## Preflight (before every start)

```bash
aspire ps
```

The two AppHosts both bind CoreBankAPI's stable ingress port 5032 (ADR-014) and cannot run side by side: the second one's PaymentsAPI silently forwards into the first one's CoreBank. If `aspire ps` lists an AppHost you did not start, stop and ask the user before touching it — it may belong to another session on this machine. If it lists the one you are about to start, `aspire stop` it first.

## Start

```bash
aspire start --apphost <project-path> --non-interactive
```

Use `--isolated` in worktrees or shared environments.

## Wait for healthy

After starting, use `aspire wait` to block until a resource is healthy:

```bash
aspire wait <resource-name> --non-interactive
```

Example:
```bash
aspire start --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --non-interactive
aspire wait loadtest-support --non-interactive
```

Do NOT use `sleep` to wait for services — always use `aspire wait`.

## Stop

```bash
aspire stop --apphost <project-path> --non-interactive
```

## Force kill (stuck processes)

If the AppHost is stuck or `aspire stop` doesn't work, kill DCP (the Aspire orchestrator):

```bash
killall dcp
```

## Rules

- Never use `dotnet run` to start AppHosts — use `aspire start`.
- Always run `aspire ps` before `aspire start`; never start while another AppHost is running (port 5032 collision).
- Always specify `--apphost` — this repo has two AppHosts.
- Always use `aspire wait <resource>` before probing APIs or running assertions.
- Never use `sleep` to wait for health — use `aspire wait`.
