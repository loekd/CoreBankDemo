---
name: corebank-regression-analysis
description: |
  Explain why one CoreBankDemo load-test run is slower than another, using only the data the run left behind.

  **When to use:**
  - When someone says a run "seems slower", "took longer", "regressed", or "used to drain faster" than a baseline run, and wants to know why.
  - When comparing two load-test windows (two `aspire start` runs, before/after a change, two days) on throughput and latency.
  - When a `poll_until_drained` run drained but took noticeably longer than the previous one.

  **When NOT to use:**
  - For a run whose assertions failed — that is a correctness incident; follow `load-test` step 5 and `corebank-trace-analysis` instead.
  - For a single run with no baseline to compare against — there is no "slower" without a "than".
  - To change code or configuration — this skill ends at naming the cause; the fix is a separate change through the normal workflow.
---

## Important!

- You are not allowed to create or run arbitrary bash commands. Restrict yourself to `curl` for HTTP calls and the `aspire` CLI (plus `python3 -c`/`jq` only to parse a `curl` response you just received, as the `corebank-trace-analysis` skill allows). The one script you may run is `corebank-trace-analysis`'s bundled `python3 .claude/skills/corebank-trace-analysis/scripts/processing-lanes.py` (step b). No `grep` over logs or source, no `docker`, no `dotnet`, no `git`, no `psql`, no `/proc`, no scripts of your own.
- Never reset the database, stop an AppHost, or re-run the load test to "reproduce" it. The evidence is already in LoadTestSupport, Tempo and Prometheus; the run under investigation must stay exactly as it is.
- Never read application logs to look for a cause. Logs are where you confirm a cause you have already localised from metrics and traces, never where you go hunting. Ad-hoc log spelunking (Aspire `list_console_logs` / `list_structured_logs` with a guessed search term, a Loki query, scrolling a resource's log) is forbidden until step d, and in step d is limited to the one component the metrics implicated.
- The method is the order below. Do not skip a step, do not reorder, and do not open code (step d) before steps a–c have produced their numbers.

## The method

Performance differences in this system have three honest explanations, and the steps separate them in order: the run was not actually healthy (a), something failed and retried (b), or one stage of the pipeline got slower (c). Only when you know *which stage* do you open the code for it (d).

### a. Confirm correctness first — `get_assertion_results`

A slow run that also lost or duplicated messages is not a performance problem, it is a correctness incident, and its timings are meaningless. Call the LoadTestSupport MCP tool `get_assertion_results` (MCP tool over HTTP at `http://localhost:5181/`, see the `load-test` skill for the session handshake; or the `mcp__corebank-loadtest__get_assertion_results` tool when it is connected) with `expectedUnique` equal to the run's transaction count.

```bash
curl -s -X POST http://localhost:5181/ \
  -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  -H "Mcp-Session-Id: $SESSION_ID" \
  -d '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"get_assertion_results","arguments":{"expectedUnique":100}}}'
```

Report `allPassed`. If it is `false`, stop here: say which check failed and hand over to `load-test` step 5 / `corebank-trace-analysis`. Do not continue into performance analysis on a run that is not correct.

LoadTestSupport only knows the current database, so this step covers the *current* run. The baseline run's assertions were recorded when it ran; say so rather than claiming you re-verified it.

### b. Hunt errors in the traces — `corebank-trace-analysis`

Invoke the **corebank-trace-analysis** skill for the current run's window (its step 1 explains how to get the window from k6's `Starting load test` line; its step 2 script prints the error-span and slow-span checks). You are looking for: error spans other than the intentional `23505` replays, retries inside a trace, a lane with one replica, and slow spans.

**Report the result explicitly either way.** "No errors: n/n intentional replays, 0 other error spans, no spans over 500 ms" is a finding, not a non-result — it rules out "something failed and retried" and sends you to step c. If there *are* errors, they are the prime suspect for the slowdown; carry them into step c rather than jumping to code.

### c. Compare the metrics of the two windows — Grafana datasource proxy

Grafana runs anonymous (Admin) on `http://localhost:3000` and proxies the LGTM Prometheus at `/api/datasources/proxy/uid/prometheus`. Everything below is plain `curl` against that proxy; no API key, no Grafana MCP.

#### c.1 Decide the two windows

If the user gave both windows (UTC), use them. If not, locate the runs from the payment-intake counter: each run is one burst of `corebankdemo_payment_intake_total` increments, and a 100-transaction run is over in well under a minute, so look at 15-second resolution:

```bash
G=http://localhost:3000/api/datasources/proxy/uid/prometheus/api/v1
curl -s -G "$G/query_range" \
  --data-urlencode 'query=sum(increase(corebankdemo_payment_intake_total[30s]))' \
  --data-urlencode "start=$(date -u -d '-3 hours' +%s)" --data-urlencode "end=$(date -u +%s)" \
  --data-urlencode step=15 \
| python3 -c 'import json,sys,datetime
for r in json.load(sys.stdin)["data"]["result"]:
    for t,v in r["values"]:
        if float(v)>0: print(datetime.datetime.fromtimestamp(t,datetime.UTC).strftime("%Y-%m-%dT%H:%M:%SZ"), round(float(v)))'
```

Each run is a group of adjacent non-zero samples. The **current** run is the latest group; the **baseline** is the group the user pointed at (or, for "the previous run", the one before it). Its window runs from the whole minute containing the first non-zero sample to one minute after it (a run that is still draining at that point is already a finding — widen the window until `corebankdemo_transaction_processed_total` has stopped increasing and note the length). Cross-check the start against k6's `Starting load test` console line when the AppHost of that run is still up. Write both windows as epoch seconds:

```bash
B_START=$(date -u -d '2026-10-12T16:08:00Z' +%s); B_END=$(date -u -d '2026-10-12T16:09:00Z' +%s)
C_START=$(date -u -d '2026-10-12T16:10:00Z' +%s); C_END=$(date -u -d '2026-10-12T16:11:00Z' +%s)
```

Use equal window lengths so the per-second numbers compare; if the runs genuinely differ in length, that *is* the first finding (the same transaction count over a longer window is the slowdown), so report both lengths.

#### c.2 The three comparisons

Per-window totals are exact with the "last sample in the window minus last sample before it" form the dashboards use (`increase()` drops a series' first sample and under-counts a counter that starts mid-window). Define it once, then run each query at `time=<window end>`:

```bash
# PromQL for the exact per-series total inside a window of $W seconds, evaluated at the window's end.
delta() { echo "(last_over_time($1[${W}s]) - last_over_time($1[7d] offset ${W}s)) or last_over_time($1[${W}s])"; }

# Run all three for one window; call once per window.
compare() {
  local START=$1 END=$2; W=$((END-START))
  echo "window $(date -u -d @$START +%H:%M:%SZ)-$(date -u -d @$END +%H:%M:%SZ) (${W}s)"
  curl -s -G "$G/query" --data-urlencode "time=$END" \
    --data-urlencode "query=sum($(delta 'corebankdemo_transaction_processed_total'))" ; echo "  <- transactions processed (count); divide by ${W} for processed/s"
  curl -s -G "$G/query" --data-urlencode "time=$END" \
    --data-urlencode "query=histogram_quantile(0.95, sum by (le) ($(delta 'corebankdemo_messaging_queue_duration_milliseconds_bucket{messaging_store_name="corebank-inbox"}')))" ; echo "  <- corebank-inbox queue duration p95 (ms)"
  curl -s -G "$G/query" --data-urlencode "time=$END" \
    --data-urlencode "query=histogram_quantile(0.95, sum by (le) ($(delta 'corebankdemo_transaction_execution_duration_milliseconds_bucket')))" ; echo "  <- transaction execution duration p95 (ms)"
}
compare $B_START $B_END
compare $C_START $C_END
```

The value is in `data.result[0].value[1]`; an empty `result` means that metric has no samples in that window (say so — the execution-duration histogram only exists for runs made after it was added).

To see *where inside the run* a p95 moved, the same three series are the dashboard's "CoreBank throughput and latency" row (`http://localhost:3000/d/corebank`); per-step numbers come from `query_range` with `step=15` and `rate(...[1m])` in place of `delta`.

#### c.3 Read the three together

| processed/s | inbox queue p95 | execution p95 | Reading |
|---|---|---|---|
| down | up | **up** | Execution itself got slower: lock waits (row or partition locks) or a slower ledger write. Step d: `TransactionExecutor`, `IAccountRepository.LockForUpdateAsync`, and whatever the inbox handler holds while it calls them. |
| down | up | flat | Rows wait longer but execute as fast as before: fewer workers reach them. Step d: the CoreBank inbox processor — partition claim and lock rotation, replica count, batch size, poll interval. |
| down | flat | flat | CoreBank is idle; the slowdown is upstream (Payments outbox delivery, HTTP hop, k6 itself). Step d: `PaymentsOutboxProcessor`, the HttpClient p95 panel, Payments outbox queue p95. |
| flat | flat or down | flat or down | Not a regression in these numbers. Say so plainly, show both columns, and ask what the user measured as "slower" (wall clock of `poll_until_drained`? k6's own summary?) before going anywhere near code. |

### d. Only now: open the implicated code path and name the cause

With the stage from c.3 (and any error from b), read the code for that stage only, and name the cause as a sentence that ties the code to the numbers: *"execution p95 rose from 12 ms to 180 ms with queue p95 following; `TransactionExecutor.LoadAccountsAsync` takes two `FOR UPDATE` row locks in account order, and the current run's accounts collide on …"*. If logs can confirm it, read the implicated component's logs for that window — once, targeted, and say what you looked for.

Do not propose or make a fix in this skill. Hand the named cause (code path, metric evidence, trace ids if any) to the normal change workflow.

## Reporting

```
## Regression Analysis
**Baseline:** <start>–<end> UTC   **Current:** <start>–<end> UTC
a. Correctness: allPassed=<true|false> (expectedUnique=<n>)
b. Traces: <VERDICT line>; <n>/<retries> intentional replays, <m> other error spans, slowest span <x> ms
c. Metrics:            baseline    current
   processed (count/s) <n> / <r>    <n> / <r>
   inbox queue p95     <ms>         <ms>
   execution p95       <ms>         <ms>
d. Cause: <one sentence naming the code path and tying it to the numbers>
```

## Excuses that do not hold

| Excuse | Reality |
|---|---|
| "I'll write a quick Python script against Tempo to compare every span name" | Step b is the `corebank-trace-analysis` script and its TraceQL recipes. A hand-rolled comparison of 800 spans is log spelunking with extra steps and takes longer than the whole method. |
| "A `grep` over the source is not really code reading" | It is step d. Until c.2 has numbers for both windows you do not know which file to grep. |
| "`docker ps` / `/proc/<pid>/environ` / `git show` just confirm the setup" | Not allowed, and not needed: the setup is the two windows and three numbers. If a number looks wrong, re-check the window, not the container list. |
| "The k6 summary already says it was faster, so I can skip the metrics" | k6 measures the HTTP edge. The user said "slower"; c.2 is what shows *which stage*, and the report needs both columns regardless. |
| "There is a FAIL in the trace verdict, so the cause is there" | One slow span is a lead to carry into step c, not a conclusion. It becomes the cause only when the c.3 row agrees with it. |
| "Both runs look fine; I'll poke around in case something is off" | "Not a regression" is the answer. Report both columns and ask what was measured. |

## Red flags — stop, you are off the method

- "Let me just check the logs quickly" before step d.
- Reading `TransactionExecutor` or any processor before c.2 has produced numbers for both windows.
- Running the load test again to "see if it reproduces".
- A `grep`, `docker`, `dotnet`, or `psql` command.
- Reporting a cause with no number next to it.
