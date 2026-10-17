#!/usr/bin/env python3
"""Measure a feature branch's impact against origin/main.

Runs, in this order, and stops at the first failure:
  1. preflight        clean checkout on a non-main branch, no AppHost running, origin/main fetched
  2. unit tier        dotnet test CoreBankDemo.UnitTests.slnf on the feature checkout (build skill)
  3. baseline run     LoadTests acceptance run from a temporary worktree of origin/main
  4. feature run      the same run from the feature checkout
  5. report           correctness and throughput of both runs, side by side

Steps 3 and 4 are the aspire-launch and load-test skills' procedures as code: aspire ps,
aspire start, aspire wait, poll_until_drained and get_assertion_results over the
LoadTestSupport MCP endpoint, aspire stop. Throughput comes from Prometheus through
Grafana, scoped to the run by the run.id stamp the LoadTests AppHost sets (lock-impact
dashboard). The script never writes inside the repository: the baseline worktree lives
under the system temp directory and is removed afterwards.

Exit code 0: report written and printed. Exit code 1: aborted; the report names the step.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

GRAFANA = "http://localhost:3000"
PROMETHEUS = f"{GRAFANA}/api/datasources/proxy/uid/prometheus/api/v1"
LOADTEST_SUPPORT = "http://localhost:5181/"
LOADTESTS_PROJECT = os.path.join("CoreBankDemo.LoadTests", "CoreBankDemo.LoadTests.csproj")
UNIT_TIER = "CoreBankDemo.UnitTests.slnf"
DEAD_OTLP = {"OTLP_ENDPOINT": "http://127.0.0.1:1", "OTEL_EXPORTER_OTLP_ENDPOINT": "http://127.0.0.1:1"}

# Generous, bounded. Nothing in this script waits without a deadline.
TIMEOUTS = {
    "git": 120,
    "unit": 15 * 60,
    "start": 20 * 60,
    "wait": 10 * 60,
    "run_id": 3 * 60,
    "drain": 6 * 60,
    "assert": 2 * 60,
    "stop": 5 * 60,
    "http": 30,
}

THROUGHPUT_RATE = (
    'sum(rate(corebankdemo_transaction_processed_total{service_name="CoreBank.CoreBankAPI"}[15s])'
    ' * on(job, instance) group_left(run_id) target_info{run_id="%s"})'
)
PROCESSED_COUNTER = (
    'sum(corebankdemo_transaction_processed_total{service_name="CoreBank.CoreBankAPI"}'
    ' * on(job, instance) group_left(run_id) target_info{run_id="%s"})'
)


class StepFailed(Exception):
    def __init__(self, step: str, detail: str):
        super().__init__(f"{step}: {detail}")
        self.step = step
        self.detail = detail


class Log:
    def __init__(self, path: str):
        self.path = path
        self.started = time.monotonic()

    def __call__(self, step: str, message: str) -> None:
        elapsed = int(time.monotonic() - self.started)
        line = f"[{dt.datetime.now():%H:%M:%S} +{elapsed // 60:02d}:{elapsed % 60:02d}] {step:<10} {message}"
        print(line, flush=True)
        with open(self.path, "a", encoding="utf-8") as handle:
            handle.write(line + "\n")


def tail(text: str, lines: int = 12) -> str:
    rows = [row for row in text.strip().splitlines() if row.strip()]
    return "\n".join(rows[-lines:])


def run(step: str, cmd: list[str], cwd: str, timeout: int, env: dict | None = None, check: bool = True) -> subprocess.CompletedProcess:
    full_env = dict(os.environ)
    if env:
        full_env.update(env)
    try:
        result = subprocess.run(cmd, cwd=cwd, env=full_env, capture_output=True, text=True, timeout=timeout)
    except subprocess.TimeoutExpired as error:
        raise StepFailed(step, f"`{' '.join(cmd)}` did not finish within {timeout} s") from error
    except FileNotFoundError as error:
        raise StepFailed(step, f"`{cmd[0]}` is not on PATH") from error
    if check and result.returncode != 0:
        raise StepFailed(step, f"`{' '.join(cmd)}` exited {result.returncode}\n{tail(result.stdout + result.stderr)}")
    return result


def http_json(step: str, url: str, data: bytes | None = None, headers: dict | None = None, timeout: int = TIMEOUTS["http"]) -> tuple[dict, str]:
    request = urllib.request.Request(url, data=data, headers=headers or {}, method="POST" if data else "GET")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            body = response.read().decode("utf-8")
            return dict(response.headers), body
    except urllib.error.HTTPError as error:
        raise StepFailed(step, f"{url} returned HTTP {error.code}: {tail(error.read().decode('utf-8', 'replace'), 5)}") from error
    except (urllib.error.URLError, TimeoutError, OSError) as error:
        raise StepFailed(step, f"{url} unreachable: {error}") from error


# ---------------------------------------------------------------- preflight

def preflight(repo: str, log: Log) -> dict:
    step = "preflight"
    for tool in ("git", "dotnet", "aspire"):
        if shutil.which(tool) is None:
            raise StepFailed(step, f"`{tool}` is not on PATH")
    status = run(step, ["git", "status", "--porcelain"], repo, TIMEOUTS["git"]).stdout.strip()
    if status:
        raise StepFailed(step, "the feature checkout has uncommitted changes; commit or stash them yourself first:\n" + tail(status, 8))
    branch = run(step, ["git", "branch", "--show-current"], repo, TIMEOUTS["git"]).stdout.strip()
    if not branch:
        raise StepFailed(step, "the feature checkout is on a detached HEAD; check out the feature branch")
    if branch == "main":
        raise StepFailed(step, "the feature checkout is on main; check out the feature branch to measure")
    feature_sha = run(step, ["git", "rev-parse", "--short", "HEAD"], repo, TIMEOUTS["git"]).stdout.strip()
    run(step, ["git", "fetch", "origin", "main", "--quiet"], repo, TIMEOUTS["git"])
    main_sha = run(step, ["git", "rev-parse", "--short", "origin/main"], repo, TIMEOUTS["git"]).stdout.strip()
    running = running_apphosts(step, repo)
    if running:
        raise StepFailed(step, "an AppHost is already running and this script never stops what it did not start:\n" + running)
    log(step, f"feature {branch} @{feature_sha}, baseline origin/main @{main_sha}, no AppHost running")
    return {"branch": branch, "feature_sha": feature_sha, "main_sha": main_sha}


def running_apphosts(step: str, cwd: str) -> str:
    result = run(step, ["aspire", "ps", "--format", "Json"], cwd, TIMEOUTS["git"], check=False)
    text = (result.stdout or "").strip()
    try:
        entries = json.loads(text) if text.startswith("[") else []
    except json.JSONDecodeError:
        entries = []
    if entries:
        return "\n".join(f"  {entry.get('appHostPath') or entry.get('AppHostPath') or json.dumps(entry)[:120]}" for entry in entries)
    if result.returncode != 0 and "No running AppHost" not in (result.stdout + result.stderr):
        raise StepFailed(step, f"`aspire ps` failed: {tail(result.stdout + result.stderr, 5)}")
    return ""


# ---------------------------------------------------------------- unit tier

def unit_tier(repo: str, log: Log) -> dict:
    step = "unit-tier"
    log(step, f"dotnet tool restore + dotnet test {UNIT_TIER} on the feature checkout (OTLP export disabled)")
    run(step, ["dotnet", "tool", "restore"], repo, TIMEOUTS["git"])
    started = time.monotonic()
    result = run(step, ["dotnet", "test", UNIT_TIER], repo, TIMEOUTS["unit"], env=DEAD_OTLP, check=False)
    seconds = int(time.monotonic() - started)
    output = result.stdout + result.stderr
    passed = sum(int(m) for m in re.findall(r"Passed:\s+(\d+)", output))
    failed = sum(int(m) for m in re.findall(r"Failed:\s+(\d+)", output))
    assemblies = len(re.findall(r"^(?:Passed|Failed)!", output, flags=re.MULTILINE))
    if result.returncode != 0:
        raise StepFailed(step, f"unit tier failed on the feature checkout ({failed} failed, {passed} passed, {assemblies} assemblies, {seconds} s)\n{tail(output, 15)}")
    log(step, f"PASS: {passed} tests in {assemblies} assemblies, {seconds} s")
    return {"passed": passed, "failed": failed, "assemblies": assemblies, "seconds": seconds}


# ---------------------------------------------------------------- load runs

def prometheus_run_ids(step: str) -> set[str]:
    now = int(time.time())
    query = urllib.parse.urlencode({"match[]": 'target_info{run_id!=""}', "start": now - 15 * 60, "end": now})
    _, body = http_json(step, f"{PROMETHEUS}/label/run_id/values?{query}")
    return set(json.loads(body).get("data", []))


def mcp_call(step: str, session: str | None, payload: dict, timeout: int) -> tuple[str | None, dict]:
    headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    if session:
        headers["Mcp-Session-Id"] = session
    response_headers, body = http_json(step, LOADTEST_SUPPORT, json.dumps(payload).encode("utf-8"), headers, timeout)
    session_id = next((value for key, value in response_headers.items() if key.lower() == "mcp-session-id"), session)
    result: dict = {}
    for line in body.splitlines():
        if line.startswith("data:"):
            try:
                message = json.loads(line[5:].strip())
            except json.JSONDecodeError:
                continue
            if "result" in message or "error" in message:
                result = message
    return session_id, result


def tool_text(step: str, message: dict) -> dict:
    if "error" in message:
        raise StepFailed(step, f"LoadTestSupport returned an error: {json.dumps(message['error'])[:300]}")
    content = message.get("result", {}).get("content", [])
    text = next((item.get("text") for item in content if item.get("type") == "text"), None)
    if text is None:
        raise StepFailed(step, f"LoadTestSupport returned no text content: {json.dumps(message)[:300]}")
    try:
        return json.loads(text)
    except json.JSONDecodeError as error:
        raise StepFailed(step, f"LoadTestSupport text is not JSON: {text[:300]}") from error


def count_checks(node, passed: list[int]) -> None:
    if isinstance(node, dict):
        if "passed" in node and isinstance(node["passed"], bool):
            passed[0 if node["passed"] else 1] += 1
        for value in node.values():
            count_checks(value, passed)
    elif isinstance(node, list):
        for value in node:
            count_checks(value, passed)


def load_run(label: str, path: str, transactions: int, vus: int, log: Log) -> dict:
    project = os.path.join(path, LOADTESTS_PROJECT)
    step = f"{label}-start"
    before = prometheus_run_ids(step)
    log(step, f"aspire start {LOADTESTS_PROJECT} ({transactions} transactions, {vus} VUs) from {path}")
    run(step, ["aspire", "start", "--apphost", project, "--non-interactive", "--",
               f"--LoadTest:TransactionCount={transactions}", f"--LoadTest:VuCount={vus}"], path, TIMEOUTS["start"])
    started_at = time.time()
    result: dict = {"label": label, "path": path}
    try:
        step = f"{label}-wait"
        wait_cmd = ["aspire", "wait", "loadtest-support", "--apphost", project, "--non-interactive"]
        first = run(step, wait_cmd, path, TIMEOUTS["wait"], check=False)
        if first.returncode != 0 and "No AppHost" in (first.stdout + first.stderr):
            time.sleep(5)  # the load-test skill's one permitted retry: the AppHost may still be registering
            first = run(step, wait_cmd, path, TIMEOUTS["wait"], check=False)
        if first.returncode != 0:
            raise StepFailed(step, f"loadtest-support did not become healthy\n{tail(first.stdout + first.stderr, 6)}")
        log(step, "loadtest-support healthy; initializer resets, then k6 runs on its own")

        step = f"{label}-run-id"
        deadline = time.monotonic() + TIMEOUTS["run_id"]
        new_ids: set[str] = set()
        while time.monotonic() < deadline and not new_ids:
            new_ids = prometheus_run_ids(step) - before
            if not new_ids:
                time.sleep(5)
        if not new_ids:
            raise StepFailed(step, "no new run_id appeared on Prometheus target_info; the LoadTests AppHost must stamp run.id (lock-impact dashboard)")
        result["run_id"] = max(new_ids)
        log(step, f"run id {result['run_id']}")

        step = f"{label}-drain"
        session, _ = mcp_call(step, None, {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
            "protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "feature-impact", "version": "1.0"}}}, TIMEOUTS["http"])
        # LoadTestSupport serves MCP statelessly: no Mcp-Session-Id comes back, and none is needed.
        # If a future build issues one, mcp_call carries it on every later request.
        initialized_headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
        if session:
            initialized_headers["Mcp-Session-Id"] = session
        http_json(step, LOADTEST_SUPPORT, json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}).encode("utf-8"),
                  initialized_headers)
        _, message = mcp_call(step, session, {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {
            "name": "poll_until_drained",
            "arguments": {"timeoutSeconds": TIMEOUTS["drain"], "minimumExpectedCompleted": transactions},
            "_meta": {"progressToken": f"{label}-drain"}}}, TIMEOUTS["drain"] + 60)
        drain = tool_text(step, message)
        result["drained_at"] = time.time()
        result["drain"] = drain
        if not drain.get("isDrained"):
            raise StepFailed(step, f"not drained within {TIMEOUTS['drain']} s: {json.dumps(drain)}")
        log(step, f"drained: completed {drain.get('completed')}, failed {drain.get('failed')}, cancelled {drain.get('cancelled')}, {drain.get('pollCount')} polls")

        step = f"{label}-assert"
        _, message = mcp_call(step, session, {"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {
            "name": "get_assertion_results", "arguments": {"expectedUnique": transactions}}}, TIMEOUTS["assert"])
        assertions = tool_text(step, message)
        counts = [0, 0]
        count_checks(assertions, counts)
        result["assertions"] = {"allPassed": bool(assertions.get("allPassed")), "passed": counts[0], "failed": counts[1], "raw": assertions}
        log(step, f"assertions allPassed={result['assertions']['allPassed']} ({counts[0]} passed, {counts[1]} failed)")
    finally:
        step = f"{label}-stop"
        log(step, "aspire stop")
        stop = run(step, ["aspire", "stop", "--apphost", project, "--non-interactive"], path, TIMEOUTS["stop"], check=False)
        if stop.returncode != 0:
            log(step, "WARNING aspire stop exited non-zero: " + tail(stop.stdout + stop.stderr, 3))
        result["stopped_at"] = time.time()

    step = f"{label}-metrics"
    result["throughput"] = throughput(step, result["run_id"], started_at - 60, result["stopped_at"] + 60)
    tp = result["throughput"]
    log(step, f"processed {tp['processed']} in {tp['window_seconds']:.1f} s: avg {tp['avg_tps']:.1f} tx/s, peak {tp['peak_tps']:.1f} tx/s")
    return result


def query_range(step: str, expr: str, start: float, end: float, step_seconds: int) -> list[tuple[float, float]]:
    # Prometheus refuses more than 11,000 points per series; a run window is a few minutes,
    # so this only matters when a caller passes a wide window.
    step_seconds = max(step_seconds, int((end - start) // 10000) + 1)
    query = urllib.parse.urlencode({"query": expr, "start": int(start), "end": int(end), "step": step_seconds})
    _, body = http_json(step, f"{PROMETHEUS}/query_range?{query}")
    data = json.loads(body)
    if data.get("status") != "success":
        raise StepFailed(step, f"Prometheus: {json.dumps(data)[:200]}")
    series = data["data"]["result"]
    if not series:
        return []
    return [(float(t), float(v)) for t, v in series[0]["values"]]


def throughput(step: str, run_id: str, start: float, end: float) -> dict:
    counter = query_range(step, PROCESSED_COUNTER % run_id, start, end, 1)
    counter = [(t, v) for t, v in counter if v == v]  # drop NaN
    if not counter:
        raise StepFailed(step, f"no corebankdemo_transaction_processed_total series joined to run_id {run_id}")
    final = max(v for _, v in counter)
    first_t = counter[0][0]
    last_t = next(t for t, v in counter if v >= final)
    window = max(last_t - first_t, 1.0)
    rate = query_range(step, THROUGHPUT_RATE % run_id, start, end, 5)
    peak = max((v for _, v in rate if v == v), default=0.0)
    return {"processed": int(final), "window_seconds": window, "avg_tps": final / window, "peak_tps": peak,
            "first_processed_at": first_t, "last_processed_at": last_t}


# ---------------------------------------------------------------- report

def pct(feature: float, baseline: float) -> str:
    if baseline <= 0:
        return "n/a"
    delta = (feature - baseline) / baseline * 100
    return f"{delta:+.0f} %"


def dashboard_link(run_id: str) -> str:
    return f"{GRAFANA}/d/lock-impact?var-run={urllib.parse.quote(run_id)}"


def render_report(meta: dict, unit: dict | None, base: dict | None, feat: dict | None, failure: StepFailed | None, log_path: str) -> str:
    lines = [f"# Feature impact: {meta.get('branch', '?')} vs main", ""]
    lines.append(f"Started {meta['started']:%Y-%m-%d %H:%M} local, {meta['transactions']} transactions / {meta['vus']} VUs, "
                 f"{int(meta['elapsed']) // 60} min {int(meta['elapsed']) % 60} s. "
                 f"Baseline origin/main @{meta.get('main_sha', '?')}, feature @{meta.get('feature_sha', '?')}.")
    lines.append("")
    if failure:
        lines += [f"## ABORTED at step `{failure.step}`", "", "```", failure.detail, "```", "",
                  "Nothing was diagnosed or changed. Fix the cause yourself, then run the skill again.", ""]
    if unit:
        lines.append(f"**Unit tier (feature checkout):** PASS, {unit['passed']} tests in {unit['assemblies']} assemblies, {unit['seconds']} s")
    elif failure and failure.step == "unit-tier":
        lines.append("**Unit tier (feature checkout):** FAIL (see above)")
    lines.append("")
    if base or feat:
        def cell(result: dict | None, getter):
            try:
                return getter(result) if result else "not run"
            except (KeyError, TypeError):
                return "n/a"
        rows = [
            ("Run id (dashboard dropdown)", lambda r: r["run_id"]),
            ("Assertions", lambda r: f"{'allPassed' if r['assertions']['allPassed'] else 'FAILED'} ({r['assertions']['passed']} passed, {r['assertions']['failed']} failed)"),
            ("Completed / failed / cancelled", lambda r: f"{r['drain']['completed']} / {r['drain']['failed']} / {r['drain']['cancelled']}"),
            ("Drain polls (2 s each)", lambda r: str(r["drain"]["pollCount"])),
            ("Processing window", lambda r: f"{r['throughput']['window_seconds']:.1f} s"),
            ("Average throughput", lambda r: f"{r['throughput']['avg_tps']:.1f} tx/s"),
            ("Peak throughput (15 s window)", lambda r: f"{r['throughput']['peak_tps']:.1f} tx/s"),
            ("Dashboard", lambda r: dashboard_link(r["run_id"])),
        ]
        lines += [f"| | main @{meta.get('main_sha', '?')} | {meta.get('branch', 'feature')} @{meta.get('feature_sha', '?')} |", "|---|---|---|"]
        for title, getter in rows:
            lines.append(f"| {title} | {cell(base, getter)} | {cell(feat, getter)} |")
        lines.append("")
    if base and feat and not failure:
        b, f = base["throughput"], feat["throughput"]
        same = base["assertions"]["allPassed"] and feat["assertions"]["allPassed"]
        correctness = "correctness unchanged, both runs allPassed" if same else (
            "CORRECTNESS CHANGED: " + ("feature run failed assertions" if base["assertions"]["allPassed"] else "baseline run failed assertions"))
        lines.append(f"**Verdict:** {correctness}; average throughput {pct(f['avg_tps'], b['avg_tps'])}, peak {pct(f['peak_tps'], b['peak_tps'])}, "
                     f"processing window {pct(f['window_seconds'], b['window_seconds'])}.")
        lines.append("")
    lines.append(f"Log: {log_path}")
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------- main

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--transactions", type=int, default=1000)
    parser.add_argument("--vus", type=int, default=10)
    parser.add_argument("--repo", default=os.getcwd(), help="the feature checkout (default: current directory)")
    parser.add_argument("--out", default=None, help="output directory (default: <temp>/corebank-feature-impact/<timestamp>)")
    args = parser.parse_args()

    repo = os.path.abspath(args.repo)
    started = dt.datetime.now()
    out_dir = args.out or os.path.join(tempfile.gettempdir(), "corebank-feature-impact", f"{started:%Y%m%d-%H%M%S}")
    os.makedirs(out_dir, exist_ok=True)
    log = Log(os.path.join(out_dir, "run.log"))
    report_path = os.path.join(out_dir, "report.md")
    meta = {"started": started, "transactions": args.transactions, "vus": args.vus}
    unit = base = feat = None
    failure: StepFailed | None = None
    worktree = os.path.join(out_dir, "main")
    t0 = time.monotonic()
    try:
        meta.update(preflight(repo, log))
        unit = unit_tier(repo, log)
        step = "baseline-worktree"
        log(step, f"git worktree add --detach {worktree} origin/main")
        run(step, ["git", "worktree", "prune"], repo, TIMEOUTS["git"])
        run(step, ["git", "worktree", "add", "--detach", worktree, "origin/main"], repo, TIMEOUTS["git"])
        run(step, ["dotnet", "tool", "restore"], worktree, TIMEOUTS["git"])
        base = load_run("baseline", worktree, args.transactions, args.vus, log)
        feat = load_run("feature", repo, args.transactions, args.vus, log)
    except StepFailed as error:
        failure = error
        log(error.step, "FAILED: " + error.detail.splitlines()[0])
    except KeyboardInterrupt:
        failure = StepFailed("interrupted", "the run was interrupted")
    finally:
        if os.path.isdir(worktree):
            removal = run("cleanup", ["git", "worktree", "remove", "--force", worktree], repo, TIMEOUTS["git"], check=False)
            if removal.returncode != 0:
                shutil.rmtree(worktree, ignore_errors=True)
                run("cleanup", ["git", "worktree", "prune"], repo, TIMEOUTS["git"], check=False)
            log("cleanup", "baseline worktree removed")
    meta["elapsed"] = time.monotonic() - t0
    report = render_report(meta, unit, base, feat, failure, log.path)
    with open(report_path, "w", encoding="utf-8") as handle:
        handle.write(report)
    with open(os.path.join(out_dir, "results.json"), "w", encoding="utf-8") as handle:
        json.dump({"meta": {k: (v.isoformat() if isinstance(v, dt.datetime) else v) for k, v in meta.items()},
                   "unit": unit, "baseline": base, "feature": feat,
                   "failure": {"step": failure.step, "detail": failure.detail} if failure else None}, handle, indent=2, default=str)
    print("\n" + "=" * 72 + f"\nREPORT ({report_path})\n" + "=" * 72 + "\n" + report, flush=True)
    return 1 if failure else 0


if __name__ == "__main__":
    sys.exit(main())
