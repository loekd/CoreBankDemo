#!/usr/bin/env bash
# Reproduces: Dev Proxy 3.2.0 stops serving after its own
# restart-on-config-change. Requires: devproxy 3.2.0, python3, curl.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
CFG="$HERE/devproxyrc.json"

req() {
  # --noproxy '' so a local NO_PROXY covering 127.0.0.1 cannot make curl
  # silently bypass the proxy and invalidate the measurement.
  curl -s -o /dev/null -w "   HTTP %{http_code} in %{time_total}s\n" \
    --noproxy '' -x http://127.0.0.1:8000 http://127.0.0.1:5032/api/ping
}

set_latency() {
  python3 - "$CFG" "$1" "$2" <<'PY'
import json, os, sys
path, lo, hi = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
d = json.load(open(path))
d["latency"] = {"minMs": lo, "maxMs": hi}
f = open(path, "w")           # in-place truncate+write, same inode
json.dump(d, f, indent=2)
f.flush(); os.fsync(f.fileno()); f.close()
PY
}

set_latency 500 600
python3 "$HERE/target.py" & TARGET=$!
sleep 2
devproxy --config-file "$CFG" --no-first-run > "$HERE/devproxy.log" 2>&1 &
PROXY=$!
for _ in $(seq 1 60); do [ -s "$HERE/devproxy.log" ] && break; sleep 1; done
sleep 3

echo "STEP 1 - baseline, config latency 500-600ms (expect ~0.5s):"
req; req

echo
echo "STEP 2 - edit config in place to 3000-3500ms; do NOT restart the proxy"
set_latency 3000 3500
sleep 8

echo "STEP 3 - same requests (expect ~3.2s; ACTUAL: HTTP 000, connection closed):"
req; req

echo
echo "STEP 4 - kill the proxy, start a NEW process with the SAME config:"
kill "$PROXY" 2>/dev/null; sleep 3
devproxy --config-file "$CFG" --no-first-run > "$HERE/devproxy-fresh.log" 2>&1 &
PROXY2=$!
for _ in $(seq 1 60); do [ -s "$HERE/devproxy-fresh.log" ] && break; sleep 1; done
sleep 3
echo "   (expect ~3.2s - proves the config is valid):"
req; req

kill "$PROXY2" "$TARGET" 2>/dev/null
echo
echo "Proxy log around the restart:"
grep -A3 "Configuration file changed" "$HERE/devproxy.log" || true
