#!/usr/bin/env python3
"""Step-6 trace verdict for a CoreBankDemo load test, straight from Tempo.

Queries Tempo's TraceQL search API for the run's processing spans and prints
PASS/FAIL lines for the acceptance claims the k6 state gate cannot see:

  * both API replicas took part, on every (store, partition) lane
  * no two processing spans of one (store, partition) lane overlap in time
  * the stage cardinality N / N / 3N / 3N (payments outbox, corebank inbox,
    corebank outbox, payments inbox)
  * the only error spans are the k6 run's intentional duplicate replays
    (one PostgreSQL 23505 per retry, each in a trace that never reached
    CoreBank)
  * no slow spans, and trace context intact across both hops on a sampled
    end-to-end trace

Stdlib only. Exit code 1 when any check fails, 2 when Tempo is unreachable.

Usage:
  python3 processing-lanes.py --k6-start 2026-10-11T10:31:17Z
  python3 processing-lanes.py --start ... --end ... --expected 1000 --retries 10
"""
from __future__ import annotations

import argparse
import base64
import collections
import datetime as dt
import json
import sys
import urllib.error
import urllib.parse
import urllib.request

PROCESSING = '{ name =~ "Process(Inbox|Outbox)Message" }'
SELECT = (
    " | select(resource.service.instance.id, resource.service.name,"
    " span.messaging.store.name, span.messaging.partition.id)"
)
STORES = ("PaymentsOutbox", "CoreBankInbox", "CoreBankOutbox", "PaymentsInbox")
PAYMENTS = "CoreBank.PaymentsAPI"
COREBANK = "CoreBank.CoreBankAPI"


def iso_to_epoch(value: str) -> int:
    value = value.replace("Z", "+00:00")
    parsed = dt.datetime.fromisoformat(value)
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=dt.timezone.utc)
    return int(parsed.timestamp())


def get_json(url: str, timeout: int = 90):
    with urllib.request.urlopen(url, timeout=timeout) as response:
        return json.load(response)


def search(tempo: str, query: str, start: int, end: int, limit: int = 1000, spss: int = 20):
    params = urllib.parse.urlencode(
        {"q": query, "start": start, "end": end, "limit": limit, "spss": spss}
    )
    return get_json(f"{tempo}/api/search?{params}")


def attr_value(entry: dict):
    value = entry["value"]
    if "stringValue" in value:
        return value["stringValue"]
    if "intValue" in value:
        return str(value["intValue"])
    return next(iter(value.values()), None)


def spans_of(result: dict):
    for trace in result.get("traces", []):
        span_sets = trace.get("spanSets") or ([trace["spanSet"]] if trace.get("spanSet") else [])
        for span_set in span_sets:
            for span in span_set.get("spans", []):
                attrs = {a["key"]: attr_value(a) for a in span.get("attributes", [])}
                yield trace["traceID"], span, attrs


class Report:
    def __init__(self) -> None:
        self.failed = 0

    def check(self, ok: bool, text: str) -> None:
        print(("PASS  " if ok else "FAIL  ") + text)
        if not ok:
            self.failed += 1

    def info(self, text: str) -> None:
        print("      " + text)


def check_processing_lanes(report: Report, tempo: str, start: int, end: int, expected: int) -> str | None:
    result = search(tempo, PROCESSING + SELECT, start, end)
    spans = []
    for trace_id, span, attrs in spans_of(result):
        spans.append(
            {
                "trace": trace_id,
                "start": int(span["startTimeUnixNano"]),
                "dur": int(span["durationNanos"]),
                "service": attrs.get("service.name"),
                "instance": attrs.get("service.instance.id"),
                "store": attrs.get("messaging.store.name"),
                "partition": attrs.get("messaging.partition.id"),
            }
        )
    report.check(bool(spans), f"processing spans found in window: {len(spans)}")
    if not spans:
        return None

    per_store = collections.Counter(s["store"] for s in spans)
    expected_counts = {
        "PaymentsOutbox": expected,
        "CoreBankInbox": expected,
        "CoreBankOutbox": 3 * expected,
        "PaymentsInbox": 3 * expected,
    }
    for store in STORES:
        report.check(
            per_store.get(store, 0) == expected_counts[store],
            f"{store}: {per_store.get(store, 0)} processing spans (expected {expected_counts[store]})",
        )

    instances = collections.defaultdict(set)
    for s in spans:
        instances[s["service"]].add(s["instance"])
    for service in (PAYMENTS, COREBANK):
        ids = sorted(i for i in instances.get(service, set()) if i)
        report.check(len(ids) == 2, f"{service}: {len(ids)} replica instance id(s) {ids}")

    lanes = collections.defaultdict(list)
    for s in spans:
        lanes[(s["store"], s["partition"])].append(s)
    overlaps = 0
    single_replica_lanes = []
    for key in sorted(lanes, key=lambda k: (str(k[0]), str(k[1]))):
        lane = sorted(lanes[key], key=lambda s: s["start"])
        lane_overlaps = sum(
            1 for a, b in zip(lane, lane[1:]) if b["start"] < a["start"] + a["dur"]
        )
        overlaps += lane_overlaps
        by_instance = collections.Counter(str(s["instance"])[:8] for s in lane)
        if len(by_instance) < 2:
            single_replica_lanes.append(key)
        report.info(
            f"lane {key[0]}/{key[1]}: {len(lane)} spans, overlaps={lane_overlaps}, by replica {dict(by_instance)}"
        )
    report.check(overlaps == 0, f"same-store/partition overlapping processing spans: {overlaps}")
    report.check(
        not single_replica_lanes,
        f"lanes served by only one replica: {single_replica_lanes or 'none'}",
    )
    command_partitions = sorted({s["partition"] for s in spans if s["store"] in ("PaymentsOutbox", "CoreBankInbox")})
    report.info(f"command stores used partitions {command_partitions} (ADR-026: load accounts hash to 0, 1, 2)")

    # The longest complete trace is the best sample for the cross-hop check.
    per_trace = collections.Counter(s["trace"] for s in spans)
    return max(per_trace, key=per_trace.get)


def check_errors(report: Report, tempo: str, start: int, end: int, retries: int) -> None:
    result = search(
        tempo,
        '{ status = error } | select(resource.service.name, span.error.type, name)',
        start,
        end,
    )
    kinds = collections.Counter()
    error_traces = set()
    for trace_id, span, attrs in spans_of(result):
        kinds[(attrs.get("service.name"), span.get("name"), attrs.get("error.type"))] += 1
        error_traces.add(trace_id)
    dedupe = kinds.get((PAYMENTS, "postgresql", "23505"), 0)
    other = sum(count for key, count in kinds.items() if key != (PAYMENTS, "postgresql", "23505"))
    report.check(
        dedupe == retries,
        f"intentional duplicate replays (PaymentsAPI postgresql 23505): {dedupe} (expected {retries})",
    )
    report.check(other == 0, f"other error spans: {other} {dict(kinds) if other else ''}")

    reached = search(
        tempo,
        '{ status = error } && { resource.service.name = "' + COREBANK + '" }',
        start,
        end,
    )
    reached_count = len(reached.get("traces", []))
    report.check(reached_count == 0, f"error traces that also contain a CoreBank span: {reached_count}")


def check_latency(report: Report, tempo: str, start: int, end: int) -> None:
    slow = search(tempo, "{ duration > 500ms }", start, end)
    slow_count = sum(1 for _ in spans_of(slow))
    report.check(slow_count == 0, f"spans slower than 500 ms: {slow_count}")
    try:
        slow_traces = search(tempo, "{ traceDuration > 2s }", start, end)
        report.check(
            not slow_traces.get("traces"),
            f"traces longer than 2 s: {len(slow_traces.get('traces') or [])}",
        )
    except urllib.error.HTTPError as error:  # older Tempo without traceDuration
        report.info(f"traceDuration query unsupported ({error.code}); skipped")


def check_cross_hop(report: Report, tempo: str, trace_id: str) -> None:
    trace = get_json(f"{tempo}/api/traces/{trace_id}")
    service_of_span = {}
    parent_of_span = {}
    for batch in trace.get("batches", []):
        resource = {a["key"]: attr_value(a) for a in batch.get("resource", {}).get("attributes", [])}
        for scope in batch.get("scopeSpans", []):
            for span in scope.get("spans", []):
                service_of_span[span["spanId"]] = resource.get("service.name")
                parent_of_span[span["spanId"]] = span.get("parentSpanId")
    edges = collections.Counter()
    for span_id, parent in parent_of_span.items():
        if parent and parent in service_of_span and service_of_span[parent] != service_of_span[span_id]:
            edges[(service_of_span[parent], service_of_span[span_id])] += 1
    http_hop = edges.get((PAYMENTS, COREBANK), 0)
    dapr_hop = sum(count for (src, dst), count in edges.items() if dst == PAYMENTS and src != PAYMENTS)
    report.check(http_hop > 0, f"trace {trace_id[:16]}: PaymentsAPI -> CoreBank parent link over HTTP ({http_hop})")
    report.check(dapr_hop > 0, f"trace {trace_id[:16]}: inbound parent link into PaymentsAPI over Dapr ({dapr_hop})")
    report.info(f"cross-service parent edges: {dict(edges)}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--tempo", default="http://localhost:3200")
    parser.add_argument("--k6-start", help="k6 'Starting load test' timestamp (UTC ISO 8601); window = start-5s .. start+90s")
    parser.add_argument("--start", help="window start, ISO 8601 UTC")
    parser.add_argument("--end", help="window end, ISO 8601 UTC")
    parser.add_argument("--expected", type=int, default=100, help="LoadTest:TransactionCount (default 100)")
    parser.add_argument("--retries", type=int, default=10, help="k6 intentional retries (default 10)")
    args = parser.parse_args()

    if args.k6_start:
        start = iso_to_epoch(args.k6_start) - 5
        end = start + 95
    elif args.start and args.end:
        start, end = iso_to_epoch(args.start), iso_to_epoch(args.end)
    else:
        parser.error("give --k6-start, or --start and --end")

    report = Report()
    try:
        ready = urllib.request.urlopen(f"{args.tempo}/ready", timeout=10).status
    except (urllib.error.URLError, OSError) as error:
        print(f"SKIP  Tempo unreachable at {args.tempo}: {error}")
        return 2
    report.check(ready == 200, f"Tempo ready at {args.tempo}")
    window = f"{dt.datetime.fromtimestamp(start, dt.timezone.utc).isoformat()} .. {dt.datetime.fromtimestamp(end, dt.timezone.utc).isoformat()}"
    report.info(f"window {window}")

    sample = check_processing_lanes(report, args.tempo, start, end, args.expected)
    check_errors(report, args.tempo, start, end, args.retries)
    check_latency(report, args.tempo, start, end)
    if sample:
        check_cross_hop(report, args.tempo, sample)

    print(f"\n{'VERDICT: PASS' if report.failed == 0 else f'VERDICT: FAIL ({report.failed} check(s))'}")
    return 1 if report.failed else 0


if __name__ == "__main__":
    sys.exit(main())
