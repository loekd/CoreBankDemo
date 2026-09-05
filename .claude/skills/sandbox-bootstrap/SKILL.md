---
name: sandbox-bootstrap
description: |
  Bring a brand-new CoreBankDemo sandbox from empty to buildable: .NET SDK, Dapr, Dev Proxy, local tools, verified by a build and test run.

  **When to use:**
  - First thing in a fresh sandbox, when none of `dotnet`, `dapr`, `devproxy` are on PATH.
  - When the user says the sandbox is new and needs the toolchain installed.

  **When NOT to use:**
  - Do NOT use if the toolchain is already present — run the §0 check and skip to whatever is actually missing.
  - Do NOT use to install a single missing tool — go straight to `dotnet-install`, `dapr-install`, or `devproxy-install`.
  - Do NOT run on the macOS host; this is sandbox/Linux-specific.
---

# Bootstrapping a fresh CoreBankDemo sandbox

This is an ordering-and-verification wrapper. Each tool's real recipe, and its footguns, live in
its own skill — follow them rather than improvising.

## 0. What is actually missing?

```bash
for t in dotnet dapr devproxy; do
  printf '%-9s ' "$t"; command -v $t >/dev/null && $t --version 2>&1 | head -1 || echo "MISSING"
done
```

Install only what is missing. All three are independent — no ordering constraint between them —
but `dotnet` is the one everything else is verified through, so do it first.

## 1. .NET SDK → skill: `dotnet-install`

The single most important detail: **install 10.0.4xx, not 10.0.100**. 10.0.100 crashes with SIGILL
mid-build in this sandbox and will send you on a long, false debugging trail. The official
installer host is 403-blocked; the skill has the working mirror, the chunked download, and the
checksum step.

## 2. Dapr → skill: `dapr-install`

CLI install is straightforward. The part that needs care is the runtime version: it must match the
**major.minor of the `Dapr.*` packages** in `Directory.Packages.props`, and `dapr init` with no
flag installs the newest runtime, which may not match. Check the Docker image cache before any
`dapr uninstall` — `auth.docker.io` is flaky here.

## 3. Dev Proxy → skill: `devproxy-install`

Pinned to **3.2.0**, matching the `schemas/v3.2.0` declared by the repo's four Dev Proxy config
files. Keep binary and schemas on the same version; a future major means migrating all four configs
in the same change. Needed only by the Regular AppHost, not `CoreBankDemo.LoadTests`. Two things
bite here: the zip does not carry the executable bit (a missing `chmod +x` looks like a crash with
a zero-byte log), and 3.x prints nothing for its first ~20s of startup.

## 4. Local tools → skill: `build`

```bash
dotnet tool restore
```

Required: the PaymentsAPI build shells out to the Kiota local tool. Skipping this produces an
`MSB3073 ... kiota generate ... exited with code 1` failure that reads like an OpenAPI problem but
is not.

## 5. Verify the whole thing

The bootstrap is not done until a build and the test suite both pass. Run the build **more than
once** — the bad-SDK failure mode is intermittent, so a single green build does not prove the
toolchain is sound:

```bash
cd /Users/loekd/projects/CoreBankDemo
dotnet restore CoreBankDemo.sln
for i in 1 2 3; do
  dotnet build CoreBankDemo.sln --no-restore -v minimal >/tmp/boot_$i.log 2>&1
  echo "build $i: exit $?  sigill: $(grep -cE 'MSB6006|MSB4166' /tmp/boot_$i.log)"
done
dotnet test CoreBankDemo.sln --no-build 2>&1 | grep -E "^(Passed|Failed)!"
```

Expected: `exit 0  sigill: 0` three times, then eight `Passed!` lines (~1,113 tests). The
`Persistence.IntegrationTests` project needs Docker for its PostgreSQL container.

## 6. Report what landed

State the versions installed, since later diagnosis depends on them:

```bash
bash -l -c 'echo "dotnet:   $(dotnet --version)"
            echo "dapr:     $(dapr --version | tr "\n" " ")"
            echo "devproxy: $(devproxy --version)"'
docker ps --format '{{.Names}}\t{{.Status}}' | grep dapr
```

## Footguns

- **This sandbox is not alone.** A VS Code dev container runs beside it, sharing the same UID and
  carrying its own .NET SDK at a path invisible from here. Never `pkill -f` anything; see
  `dotnet-install` §5.
- **Confirm before destructive steps.** Bootstrapping is additive, but "fixing" it often is not
  (`dapr uninstall`, `rm -rf`, killing processes). Ask first — a failed re-install can leave the
  sandbox worse than the problem you set out to fix.
- **Do not persist anything but `export` lines** in `/etc/sandbox-persistent.sh`; it is sourced
  before every bash command, and a completion script there breaks the shell entirely.
