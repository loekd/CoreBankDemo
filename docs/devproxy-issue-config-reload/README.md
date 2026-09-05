# Dev Proxy stops serving after its own restart-on-config-change

> Ready-to-post issue for https://github.com/dotnet/dev-proxy/issues.
> Everything below the line is the issue body; `repro.sh`, `devproxyrc.json`
> and `target.py` in this folder are the attachments.

---

## Summary

When Dev Proxy detects a change to its configuration file it logs
`Configuration file changed. Restarting proxy...` and then reports that it is
listening again — but after that restart it no longer serves. The listening
socket accepts the TCP connection and immediately closes it, so every client
gets an empty reply. The proxy never recovers; it has to be killed and started
again.

Starting a **new** Dev Proxy process with the byte-identical configuration
works perfectly, which rules out the configuration itself.

The practical effect is that the documented file-watch behaviour (`--no-watch`
exists to *disable* "automatic restart on configuration file changes", so
watching is on by default) cannot be used to change plugin settings on a
running proxy. Any tool that edits a Dev Proxy config while the proxy is
running silently kills it.

## Version

```
$ devproxy --version
3.2.0
```

Linux x64 (container, .NET 10). Installed to `/opt/devproxy`, on `PATH`.

## Steps to reproduce

`repro.sh` in this folder does all of the below; it needs only `devproxy`,
`python3` and `curl`.

1. Start a trivial upstream on `127.0.0.1:5032` that answers `200` instantly
   (`target.py`).
2. Start Dev Proxy with `devproxyrc.json` — one `LatencyPlugin`, watching
   `http://127.0.0.1:5032/api/*`, `latency` set to `minMs: 500 / maxMs: 600`.
3. Send a request through the proxy. It is delayed ~500 ms, as configured.
4. Edit `devproxyrc.json` **in place**, changing `latency` to
   `minMs: 3000 / maxMs: 3500`. Do not touch the proxy.
5. Wait a few seconds and send the same request again.

## Expected

The request is delayed ~3000-3500 ms, per the reloaded configuration.

## Actual

The request fails. `curl` reports `Empty reply from server` and an HTTP status
of `000`. The proxy stays in this state indefinitely.

```
STEP 1 - baseline, config latency 500-600ms (expect ~0.5s):
   HTTP 200 in 0.617422s
   HTTP 200 in 0.525642s

STEP 2 - edit config in place to 3000-3500ms; do NOT restart the proxy

STEP 3 - same requests (expect ~3.2s; ACTUAL: HTTP 000, connection closed):
   HTTP 000 in 0.000705s
   HTTP 000 in 0.000267s

STEP 4 - kill the proxy, start a NEW process with the SAME config:
   (expect ~3.2s - proves the config is valid):
   HTTP 200 in 3.411787s
   HTTP 200 in 3.082397s
```

Dev Proxy's own log during step 2-3 reports a successful restart:

```
 info    Configuration file changed. Restarting proxy...
 info    Dev Proxy listening on 127.0.0.1:8000...
 warn    Configure your operating system to use this proxy's port and address 127.0.0.1:8000
 info    Dev Proxy API listening on http://127.0.0.1:8897...
```

No error or warning is logged. From the log alone the restart looks healthy,
which is what makes this hard to notice — the proxy reports itself up while
silently refusing to serve.

Step 4 is the control: the same configuration file, loaded by a freshly
started process, produces exactly the expected ~3.2 s delay.

## Notes

- **Standalone Dev Proxy — no .NET Aspire, no DCP, no second proxy in the
  path.** The upstream is a plain `python3` HTTP server bound directly to
  `127.0.0.1:5032`; Dev Proxy is launched straight from a shell with
  `--config-file` (its parent process is the container init, not an app host).
  Reproduced a second time on isolated ports (proxy `8020`, upstream `5040`) to
  rule out interference from anything else bound to the usual ports. `curl` is
  given `--noproxy ''` and an explicit `-x`, and the environment's own
  `HTTP_PROXY` is bypassed for loopback via `NO_PROXY`, so nothing else is
  proxying these requests.
- **Step 4 is the control that isolates the fault to the restart**: same
  configuration file, same shell, same environment, same ports — a freshly
  started process serves correctly, while the self-restarted one does not.
- Reproduced repeatedly, on two different ports (8000 and 8010) and with
  several latency bands (500-600, 800-2000, 3000-3500, 4000-4500). The band is
  irrelevant; any change to the file triggers it.
- The edit must be **in place** (truncate + write, same inode). Writing to a
  temporary file and `rename(2)`-ing it over the original does not trigger the
  watcher at all — the proxy keeps serving happily with the old configuration
  and never notices the new one. That may be worth handling too, since
  write-temp-then-rename is the usual way to update a file atomically, and it
  means a config change can be silently ignored rather than applied.
- If a fix is not close, a documented way to ask a running proxy to reload —
  or an endpoint on the API at `127.0.0.1:8897` to update plugin configuration —
  would remove the need for the file watcher in this scenario entirely.

## Why this matters

We drive latency/error/throttling levels from an operator console during live
demonstrations, editing the Dev Proxy config to change conditions while the
system under test keeps running. With the watcher, the proxy dies on the first
change; without it, changes are never picked up. We have worked around it by
restarting the Dev Proxy process after every configuration write, which costs a
short outage on a connection path that is supposed to stay up.
