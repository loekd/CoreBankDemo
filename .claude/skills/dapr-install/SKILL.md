---
name: dapr-install
description: |
  Install the Dapr CLI and initialize the Dapr runtime in a CoreBankDemo sandbox, with the runtime version matched to the Dapr SDK packages the code references.

  **When to use:**
  - In a new sandbox where `dapr --version` reports nothing, before running an AppHost that wires Dapr sidecars.
  - When `dapr --version` reports a Runtime version whose major.minor differs from the `Dapr.*` package versions in `Directory.Packages.props`.

  **When NOT to use:**
  - Do NOT run this on the macOS host — it is sandbox/Linux-specific.
  - Do NOT use if CLI and runtime are present and the runtime major.minor already matches the SDK packages.
  - Do NOT run `dapr uninstall` to "fix" a version mismatch without reading §4 first — it removes a working install before proving the replacement is obtainable.
---

# Installing Dapr in a CoreBankDemo sandbox

## 0. Check first (idempotent)

```bash
dapr --version                      # CLI version + Runtime version
docker ps --format '{{.Names}}' | grep dapr
```

A healthy install reports both versions and shows four containers: `dapr_placement`,
`dapr_scheduler`, `dapr_redis`, `dapr_zipkin`.

## 1. The version rule: runtime follows the SDK packages

**The code is the source of truth.** `Directory.Packages.props` pins the Dapr .NET SDK, and the
runtime you initialize must match its **major.minor**:

```bash
grep -E 'PackageVersion Include="Dapr\.' Directory.Packages.props
#   <PackageVersion Include="Dapr.AspNetCore" Version="1.18.5" />
#   <PackageVersion Include="Dapr.Client"     Version="1.18.5" />
#                                                      ^^^^ runtime must be 1.18.x
```

The AppHost does **not** pin a runtime version anywhere, so nothing else will catch a mismatch for
you — `CommunityToolkit.Aspire.Hosting.Dapr` just launches whatever `daprd` is installed.

Patch numbers are versioned independently between SDK and runtime, so do not try to match the
patch — match `1.18` and take the newest patch of that line:

```bash
curl -sS "https://api.github.com/repos/dapr/dapr/releases?per_page=100" \
  | grep -oE '"tag_name": "v1\.18\.[0-9]+"' | sort -u -V | tail -1
```

**Prefer upgrading the SDK packages over pinning the runtime backwards.** If the installed runtime
is newer than the packages, bump `Dapr.AspNetCore` / `Dapr.Client` to the matching line rather than
downgrading the runtime.

## 2. Install the CLI

The install script and its GitHub release assets are reachable; no proxy workaround needed.

```bash
curl -fsSL https://raw.githubusercontent.com/dapr/cli/master/install/install.sh | sudo bash
dapr --version    # CLI version populated, "Runtime version: n/a" until step 3
```

The CLI lands in `/usr/local/bin/dapr`, already on PATH — no `/etc/sandbox-persistent.sh` entry
needed (unlike dotnet and devproxy). The CLI does **not** have to match the runtime line; a 1.18
CLI can initialize a 1.17 runtime via `--runtime-version`.

## 3. Initialize the runtime

Pass the version explicitly rather than taking the CLI default, so the result is reproducible:

```bash
dapr init --runtime-version 1.18.3
```

Expect `✅ Success! Dapr is up and running` and the four containers. `daprd` is installed to
`~/.dapr/bin`.

## 4. Docker Hub is only half-reachable — check before you uninstall

`registry-1.docker.io` responds, but **`auth.docker.io` intermittently times out**, so
`dapr init` can fail mid-flight on the anonymous-token fetch:

```
❌ Unable to find image 'daprio/dapr:1.18.3' locally
docker: failed to fetch anonymous token: ... net/http: timeout awaiting response headers
```

`dapr init` pulls three images (`daprio/dapr`, `redis:6`, `openzipkin/zipkin`). **Before running
`dapr uninstall` for any reason, confirm the image you will need is already cached**, or you can
strand yourself with no working install and no way to pull one:

```bash
docker images | grep -iE "daprio/dapr|redis|zipkin"
```

If the exact tag is listed, `dapr init --runtime-version <that tag>` works with no network. If it
is not, get the pull working *first*:

```bash
docker pull daprio/dapr:1.18.3      # retry; the auth timeout is intermittent, not permanent
```

## 5. Verify

```bash
dapr --version
docker ps --format '{{.Names}}\t{{.Status}}' | grep dapr
```

Both versions populated and four containers `Up`. Cross-check the runtime against the packages one
last time — the whole point of this skill:

```bash
grep -E 'Dapr\.(Client|AspNetCore)"' Directory.Packages.props   # e.g. 1.18.5
dapr --version | grep Runtime                                   # must be 1.18.x
```

## Footguns

- **`dapr uninstall` is destructive and not always reversible here.** It removes containers and
  `~/.dapr/bin`. Combined with the Docker Hub auth flakiness in §4, running it "just to re-pin the
  version" can leave Dapr broken. Check the image cache first, and ask before running it.
- **A partial `init` leaves a half-state.** If `init` fails after writing `daprd`, a retry refuses
  with `daprd file already exists, please run 'dapr uninstall' first`. Some containers
  (`dapr_redis`, `dapr_zipkin`) may still be running from the previous install while
  `dapr_placement` / `dapr_scheduler` are gone. `dapr uninstall` then `dapr init` clears it —
  safe at that point, because the state is already broken.
- **Do not assume the CLI default runtime is the right one.** `dapr init` with no flag installs the
  newest runtime, which may drift ahead of the SDK packages and silently create the mismatch this
  skill exists to prevent.
