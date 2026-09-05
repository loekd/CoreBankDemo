---
name: build
description: |
  Build and test CoreBankDemo from the command line, including the `dotnet tool restore` step the build depends on.

  **When to use:**
  - Any time you compile or test CoreBankDemo: `dotnet build`, `dotnet test`, or verifying a change.
  - When the build fails with `MSB3073: The command "dotnet tool run kiota generate ..." exited with code 1` — that is the missing tool restore.

  **When NOT to use:**
  - Do NOT use to start or orchestrate the running application — use `aspire-launch`.
  - Do NOT use to inspect logs or traces of a running AppHost — use `aspire-mcp`.
  - Do NOT use for the k6 load-test flow — use `load-test`.
---

# Building CoreBankDemo

## The order that works

```bash
cd /Users/loekd/projects/CoreBankDemo
dotnet tool restore                       # local tools — REQUIRED, see below
dotnet restore CoreBankDemo.sln           # NuGet packages
dotnet build CoreBankDemo.sln --no-restore
dotnet test  CoreBankDemo.sln --no-build
```

In a fresh sandbox `dotnet tool restore` is the step that is easy to forget and the one that
produces the most confusing failure.

## Why `dotnet tool restore` is mandatory

`CoreBankDemo.PaymentsAPI.csproj` runs Kiota as a **pre-build MSBuild step**, generating the
CoreBank API client from `CoreBankDemo.CoreBankAPI/OpenApi/corebank-api.json`. Kiota is a .NET
*local tool*, declared in `.config/dotnet-tools.json`:

```json
{ "tools": { "microsoft.openapi.kiota": { "version": "1.34.1", "commands": ["kiota"] } } }
```

Local tools are **not** installed by `dotnet restore` and are **not** part of the NuGet package
graph — they need their own restore. Without it the build fails on a project that looks unrelated
to tooling:

```
CoreBankDemo.PaymentsAPI.csproj(72,5): error MSB3073: The command
"dotnet tool run kiota generate --openapi ... --language csharp ..." exited with code 1.
```

That error means "the `kiota` command does not exist", not "the OpenAPI document is bad". Fix:

```bash
dotnet tool restore
```

It is idempotent and takes a second or two, so just run it whenever you are unsure.

## Verifying a change

Build alone is not sufficient evidence for a behavioural change; run the suite:

```bash
dotnet test CoreBankDemo.sln --no-build
```

All eight test projects should report `Passed!`, ~1,113 tests total. `CoreBankDemo.Persistence.
IntegrationTests` (213 of them) drives a **real PostgreSQL container** via Testcontainers, so
Docker must be up — those tests are the slowest and the first to fail if Docker is unhealthy.

## Expected warnings — do not "fix" these

`CoreBankDemo.ServiceDefaults.Tests` emits several **xUnit1051** analyzer warnings
("should use TestContext.Current.CancellationToken"). They are pre-existing and unrelated to
whatever you are changing. Leave them alone unless the task is specifically about them.

The build should otherwise be warning-free. In particular there should be **no NU1902/NU1903**
vulnerability warnings — if `MessagePack` warnings reappear, a package downgrade has crept in
(they came transitively via `Aspire.Hosting` → `StreamJsonRpc` and were cleared by moving Aspire
to 13.5.3+).

## If the build crashes rather than fails

`error MSB6006: "csc" exited with code 132` or `MSB4166: Child node "N" exited prematurely` is a
**SIGILL crash of the toolchain**, not a compile error. Tell-tale sign: the failing project changes
from run to run. This means the installed .NET SDK is a bad build for this sandbox — see the
`dotnet-install` skill (§1: use 10.0.4xx, not 10.0.100). Do not chase it as a code problem.

## Footguns

- **`dotnet restore` succeeding tells you nothing** about whether local tools are present, or
  whether the SDK can actually compile.
- **`--no-build` on `dotnet test` is only safe right after a successful build.** If you changed
  code since, drop the flag.
- **Do not run a broad `pkill` to clear stuck build processes.** A VS Code dev container shares
  this sandbox's UID and you will kill the user's IDE processes; use
  `dotnet build-server shutdown`. See `dotnet-install` §5.
