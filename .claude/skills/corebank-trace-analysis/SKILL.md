---
name: corebank-trace-analysis
description: |
  Produce the trace-level verdict for a CoreBankDemo load test from Tempo, and investigate errors or slow spans in a run.

  **When to use:**
  - As step 6 of the `load-test` skill, after `get_assertion_results` — the k6 state gate is not a complete acceptance verdict without this.
  - When the assertions failed and you need to know which hop, store, or replica misbehaved.
  - When asked to analyze Tempo traces, spans, or distributed tracing data for CoreBank or Payments.

  **When NOT to use:**
  - For business-level assertions (exactly-once, balance conservation) — those come from LoadTestSupport.
  - Before a load test has run — there is nothing in Tempo to read.
  - For general .NET debugging unrelated to a load-test run.
---

## Where the traces are

Both AppHosts run Grafana LGTM (ADR-022). Tempo answers on **`http://localhost:3200`** (host-bound to 127.0.0.1), Grafana on `:3000`. Query Tempo directly with `curl` and TraceQL — the only tools the `load-test` skill allows — or through the bundled script below. There is no separate trace MCP server: a direct TraceQL `select()` returns per-span attributes (replica id, store, partition) that span-summary APIs drop, and `curl` is already in the allowed set.

Service names as registered: `CoreBank.PaymentsAPI`, `CoreBank.CoreBankAPI`, plus the Dapr sidecars `payments-api-dapr-cli` and `corebank-api-dapr-cli` (relay only). Confirm with:

```bash
curl -s http://localhost:3200/api/search/tag/service.name/values
```

If `curl -s -o /dev/null -w "%{http_code}" http://localhost:3200/ready` is not `200`, report **"Trace analysis skipped: Tempo unavailable (LGTM container not running)"** and move on to stopping the AppHost. Do not troubleshoot the backend here.

## Step 1 — The window comes from k6, never from the trace tool

Read k6's console log for `Starting load test` (it is UTC):

```
time="2026-10-11T10:31:17Z" level=info msg="Starting load test: 100 unique transactions, 4 VUs, 10 intentional retries"
```

Use the Aspire MCP `list_console_logs` on resource `k6` with search `Starting load test`. That one line gives you the start time, `--expected` (unique transactions) and `--retries`. Trace tools that print span times without a zone print **local** time; building a window from them puts you hours off and every query answers empty.

## Step 2 — Run the verdict script

```bash
python3 .claude/skills/corebank-trace-analysis/scripts/processing-lanes.py --k6-start 2026-10-11T10:31:17Z --expected 100 --retries 10
```

Stdlib only; `--tempo` overrides the URL; `--start/--end` replace `--k6-start` for a hand-chosen window. It prints one `PASS`/`FAIL` line per check and ends with `VERDICT: PASS` or `VERDICT: FAIL (n check(s))`; exit code 1 on failure, 2 when Tempo is unreachable.

| Check | Pass criterion | What a failure means |
|---|---|---|
| Processing spans in window | > 0 | wrong window, or the run never happened |
| Stage cardinality | `PaymentsOutbox` = N, `CoreBankInbox` = N, `CoreBankOutbox` = 3N, `PaymentsInbox` = 3N | lost or duplicated hop |
| Replica identities | exactly 2 `service.instance.id` per API | a replica never came up or never got work |
| Lane overlap | 0 overlapping consecutive spans in any (store, partition) | two workers in one partition — the locking invariant is broken |
| Lanes with one replica | none | partition lock never rotated |
| Intentional replays | exactly `--retries` PaymentsAPI `postgresql` spans with `error.type 23505` | the dedupe path changed, or real unique-index failures |
| Other error spans | 0 | investigate (Step 4) |
| Error traces reaching CoreBank | 0 | a duplicate was forwarded — exactly-once at risk |
| Slow spans / traces | none > 500 ms, none > 2 s | a stalled hop or lock wait |
| Cross-hop parent links | sampled trace has PaymentsAPI→CoreBank (HTTP) and inbound-to-PaymentsAPI (Dapr) edges | `traceparent` dropped on a hop |

It also prints, as information, each lane's span count per replica and which partitions the command stores used (the load accounts hash to 0, 1, 2 under ADR-026; a command row in partition 3 means routing drift).

## Step 3 — Read the noise correctly

The k6 scenario resends `--retries` payments with the same `Idempotency-Key` on purpose. Each replay is a trace whose `INSERT INTO "OutboxMessages"` fails with `23505`, rolls back to its savepoint, loads the winner and answers `202` in a few ms — the DB span is `ERROR`, the HTTP span is not, and no CoreBank span exists. That is the insert-first dedupe working. Report the count against `--retries`; do not list them as incidents.

## Step 4 — Investigate a real failure

Hand-written TraceQL for the cases the script flags (epoch seconds for `start`/`end`; add `--data-urlencode "limit=1000" --data-urlencode "spss=20"`):

```bash
T=http://localhost:3200/api/search
# error spans with their type and service
curl -s -G $T --data-urlencode 'q={ status = error } | select(resource.service.name, span.error.type, name)' --data-urlencode start=$START --data-urlencode end=$END
# every processing span of one lane, in order
curl -s -G $T --data-urlencode 'q={ span.messaging.store.name = "CoreBankInbox" && span.messaging.partition.id = 1 } | select(resource.service.instance.id)' --data-urlencode start=$START --data-urlencode end=$END
# one whole trace (OTLP JSON: batches[].resource.attributes, scopeSpans[].spans[] with base64 ids)
curl -s http://localhost:3200/api/traces/<traceId>
```

Pipe results through `python3 -c` or `jq`; a full trace is ~65 KB — never print it. Look for: which span failed and whether it is a database, messaging or HTTP span; whether the error crossed a service boundary; the same operation repeated inside one trace (retries); a span whose parent lives in another service but is missing (dropped context).

| Assertion failure | What to look for |
|---|---|
| Duplicate processing | one `transaction.id` in two `ProcessInboxMessage` spans; an error trace that still has a CoreBank span |
| Lost messages | a `POST api/Payments` trace with no CoreBank child; `CoreBankOutbox` count < 3N |
| Balance drift | an `ERROR` span inside `ProcessInboxMessage` on CoreBank |
| Drain timeout | processing spans still appearing after k6 finished; a lane with one replica |

## Reporting

End with the structured summary — interpret, never dump JSON:

```
## Trace Analysis Summary
**Time window:** <k6 start> to <end>   **Services:** CoreBank.PaymentsAPI, CoreBank.CoreBankAPI
**Verdict script:** VERDICT: PASS | FAIL (<which checks>)
### Errors — <count>; intentional replays <n>/<retries>; other: <type — explanation; trace ids>
### Performance — slowest span/trace, bottleneck span or hop
### Lanes — replicas per service, overlaps, partitions used by the command stores
### Correlation with assertions — traces consistent with passing assertions | likely cause of <failure>
### Recommended next step — one concrete action, or "no action needed"
```
