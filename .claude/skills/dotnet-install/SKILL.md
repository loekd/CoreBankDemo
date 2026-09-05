---
name: dotnet-install
description: |
  Install the .NET SDK on PATH in a fresh CoreBankDemo sandbox, using the mirror that is not blocked by network policy.

  **When to use:**
  - In a new sandbox where `dotnet --version` reports nothing, before building or running anything.
  - When `dotnet` exists but builds die with `csc exited with code 132` / "Illegal instruction" — that is a bad SDK build, not a code problem. Reinstall per this recipe.

  **When NOT to use:**
  - Do NOT run this on the macOS host — it is sandbox/Linux-specific (`/opt`, `sudo`, `/etc/sandbox-persistent.sh`).
  - Do NOT use if `dotnet --version` already reports a working 10.0.4xx — run `dotnet tool restore` (see `build`) and move on.
  - Do NOT use `dotnet-install.sh` from `dot.net` / `builds.dotnet.microsoft.com`. Those are 403-blocked here; see below.
---

# Installing the .NET SDK in a CoreBankDemo sandbox

## 0. Check first (idempotent)

```bash
dotnet --version
```

If it prints `10.0.400` (or any working 10.0.4xx), stop — nothing to do.

## 1. Pick the version: 10.0.4xx, not 10.0.1xx

The repo targets `net10.0` and has **no `global.json`**, so any 10.0.x SDK is *accepted* — but not
every one of them *works* here.

**10.0.100 is broken in this sandbox.** Its `csc` and MSBuild worker processes die with SIGILL
(exit 132) on roughly two-thirds of builds, hitting a different random project each run, sometimes
crashing `dotnet build` outright before it prints anything. It is not a code, project, or NuGet
problem, and it is not worth debugging: `DOTNET_EnableHWIntrinsic`, `DOTNET_TieredCompilation`,
`DOTNET_JITMinOpts`, `DOTNET_PROCESSOR_COUNT` and `DOTNET_EnableWriteXorExecute` were all tried and
none of them fix it. **10.0.400 has zero crashes** under the same load.

The dev container alongside this sandbox (see §5) also uses a 10.0.4xx SDK — matching it is the
intent, not a coincidence.

## 2. Download — the official installer is blocked

`https://dot.net/v1/dotnet-install.sh` redirects to `builds.dotnet.microsoft.com`, which returns:

```
Blocked by network policy: domain builds.dotnet.microsoft.com:443
  detail: no matching allow rule — blocked by default deny policy
```

`dotnetcli.blob.core.windows.net` serves the same artifacts and **is** allowed. A plain `curl -L`
of the ~222 MB tarball dies part-way (the proxy cuts long single transfers), so pull it in 8 MB
ranges. Unlike the Dev Proxy download, the blob URL needs no redirect resolution.

```bash
V=10.0.400
RID=$([ "$(uname -m)" = aarch64 ] && echo linux-arm64 || echo linux-x64)
URL="https://dotnetcli.blob.core.windows.net/dotnet/Sdk/$V/dotnet-sdk-$V-$RID.tar.gz"
TAR=/tmp/dotnet-sdk-$V.tar.gz

# Total size comes from the range probe itself — works for any version.
TOTAL=$(curl -sS -r 0-0 -o /dev/null -D - "$URL" \
        | grep -i '^content-range' | tr -d '\r' | sed 's#.*/##')

CHUNK=$((8*1024*1024)); : > "$TAR"; start=0
while [ "$start" -lt "$TOTAL" ]; do
  end=$((start + CHUNK - 1)); [ "$end" -ge "$TOTAL" ] && end=$((TOTAL - 1))
  want=$((end - start + 1)); ok=0
  for a in 1 2 3 4 5; do
    # Fetch to a scratch file first — appending straight into $TAR would leave a
    # partial chunk behind if curl dies mid-transfer, and the retry would then
    # duplicate those bytes. -f so an HTTP error body is never mistaken for data.
    if curl -fsS -m 180 -r "${start}-${end}" -o /tmp/dn.part "$URL" \
       && [ "$(stat -c %s /tmp/dn.part)" -eq "$want" ]; then ok=1; break; fi
    sleep 2
  done
  [ "$ok" -eq 1 ] || { echo "FAILED at offset $start"; exit 1; }
  cat /tmp/dn.part >> "$TAR"; start=$((end + 1))
done
rm -f /tmp/dn.part
```

## 3. Verify the checksum — do not skip this

Microsoft publishes a `.sha512` next to the artifact on the same allowed host. A chunked download
through a proxy that intermittently 502s is exactly the situation where silent corruption would
otherwise reach the disk.

```bash
EXPECTED=$(curl -sS "$URL.sha512" | tr -d '[:space:]')
ACTUAL=$(sha512sum "$TAR" | cut -d' ' -f1)
[ "$EXPECTED" = "$ACTUAL" ] && echo "checksum ok" || { echo "MISMATCH — do not install"; exit 1; }
```

## 4. Install

```bash
sudo mkdir -p /opt/dotnet
sudo tar -xzf "$TAR" -C /opt/dotnet
sudo ln -sfn /opt/dotnet/dotnet /usr/local/bin/dotnet
```

Persist it, guarded by a marker variable so re-sourcing stays idempotent:

```bash
grep -q SBX_DOTNET_PATH_DONE /etc/sandbox-persistent.sh || \
sudo tee -a /etc/sandbox-persistent.sh >/dev/null <<'EOF'

# .NET SDK for CoreBankDemo (net10.0)
if [ -z "${SBX_DOTNET_PATH_DONE:-}" ]; then
  export DOTNET_ROOT=/opt/dotnet
  export PATH="/opt/dotnet:$PATH"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  export SBX_DOTNET_PATH_DONE=1
fi
EOF
```

**Warning:** `/etc/sandbox-persistent.sh` is sourced before EVERY bash command. Only put `export`
statements in it — never shell completion scripts, which break the bash tool completely.

## 5. There is a VS Code dev container running alongside this sandbox

This trips up diagnosis badly, so know it before you debug anything.

A dev container runs in parallel with C# Dev Kit, a Roslyn language server, and vstest hosts. It
has **its own .NET SDK at `/usr/share/dotnet/sdk/<version>`** — a path in *its* mount namespace,
so `ls /usr/share/dotnet` fails here while `ps` happily shows processes running from it. A stray
`VBCSCompiler` from a path that "does not exist" is this, not a corrupted install.

Its `vscode` user **maps to the same UID as this sandbox's `agent`**, so process-killing crosses
the boundary:

```bash
# NEVER — matches the dev container's processes, and -f also matches your own wrapper
pkill -f dotnet

# Check who owns it first, then match the exact name only
ps -eo pid,user,cmd | grep -iE "dotnet|vstest|VBCSCompiler" | grep -v grep
pkill -x VBCSCompiler
```

Prefer `dotnet build-server shutdown`, which only touches your own build servers.

## 6. Verify

```bash
dotnet --list-sdks
bash -l -c "dotnet --version"     # confirm login shells resolve it too
```

The real acceptance test is a build that does not crash — run it more than once, because the
10.0.100 failure mode is intermittent:

```bash
cd /Users/loekd/projects/CoreBankDemo
dotnet tool restore          # required — see the `build` skill
dotnet restore CoreBankDemo.sln
for i in 1 2 3; do
  dotnet build CoreBankDemo.sln --no-restore -v minimal >/tmp/b_$i.log 2>&1
  echo "exit $?  sigill: $(grep -cE 'MSB6006|MSB4166' /tmp/b_$i.log)"
done
```

Expect `exit 0` and `sigill: 0` three times. Any `MSB6006 ... exited with code 132` or
`MSB4166 Child node ... exited prematurely` means you are on a bad SDK — go back to §1.

## Footguns

- **Do not diagnose SIGILL as a code problem.** It hits random projects, and "which project failed"
  changes every run. That randomness *is* the signal.
- **Do not chase it with `DOTNET_*` JIT knobs.** All the obvious ones were tried; none work.
  `DOTNET_EnableHWIntrinsic=0` makes it *worse* — the host then crashes instantly at startup.
- **A clean `gzip -t` is not enough** on its own to trust the download; verify the SHA512.
- **`dotnet restore` succeeding proves nothing** about SDK health — restore survives on a broken
  SDK that cannot compile a single project.
