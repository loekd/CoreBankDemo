"""Unit tests for the latency check of processing-lanes.py.

Run from the repository root:

    python3 -m pytest .claude/skills/corebank-trace-analysis/scripts/tests -q

Tempo is replaced by a fake ``search`` so the tests pin the classification
rule, not the TraceQL transport. The instant rail waits for its turn inside
its budget (ADR-018, ADR-020), so an instant request's root span may run up
to the PaymentsAPI budget and the spans on its inline forward path up to the
per-attempt timeout; every other span keeps the 500 ms bar.
"""

from __future__ import annotations

import importlib.util
import pathlib
import sys

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "processing-lanes.py"
spec = importlib.util.spec_from_file_location("processing_lanes", SCRIPT)
lanes = importlib.util.module_from_spec(spec)
sys.modules["processing_lanes"] = lanes
spec.loader.exec_module(lanes)

MS = 1_000_000  # nanoseconds per millisecond


def span(span_id: str, duration_ms: int) -> dict:
    return {"spanID": span_id, "durationNanos": str(duration_ms * MS), "attributes": []}


def trace(trace_id: str, spans: list[dict], duration_ms: int | None = None) -> dict:
    result = {"traceID": trace_id, "spanSets": [{"spans": spans}]}
    if duration_ms is not None:
        result["durationMs"] = duration_ms
    return result


class FakeTempo:
    """Answers the four TraceQL queries check_latency issues from canned data."""

    def __init__(self, *, slow, slow_roots, instant_traces, slow_traces):
        self.slow = slow
        self.slow_roots = slow_roots
        self.instant_traces = instant_traces
        self.slow_traces = slow_traces
        self.queries: list[str] = []

    def __call__(self, tempo, query, start, end, limit=1000, spss=20):
        self.queries.append(query)
        if "messaging.inline" in query:
            return {"traces": self.instant_traces}
        if "traceDuration" in query:
            return {"traces": self.slow_traces}
        if "nestedSetParent" in query:
            return {"traces": self.slow_roots}
        return {"traces": self.slow}


class RecordingReport(lanes.Report):
    def __init__(self) -> None:
        super().__init__()
        self.lines: list[str] = []

    def check(self, ok: bool, text: str) -> None:
        self.lines.append(("PASS " if ok else "FAIL ") + text)
        if not ok:
            self.failed += 1

    def info(self, text: str) -> None:
        self.lines.append("INFO " + text)


def run(fake: FakeTempo, budget_ms: int = 9000, attempt_ms: int = 2500) -> RecordingReport:
    report = RecordingReport()
    lanes.check_latency(report, "http://tempo", 0, 1, budget_ms, attempt_ms, search_fn=fake)
    return report


def test_instant_root_span_within_budget_is_not_slow():
    fake = FakeTempo(
        slow=[trace("instant", [span("root", 1882)])],
        slow_roots=[trace("instant", [span("root", 1882)])],
        instant_traces=[trace("instant", [span("inline", 8)])],
        slow_traces=[],
    )
    report = run(fake)
    assert report.failed == 0, report.lines


def test_instant_root_span_over_budget_is_slow():
    fake = FakeTempo(
        slow=[trace("instant", [span("root", 9500)])],
        slow_roots=[trace("instant", [span("root", 9500)])],
        instant_traces=[trace("instant", [span("inline", 8)])],
        slow_traces=[],
    )
    report = run(fake)
    assert report.failed == 1, report.lines


def test_instant_forward_path_span_within_attempt_timeout_is_not_slow():
    fake = FakeTempo(
        slow=[trace("instant", [span("corebank", 647)])],
        slow_roots=[],
        instant_traces=[trace("instant", [span("inline", 8)])],
        slow_traces=[],
    )
    report = run(fake)
    assert report.failed == 0, report.lines


def test_instant_forward_path_span_over_attempt_timeout_is_slow():
    fake = FakeTempo(
        slow=[trace("instant", [span("corebank", 2600)])],
        slow_roots=[],
        instant_traces=[trace("instant", [span("inline", 8)])],
        slow_traces=[],
    )
    report = run(fake)
    assert report.failed == 1, report.lines


def test_standard_rail_span_keeps_the_500ms_bar():
    fake = FakeTempo(
        slow=[trace("standard", [span("root", 600)])],
        slow_roots=[trace("standard", [span("root", 600)])],
        instant_traces=[],
        slow_traces=[],
    )
    report = run(fake)
    assert report.failed == 1, report.lines


def test_instant_trace_duration_bar_is_budget_plus_two_seconds():
    fake = FakeTempo(
        slow=[],
        slow_roots=[],
        instant_traces=[trace("ok", [span("inline", 8)]), trace("late", [span("inline", 8)])],
        slow_traces=[trace("ok", [], duration_ms=2100), trace("late", [], duration_ms=11500)],
    )
    report = run(fake)
    assert report.failed == 1, report.lines
    assert any("traces longer than" in line and "late" not in line for line in report.lines)


def test_standard_trace_over_two_seconds_is_slow():
    fake = FakeTempo(
        slow=[],
        slow_roots=[],
        instant_traces=[],
        slow_traces=[trace("standard", [], duration_ms=2100)],
    )
    report = run(fake)
    assert report.failed == 1, report.lines
