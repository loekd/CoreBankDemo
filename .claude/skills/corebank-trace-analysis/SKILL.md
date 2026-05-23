---
name: corebank-trace-analysis
description: |
  Analyze OpenTelemetry traces after a CoreBankDemo load test to identify performance issues and errors.

  **When to use:**
  - After a load test completes (or fails), to investigate what happened at the infrastructure level
  - When `get_assertion_results` returns failures and you need to understand why
  - When you want to find slow spans, high-latency payment flows, or exceptions during a test run
  - When asked to analyze Jaeger traces, spans, or distributed tracing data for CoreBank or Payments

  **When NOT to use:**
  - For business-level assertions (exactly-once, balance conservation) — use LoadTestSupport MCP tools instead
  - Before the load test has run — there will be no relevant traces
  - For general .NET debugging unrelated to load testing
---

## Services in CoreBankDemo

Always use these exact service names when filtering:

| Service | Jaeger name | Role |
|---|---|---|
| Payments API | `payments-api` | Receives payment requests from k6 |
| CoreBank API | `corebank-api` | Processes transactions, updates balances |
| LoadTestSupport | `loadtest-support` | Orchestration only, ignore in trace analysis |

If unsure of exact service names, call `list_services` first.

## Step 1 — Establish the time window

Before searching, determine the test time window. Use the k6 run timestamps if available.
If not, use the last 30 minutes as default:

```
start_time: <test start or 30 minutes ago in ISO 8601>
end_time:   <test end or now in ISO 8601>
```

Always pass explicit time windows to every tool call. Never rely on defaults.

## Step 2 — Find exceptions

Call `find_errors` for each service separately. Do not combine services in one call.

```json
{ "service_name": "payments-api", "start_time": "...", "end_time": "...", "limit": 50 }
{ "service_name": "corebank-api", "start_time": "...", "end_time": "...", "limit": 50 }
```

For each error found, report:
- `error_type` and `error_message`
- `trace_id` (save for Step 4)
- Timestamp and service

If zero errors: report explicitly — do not skip this step silently.

## Step 3 — Find slow spans

Call `search_traces` with duration filters. Use these thresholds for CoreBankDemo:

| Operation | Slow threshold |
|---|---|
| Payment request (end-to-end) | > 2000ms |
| CoreBank transaction processing | > 1000ms |
| Any single span | > 500ms |

```json
{
  "service_name": "payments-api",
  "min_duration_ms": 2000,
  "start_time": "...",
  "end_time": "...",
  "limit": 20
}
```

Repeat for `corebank-api`. Report the slowest 5 traces per service with their duration.

## Step 4 — Deep dive on suspect traces

For any trace that had an error (Step 2) or was in the top slowest (Step 3), call `get_trace`:

```json
{ "trace_id": "<id from previous steps>" }
```

In the response, look for:
- Which span failed or was slowest
- Whether the slow span was a database call, messaging, or HTTP
- Whether errors propagated across service boundaries (cross-service spans)
- Retry patterns — the same operation appearing multiple times in one trace

## Step 5 — Correlate with LoadTestSupport assertions

If `get_assertion_results` showed failures, cross-reference:

| Assertion failure | What to look for in traces |
|---|---|
| Duplicate messages | Same payment appearing in multiple traces, retry storms |
| Lost messages | Traces that started in payments-api but have no corresponding corebank-api span |
| Balance drift | Traces with errors mid-transaction, rolled-back spans |
| Drain timeout | High volume of pending spans near test end |

## Reporting

Always end with a structured summary:

```
## Trace Analysis Summary

**Time window:** <start> to <end>
**Services analyzed:** payments-api, corebank-api

### Errors
- <count> errors found
- Most common: <error_type> — <brief explanation>
- Trace IDs with errors: <list>

### Performance
- Slowest trace: <trace_id> — <duration>ms (<service>)
- P99 estimate: <if enough data>
- Bottleneck: <span name or service>

### Correlation with assertions
- <if assertion failures> Likely cause: <explanation based on trace evidence>
- <if no assertion failures> Traces consistent with passing assertions

### Recommended next step
<One concrete action: fix X, investigate Y, or no action needed>
```

Do not dump raw JSON. Interpret the data.
