---
name: aspire-launch
description: "Start and stop CoreBankDemo AppHosts using the Aspire CLI."
---

## AppHosts

| AppHost | Project path |
|---|---|
| Regular dev | `CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj` |
| Load testing | `CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj` |

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
- Always specify `--apphost` — this repo has two AppHosts.
- Always use `aspire wait <resource>` before probing APIs or running assertions.
- Never use `sleep` to wait for health — use `aspire wait`.
