---
name: devproxy-install
description: |
  Install Microsoft Dev Proxy 3.2.0 on PATH in this sandbox so the Regular CoreBankDemo AppHost can start.

  **When to use:**
  - Before starting `CoreBankDemo.AppHost` (Regular profile) when `devproxy --version` is not on PATH or doesn't report 3.2.0.
  - When the Regular AppHost fails to start because `AddDevProxyExecutable("devproxy")` can't find the binary.

  **When NOT to use:**
  - Do NOT use for `CoreBankDemo.LoadTests` — that AppHost has no Dev Proxy wiring at all; this only affects the Regular AppHost.
  - Do NOT run this recipe on the macOS host — it is sandbox/Linux-specific (`/opt`, `sudo`, `/etc/sandbox-persistent.sh`).
  - Do NOT use if `devproxy --version` already reports 3.2.0 — skip straight to `aspire-launch`.
---

# Installing Dev Proxy in a CoreBankDemo sandbox

## Version: 3.2.0, matching the repo's config schemas

The repo's four config files declare `schemas/v3.2.0`:

```
CoreBankDemo.AppHost/devproxy/config/devproxyrc.json        -> v3.2.0/rc.schema.json
CoreBankDemo.AppHost/devproxy/config/devproxy-errors.json   -> v3.2.0/genericrandomerrorplugin.errorsfile.schema.json
CoreBankDemo.LoadTests/devproxy/config/devproxyrc.json      -> (same pair)
CoreBankDemo.LoadTests/devproxy/config/devproxy-errors.json
```

Keep the binary and both schema pairs on the same version. If you move to a future major, migrate
all four files in the same change — and note the **main config schema was renamed** in v3:
`devproxyrc.schema.json` (v2) → `rc.schema.json` (v3). The errors-file schema kept its name. The v2
config also pointed at the old `microsoft/dev-proxy` GitHub org; the current org is
`dotnet/dev-proxy`.

## 0. Check if already installed (idempotent)

```bash
command -v devproxy && devproxy --version
```

If this prints `3.2.0`, stop here — nothing to do.

## 1. Download (must be chunked)

A plain `curl -L` of the ~53 MB release asset reliably fails with `curl: (52) Empty reply from
server` — the sandbox proxy cuts long single transfers, the host itself is not blocked (ranged
requests return 206 fine). Resolve the redirect once, then pull it in 4 MB ranges:

```bash
VERSION=3.2.0
RID=$([ "$(uname -m)" = aarch64 ] && echo linux-arm64 || echo linux-x64)
URL="https://github.com/dotnet/dev-proxy/releases/download/v${VERSION}/dev-proxy-${RID}-v${VERSION}.zip"
ZIP=/tmp/devproxy-$VERSION.zip

# The github.com URL 302s to a signed release-assets.githubusercontent.com URL (valid ~40 min).
resolve() { curl -sS -o /dev/null -w '%{redirect_url}' "$URL"; }
SIGNED=$(resolve)
# Total size comes from the range probe itself — no second API call, works for any version.
TOTAL=$(curl -sS -r 0-0 -o /dev/null -D - "$SIGNED" \
  | grep -i '^content-range' | tr -d '\r' | sed 's#.*/##')

CHUNK=$((4*1024*1024)); : > "$ZIP"; start=0
while [ "$start" -lt "$TOTAL" ]; do
  end=$((start + CHUNK - 1)); [ "$end" -ge "$TOTAL" ] && end=$((TOTAL - 1))
  want=$((end - start + 1)); ok=0
  for attempt in 1 2 3 4 5; do
    # Fetch to a scratch file first. Appending straight into $ZIP would leave a partial
    # chunk behind when curl dies mid-transfer, and the retry would then duplicate those
    # bytes into the archive. -f so an HTTP error body is never mistaken for data.
    if curl -fsS -m 120 -r "${start}-${end}" -o /tmp/dp.part "$SIGNED" \
       && [ "$(stat -c %s /tmp/dp.part)" -eq "$want" ]; then ok=1; break; fi
    SIGNED=$(resolve)
  done
  [ "$ok" -eq 1 ] || { echo "FAILED at offset $start"; exit 1; }
  cat /tmp/dp.part >> "$ZIP"; start=$((end + 1))
done
rm -f /tmp/dp.part

python3 -c "import zipfile; assert zipfile.ZipFile('$ZIP').testzip() is None; print('zip ok')"
```

## 2. Install (~136 MB on disk)

```bash
sudo rm -rf /opt/devproxy          # if replacing an older version
sudo mkdir -p /opt/devproxy
sudo unzip -q -o "$ZIP" -d /opt/devproxy
sudo chmod +x /opt/devproxy/devproxy /opt/devproxy/*.sh
sudo ln -sfn /opt/devproxy/devproxy /usr/local/bin/devproxy
```

**The `chmod +x` is not optional.** The zip does not carry the executable bit, so a freshly
extracted `devproxy` is `-rw-r--r--`. Launched via `nohup ... &` it dies instantly and writes a
**zero-byte log**, which reads like a crash rather than a permissions problem.

Append it to `/etc/sandbox-persistent.sh`, guarded by a marker variable so re-sourcing stays
idempotent, and skip if it is already there:

```bash
grep -q SBX_DEVPROXY_PATH_DONE /etc/sandbox-persistent.sh || \
sudo tee -a /etc/sandbox-persistent.sh >/dev/null <<'EOF'

# Microsoft Dev Proxy — the AppHost starts it via AddDevProxyExecutable("devproxy").
if [ -z "${SBX_DEVPROXY_PATH_DONE:-}" ]; then
  export PATH="/opt/devproxy:$PATH"
  export SBX_DEVPROXY_PATH_DONE=1
fi
EOF
```

**Warning:** `/etc/sandbox-persistent.sh` is sourced before EVERY bash command. Only put `export`
statements in it — never shell completion scripts, which break the bash tool completely (every
command returns silently, `echo`/`pwd` produce no output).

## 3. Verify

```bash
devproxy --version                    # expect 3.2.0
bash -l -c "devproxy --version"       # confirm login shells resolve it too
```

Run it against the repo's own config — this is the real acceptance test:

```bash
cd /Users/loekd/projects/CoreBankDemo
nohup devproxy --config-file CoreBankDemo.AppHost/devproxy/config/devproxyrc.json \
      --urls-to-watch "http://127.0.0.1:5032/*" --as-system-proxy false > /tmp/dp.log 2>&1 &
echo $! > /tmp/dp.pid
sleep 30 && cat /tmp/dp.log   # 3.x needs ~20-30s before the listener is up
```

**3.x starts noticeably slower than 2.x.** It prints nothing at all for the first ~20 seconds, so a
12-second wait (which was enough for 2.1.0) shows an empty log and looks like a failure. Give it 30.

Expect:

```
info    1 error responses loaded from .../devproxy-errors.json
info    Dev Proxy API listening on http://127.0.0.1:8897...
info    Dev Proxy listening on 127.0.0.1:8000...
```

Port 8000 matters — the AppHost injects `HTTP_PROXY=http://127.0.0.1:8000` into payments-api.
(`CoreBankDemo.LoadTests`'s config uses 8001, but nothing wires it.) There is no "new version
available" notice on 3.2.0; 2.1.0 nagged on every start.

Confirm the port (`ss`/`netstat` are not installed in this sandbox):

```bash
python3 -c "import socket;s=socket.socket();s.settimeout(3);s.connect(('127.0.0.1',8000));print('ok');s.close()"
```

Stop it:

```bash
kill "$(cat /tmp/dp.pid)"
```

## Footguns

- **Never `pkill -f devproxy`.** The bash tool's own wrapper command line contains the string
  `devproxy`, so this kills your wrapper shell instead (exit 144, no output). It would also reach
  the parallel VS Code dev container, which shares this sandbox's UID. Use a pidfile or
  `pkill -x devproxy`.
- **Don't start a second instance while one is running.** It fails with `Failed to bind to
  address http://127.0.0.1:8897: address already in use` — that's a self-collision, not a broken
  install. Check for a running instance first.
- **A zero-byte log means check the exec bit before anything else** (see §2).

## Side effect once installed

With `Features:UseDevProxy: true`, the AppHost starts Dev Proxy and the repo config runs
`GenericRandomErrorPlugin` at `rate: 5` against `http://127.0.0.1:5032/api/*`, so ~5% of
PaymentsAPI→CoreBankAPI calls get an injected 503/429/500 — that's the intended ADR-005 resilience
scenario, not a regression. Set `Features:UseDevProxy=false` for a clean demo run.

The AppHost config also carries `rateLimiting` (10 req/60s) and `latency` (20–200 ms) sections, but
**neither is in effect**: Dev Proxy only runs plugins listed in `plugins`, and that array holds
`GenericRandomErrorPlugin` alone. Startup confirms it — `1 error responses loaded from
devproxy-errors.json` and no rate-limiting or latency plugin line. Do not attribute slow or
throttled CoreBank calls to those sections. (The v3 `rc` schema sets `additionalProperties: true`,
so these dead sections still validate — schema validation will not flag them for you.)
