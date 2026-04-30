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
aspire start --apphost <project-path> --no-build --non-interactive
```

Use `--isolated` in worktrees or shared environments.

## Stop

```bash
aspire stop --apphost <project-path> --non-interactive
```

## Rules

- Never use `dotnet run` to start AppHosts — use `aspire start`.
- Always specify `--apphost` — this repo has two AppHosts.
- Use `aspire wait <resource>` before probing APIs or running assertions.
