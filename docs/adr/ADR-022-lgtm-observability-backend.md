# ADR-022: Replace Jaeger with Grafana LGTM for all telemetry signals

**Date:** 2026-09-16
**Status:** Accepted
**Amended by:** ADR-025 (retention: one day; component configs mounted from `observability/lgtm/`)
**Deciders:** Architecture team
**Supersedes in part:** ADR-003 (telemetry backend only; the OpenTelemetry instrumentation
and trace-propagation decisions stand)

## Context

Both AppHosts ran `jaegertracing/all-in-one` as the only telemetry backend, which covered
traces and nothing else:

- **Logs were never exported.** `ServiceDefaults/Extensions.cs` configured OpenTelemetry
  logging but attached no exporter, so structured logs never left the process.
- **Metrics were dropped.** The metrics pipeline exported over OTLP to Jaeger, which stores
  traces only, so the `CoreBankDemo.Business` meter (payment and transaction intake and
  processed counters, Inbox/Outbox store operations, items processed, deliveries, queue and
  instant-payment duration histograms) was invisible.

For the on-stage load-test demo the audience should see throughput, latency, the
Inbox/Outbox drain, and logs next to traces, in one UI.

## Decision

- **One `grafana/otel-lgtm` container, pinned to `0.33.0`** (OpenTelemetry Collector,
  Tempo, Loki, Prometheus, Grafana 13.2.1 in one image), declared identically by both
  AppHosts as resource `lgtm`, Docker container `corebank-lgtm`, with
  `ContainerLifetime.Persistent` and a named `corebank-lgtm-data` volume mounted at `/data`
  (where Grafana, Loki, Prometheus and Tempo keep their state). Whichever AppHost starts
  reuses the running container, so telemetry history carries across both. Never `latest`.
- **`OTLP_ENDPOINT`** replaces `JAEGER_OTLP_ENDPOINT` as the key `ResolveOtlpEndpoint` reads;
  the parsing rules are unchanged and there is no fallback to the old key. The logging
  pipeline gets an OTLP exporter alongside the existing metric and trace exporters, through
  one shared `ApplyOtlpEndpoint` helper.
- **Provisioned dashboard.** `observability/grafana/dashboards/corebank.json` (uid `corebank`,
  a contract DemoRunner deep-links to as `http://localhost:3000/d/corebank`) and its provider
  `dashboards.yaml` are bind-mounted read-only into the container. Grafana reads provider
  YAML only from the top level of `provisioning/dashboards`, never from a subdirectory, so the
  provider file is mounted there as a single file next to the directory mount the provider
  points at. A drift test in `CoreBankDemo.ServiceDefaults.Tests` asserts the uid and that
  every `corebankdemo_*` metric the dashboard queries maps to a `BusinessMetrics`
  instrument name.
- **Traces dashboard as home page.** `observability/grafana/dashboards/corebank-traces.json`
  (uid `corebank-traces`) is a Jaeger-style trace search: Service and Operation filters (from
  Tempo span metrics), rate, errors and p95 for that operation, the matching end-to-end traces,
  and, since ADR-030, the CoreBank circuit panel (breaker transitions from Polly's meter; it
  replaced Tempo's service map, which the talk does not use). Below that, a row of totals for the
  dashboard's selected time range (until 2026-10-24 a "Today" row pinned to `now/d`; it now
  follows the time picker so a rehearsal burst shows its own numbers rather than the whole
  day's), in the order a payment flows: payments received (stored in the Payments outbox),
  transactions processed, messages sent (the `transaction-completed` or `transaction-failed`
  result event from CoreBank's outbox), and failed payments. Received equals processed plus
  cancellations unless there is an outage. They are exact counts (the last value each
  `service_instance_id` series reported inside the range, less its last value before the range
  opened), not `increase()`, which skips a series' first sample and under-reports. Each total is scoped
  to the `service_name` that owns it, so another process exporting to the same collector (a
  test run reporting as `test-service`, for one) cannot move it. The per-instrument
  charts live on the CoreBank dashboard only. Both AppHosts set
  `GF_DASHBOARDS_DEFAULT_HOME_DASHBOARD_PATH` to the provisioned file, so Grafana opens on it;
  drift tests check the path in both AppHosts, the three totals and their order, and that every
  queried metric is a `BusinessMetrics` instrument.
- **Anonymous Admin access** (`GF_AUTH_ANONYMOUS_*`) so there is no login screen on stage;
  acceptable for a local development image. The Grafana UI binds `0.0.0.0` for the same
  sandbox port-publish reason the Jaeger UI did; OTLP (4317/4318) and Tempo (3200) stay on
  loopback.
- **Naming rule.** Everything this change introduces is named `LGTM` / `Lgtm` / `lgtm`, never
  "Grafana", because `grafana/k6` is already part of the LoadTests topology. "Grafana"
  appears only where the product itself is meant: the dashboard JSON, `GF_*` variables,
  `mcp-grafana`, and the UI in prose.
- **Agent tooling.** A `grafana` server (`uvx mcp-grafana`) covers Prometheus, Loki and
  dashboards. Trace analysis queries Tempo directly (`http://localhost:3200`, TraceQL with
  `select()`), through the `corebank-trace-analysis` skill's bundled script. The
  `opentelemetry-mcp` server originally configured here was removed on 2026-10-12: it crashed on
  the sandbox's bracketed `[::1]` `NO_PROXY` entry, reported span times without a zone, and its
  span summaries carried none of the attributes (replica id, store, partition) the acceptance
  verdict needs, all of which one TraceQL query returns.

Rejected: separate Collector, Tempo, Loki, Prometheus and Grafana containers (five configs
and failure points for a live demo); a Collector fan-out keeping Jaeger for traces (two UIs
and duplicate trace storage); the Aspire dashboard alone (in-memory, no custom dashboards).

## Consequences

- An all-in-one development image, not a production topology; production would use managed
  backends behind the same OTLP export.
- Both AppHost declarations must stay byte-for-byte equivalent (image, tag, environment,
  ports, mounts, volume); any difference makes Aspire recreate the container and the shared
  history is lost. Running both AppHosts at once is not a supported scenario.
- Port 3000 (Grafana) and 3200 (Tempo) join the fixed-port set; 16686 leaves it. Port 3000 is
  also a common dev-server port, so DemoRunner's Doctor port check surfaces a clash before
  Start and treats the port held by the healthy persistent container as the normal state.
- Trace analysis tooling moves to Tempo; the `corebank-trace-analysis` skill still analyses
  traces only (extending it to metrics and logs is deferred in `docs/backlog.md`).
- Dapr sidecar logs are still not collected; the sidecars export traces only.
- The dashboard shows Inbox/Outbox in-vs-out rates, not backlog depth: no instrument reports
  pending rows, and subtracting cumulative counters is wrong once a replica restarts. A
  per-store depth gauge is deferred (`docs/backlog.md`).

## Key takeaway

> One pinned, persistent LGTM container shared by both AppHosts turns the metrics and logs the
> services already produce into something the audience can see, without touching the banking
> services themselves.
