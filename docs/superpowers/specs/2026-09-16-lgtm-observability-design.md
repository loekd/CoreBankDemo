# Replace Jaeger with Grafana LGTM for traces, logs, and metrics

**Date:** 2026-09-16
**Status:** Draft
**Author:** Claude (Opus 5), on behalf of Loek Duys

## Context

Both AppHosts run `jaegertracing/all-in-one:1.66.0` as the only telemetry backend. That covers
traces and nothing else:

- **Logs are never exported.** `CoreBankDemo.ServiceDefaults/Extensions.cs`
  (`ConfigureOpenTelemetry`) calls `builder.Logging.AddOpenTelemetry(...)` but attaches no
  exporter, so structured logs never leave the process.
- **Metrics are dropped.** The metrics pipeline exports over OTLP gRPC to the Jaeger endpoint,
  and Jaeger stores traces only. The `CoreBankDemo.Business` meter (`BusinessMetrics`: payment
  and transaction intake/processed counters, Inbox/Outbox store operations, items processed,
  deliveries, queue-duration and instant-payment-duration histograms) is invisible.

For the local load-test demo on stage, the audience should see throughput, latency, the
Inbox/Outbox drain, and logs next to traces, all in one UI.

## Decision summary

Replace the `jaeger` container in **both** AppHosts with a single `grafana/otel-lgtm` container
(OpenTelemetry Collector, Tempo, Loki, Prometheus, Grafana in one image), shared by both AppHosts,
persistent, with a named data volume and a provisioned CoreBank dashboard. Export logs from every
service. Point agent tooling at Tempo (`opentelemetry-mcp`) and Grafana (`mcp-grafana`).

Alternatives considered and rejected:

- **Separate Collector, Tempo, Loki, Prometheus, and Grafana containers.** Closer to production,
  but five containers and five configs add startup time and failure points to a live demo.
- **Collector fan-out that keeps Jaeger for traces and adds LGTM for logs and metrics.** Two UIs
  on stage and duplicate trace storage, which defeats the purpose of the swap.
- **Aspire dashboard.** Supports all three signals, but is in-memory only and has no custom
  dashboards.

## Naming rule

Everything this change introduces is named **LGTM**, never "Grafana", because `grafana/k6` is
already part of the LoadTests topology and "Grafana" would be ambiguous. "Grafana" appears only
where the product itself is meant: the Grafana dashboard JSON, `GF_*` environment variables,
`mcp-grafana`, and the Grafana UI in prose.

| Thing | Name |
|---|---|
| Aspire resource | `lgtm` |
| Docker container | `corebank-lgtm` |
| Docker volume | `corebank-lgtm-data` |
| DemoRunner constants | `KnownResources.Lgtm`, `KnownLinks.Lgtm` |
| DemoRunner header button and short tag | `LGTM`, `lgtm` |

## 1. Hosting (both AppHosts)

`CoreBankDemo.AppHost/AppHost.cs` and `CoreBankDemo.LoadTests/AppHost.cs` replace the `jaeger`
container with the same declaration:

```csharp
var lgtm = builder.AddContainer("lgtm", "grafana/otel-lgtm", "<pinned tag>")
    .WithContainerName("corebank-lgtm")
    .WithHttpEndpoint(port: 3000, targetPort: 3000, name: "grafana")
    .WithEndpoint(port: 4317, targetPort: 4317, name: "otlp-grpc")
    .WithEndpoint(port: 4318, targetPort: 4318, name: "otlp-http")
    .WithHttpEndpoint(port: 3200, targetPort: 3200, name: "tempo")
    .WithEndpointProxySupport(false)
    .WithEndpoint("grafana", endpoint => endpoint.TargetHost = "0.0.0.0")
    .WithEnvironment("GF_AUTH_ANONYMOUS_ENABLED", "true")
    .WithEnvironment("GF_AUTH_ANONYMOUS_ORG_ROLE", "Admin")
    .WithBindMount(
        Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "observability", "grafana", "dashboards")),
        "/otel-lgtm/grafana/conf/provisioning/dashboards/custom",
        isReadOnly: true)
    .WithVolume("corebank-lgtm-data", "/data")
    .WithHttpHealthCheck("/api/health", endpointName: "grafana")
    .WithLifetime(ContainerLifetime.Persistent);
var lgtmOtlpGrpcEndpoint = lgtm.GetEndpoint("otlp-grpc");
```

- **Pinned tag.** An exact `grafana/otel-lgtm` release, chosen at implementation time and
  recorded in ADR-022. Never `latest`.
- **One shared container.** Both AppHosts declare the container identically under the fixed
  name `corebank-lgtm`, so whichever AppHost starts reuses the running container and telemetry
  history carries across both. **The two declarations must stay byte-for-byte equivalent**
  (image, tag, environment, ports, mounts, volume); any difference makes Aspire recreate the
  container. Both AppHosts are sibling directories, so the bind-mount path resolves to the same
  absolute path. Running both AppHosts at the same time is not a supported scenario and needs no
  handling.
- **Grafana UI on `0.0.0.0`.** Same reasoning as the existing `jaeger-ui` comment: the UI must be
  reachable through a host-side port publish when the AppHost runs in a sandbox. The OTLP and
  Tempo endpoints stay on the default bind; only in-sandbox services and agent tooling dial them.
- **Anonymous access.** No login screen on stage. Acceptable because this is a local development
  image.
- **Health.** The APIs `.WaitFor(lgtm)`; the HTTP health check on `/api/health` makes that wait
  for Grafana to be ready, not merely for the container to exist (startup is roughly 10-20s).
- **Environment variable.** The APIs receive `.WithEnvironment("OTLP_ENDPOINT", lgtmOtlpGrpcEndpoint)`
  in place of `JAEGER_OTLP_ENDPOINT`.
- **Dapr sidecars.** `dapr/components/otel-config.yaml` and
  `dapr/components-loadtest/otel-config.yaml` already export to `localhost:4317`. No change.

## 2. ServiceDefaults

All changes are in `CoreBankDemo.ServiceDefaults/Extensions.cs`.

**Rename the endpoint key.** `ResolveOtlpEndpoint` reads `OTLP_ENDPOINT` instead of
`JAEGER_OTLP_ENDPOINT`. Its parsing rules are unchanged (explicit `://` gate, `tcp://` rewritten to
`http://` with default port 4317, bare `host:port` normalized to `http://`, invalid values throw).
Only the key, the "Prefer explicit Jaeger endpoint" comment, and the exception message change.
No fallback to the old key: only this repository's AppHosts set it.

**Export logs.** The logging pipeline gets an OTLP exporter, following the same
configured-endpoint-or-default pattern metrics and tracing already use:

```csharp
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    // OTLP exporter: configured endpoint over gRPC, else the OTEL_EXPORTER_OTLP_* defaults.
});
```

The "endpoint + gRPC protocol, else default" block now appears for three signals, so it moves
into one private helper used by logs, metrics, and traces. `IncludeFormattedMessage` and
`IncludeScopes` stay; log records carry trace and span IDs, which is what lets Grafana link a
Loki log line to its Tempo trace.

**Not used: `UseOtlpExporter()`.** It configures all three signals in one call but cannot be
combined with the existing per-signal `AddOtlpExporter` calls, so adopting it means rewriting the
tested wiring for no functional gain.

**Unchanged.** `ConfigureResource(resource => resource.AddService(serviceName))` already sets
`service.name` for all three signals, which is the key Grafana filters on.

## 3. Provisioned CoreBank dashboard

**Files** (shared by both AppHosts through the bind mount in section 1):

- `observability/grafana/dashboards/dashboards.yaml`: the dashboard provisioning provider. Folder
  "CoreBank", `allowUiUpdates: true` so panels can be tweaked live during rehearsal, file-watching
  on so edits to the JSON load without restarting the container.
- `observability/grafana/dashboards/corebank.json`: the dashboard, with the fixed uid `corebank`.
  DemoRunner deep-links to `http://localhost:3000/d/corebank`, so the uid is a contract.

**Dashboard defaults.** Time range last 15 minutes, auto-refresh 5s, a `service` template variable
(all / `payments-api` / `corebank-api`). Queries use the Prometheus, Loki, and Tempo datasources the
image provisions.

**Rows and panels**, ordered the way a payment flows:

1. **Payments**
   - Payment intake rate by `outcome` (`stored`, `duplicate`, `validation_failed`) and
     `payment.scheme` (`standard`, `instant`).
   - Instant-payment duration p50/p95/p99 by `outcome` (`settled`, `rejected`, `deferred`,
     `cancelled`).
2. **CoreBank**
   - Transaction intake rate by `outcome` (`accepted`, `replayed`, `in_flight`,
     `transport_failed`).
   - Transactions processed rate by `outcome` (`completed`, `business_rejected`).
3. **Inbox/Outbox**
   - In vs out per store: `store.operations{outcome="added"}` rate against
     `items.processed{outcome="completed"}` rate, per `messaging.store.name`
     (`payments-outbox`, `corebank-inbox`, `corebank-outbox`, `payments-inbox`). The lines meet once
     the backlog drains.
   - Queue duration p95 per store.
   - Retries and failures: `items.processed` for `retry_scheduled`, `terminal_failed`,
     `completion_persistence_failed`, `retry_persistence_failed`.
   - Deliveries by `messaging.transport` and `messaging.message.type`, with `duplicate` counts
     shown separately to make exactly-once handling visible.
4. **HTTP and runtime**
   - Server request rate, 5xx rate, and p95 duration per service (ASP.NET Core instrumentation).
   - HttpClient p95 duration for the PaymentsAPI to CoreBankAPI hop, where Dev Proxy latency
     injection shows up.
   - GC and thread-pool basics (runtime instrumentation).
5. **Logs and traces**
   - Loki logs panel: severity warning and above, filtered by `service`. Log lines link to Tempo by
     trace ID.
   - Tempo table: error traces (`{ status = error }`) and the slowest traces.

**No backlog-depth gauge.** No instrument reports Inbox/Outbox depth, and subtracting cumulative
counters is wrong once a replica restarts. The dashboard shows in-vs-out rates instead. A real
depth gauge is deferred (see Out of scope).

**How the dashboard is produced.** Build it in the Grafana UI against a live LoadTests run, export
the JSON, commit it. Prometheus metric and label names come from the OTLP-to-Prometheus translation
(for example `corebankdemo_payment_intake_total`,
`corebankdemo_messaging_queue_duration_milliseconds_bucket`, and whether `service.name` surfaces as
`job` or `service_name`); read the real names from the running Prometheus
(`/api/v1/label/__name__/values`) rather than assuming them. Datasource references use the uids the
image provisions, likewise read from the running instance.

## 4. DemoRunner

A rename to the new resource, port, and link. No new behavior.

| Location | Today | After |
|---|---|---|
| `Application/KnownOperatorSurface.cs` `KnownResources` | `Jaeger = "jaeger"` | `Lgtm = "lgtm"` |
| `Application/KnownOperatorSurface.cs` `KnownLinks` | `Jaeger = "jaeger"` | `Lgtm = "lgtm"` |
| `PersistentInfrastructure`, `ResourceCommandAllowList`, `RequiredFor(Regular)`, `RequiredFor(LoadTests)` | `Jaeger` | `Lgtm` |
| `ExpectedEndpointPorts` (both profiles) | `[Jaeger] = 16686` | `[Lgtm] = 3000` |
| `Infrastructure/EndpointResolver.cs` probe URL | `http://127.0.0.1:16686/` | `http://127.0.0.1:3000/api/health` |
| `Infrastructure/EndpointResolver.cs` link URL | `http://localhost:16686/` | `http://localhost:3000/d/corebank` |
| `EndpointResolver.RegularProfilePorts` | `[Jaeger] = 16686` | `[Lgtm] = 3000` |
| `EndpointResolver.LoadTestProfilePorts` | no telemetry entry | `[Lgtm] = 3000` (the container is now persistent under LoadTests too) |
| `Terminal/MainWindow.cs` header button | `"Jaeger"`, `_jaegerButton`, `JaegerButton` | `"LGTM"`, `_lgtmButton`, `LgtmButton`; anchor offset adjusted to the label width |
| `Terminal/PresentationModel.cs` short tag | `"jae"` | `"lgtm"` |
| Doctor remediation text | persistent jaeger container, `docker ps --filter publish=16686` | persistent LGTM container `corebank-lgtm`, `docker ps --filter publish=3000` |
| `Infrastructure/BrowserLauncher.cs` summary | "Aspire or Jaeger URL" | "Aspire or LGTM URL" |

- **Port 3000** is Grafana's default and also a common dev-server port. Doctor's existing
  "port held by something else" check surfaces a clash before Start.
- **ADR-015** (operator input never supplies a URL) still holds: the dashboard URL is a constant in
  `EndpointResolver`.

## 5. Tooling, docs, and ADR

**Agent tooling**

- `.mcp.json`
  - `opentelemetry-mcp`: `BACKEND_TYPE=tempo`, `BACKEND_URL=http://localhost:3200`. Still traces
    only.
  - New `grafana` server: `uvx mcp-grafana` with `GRAFANA_URL=http://localhost:3000`,
    `GRAFANA_USERNAME=admin`, `GRAFANA_PASSWORD=admin` (the image's local defaults; `mcp-grafana`
    documents no anonymous mode). Covers Prometheus, Loki, and dashboards. It has no Tempo query
    tools, so it complements `opentelemetry-mcp` rather than replacing it.
- `.claude/skills/corebank-trace-analysis/SKILL.md`: wording only. "Jaeger may have crashed"
  becomes "LGTM container not running", the "Jaeger name" column becomes "Service name", and the
  trigger line says Tempo. Tempo reports the same `service.name` values. If any step relies on a
  Jaeger-specific query parameter, adjust it for Tempo. Extending the skill to use metrics and logs
  is deferred.
- `mcp-config.example.json` and `opencode.json` contain no telemetry backend today and are left
  unchanged.

**ADR**

- New `docs/adr/ADR-022-lgtm-observability-backend.md`: Replace Jaeger with Grafana LGTM for all
  telemetry signals.
  - Context: logs never exported, metrics dropped by Jaeger.
  - Decision: one pinned `grafana/otel-lgtm` container shared by both AppHosts (`corebank-lgtm`,
    persistent, `corebank-lgtm-data` volume), fed through `OTLP_ENDPOINT`, provisioned `corebank`
    dashboard, LGTM naming rule.
  - Consequences: all-in-one development image, not a production topology; both AppHost
    declarations must stay identical for reuse; trace MCP moves to Tempo; Dapr sidecar logs are
    still not collected; port 3000 joins the fixed-port set.
- `docs/adr/ADR-003-distributed-tracing-opentelemetry.md`: add a "Superseded in part by ADR-022
  (telemetry backend)" status line. The body stays as written; the OpenTelemetry instrumentation
  decision stands.

**Docs**

- `README.md`: architecture blurb, prerequisites, URL table (LGTM `http://localhost:3000`,
  dashboard `/d/corebank`, anonymous access), "No traces in Jaeger" troubleshooting becomes "No
  telemetry in LGTM" with `OTLP_ENDPOINT`, ports list (16686 out; 3000 and 3200 in), and a note
  that the MCP servers need `uvx`.
- `ARCHITECTURE.md`: tech table, container diagram box, ports table, and the "visibility in Jaeger
  UI" lines.
- `AGENTS.md` and `.github/copilot-instructions.md`: "Jaeger" becomes "LGTM (Grafana, Tempo, Loki,
  Prometheus)".
- `.devcontainer/devcontainer.json`: forwarded ports and labels, 16686 out, 3000 and 3200 in.
- `docs/backlog.md`: story 6.1 and 6.4 acceptance criteria that name Jaeger say LGTM; add the
  deferred items listed under Out of scope.

**Left unchanged**

- Past specs and plans under `docs/superpowers/` are historical records.
- `.serena/memories/` belongs to another tool.

## 6. Testing and verification

**Unit tier** (`dotnet test CoreBankDemo.UnitTests.slnf`, Docker-free), test-first:

- `tests/CoreBankDemo.ServiceDefaults.Tests/Extensions/ResolveOtlpEndpointTests.cs` and
  `AddServiceDefaultsTests.cs`: use `OTLP_ENDPOINT`; sample hosts `jaeger:4317` become `lgtm:4317`.
- New test: with an endpoint configured, an OTLP log exporter is registered for the logging
  pipeline, asserted through the built service provider in the style of the existing registration
  test.
- New dashboard drift test in `CoreBankDemo.ServiceDefaults.Tests`: parses
  `observability/grafana/dashboards/corebank.json`, asserts uid `corebank`, and asserts every
  `corebankdemo_*` metric referenced in a query maps to a `BusinessMetrics` instrument name
  constant under the OTLP-to-Prometheus translation. Renaming an instrument then fails the build
  instead of blanking a panel.
- DemoRunner tests updated from Jaeger to LGTM: `EndpointResolverTests`, `AspireJsonParserTests`,
  `AspireAdapterTests`, `BrowserLauncherTests`, `MainWindowTests`,
  `OperatorConsoleControllerTests`, `DoctorRunnerTests`. One new case: `LoadTestProfilePorts`
  contains `[Lgtm] = 3000`.
- The ≥90% coverlet line-coverage gate still applies.

**Integration tier** (`CoreBankDemo.IntegrationTests.slnf`): no change.

**Acceptance harness**: a full `load-test` run on the LoadTests AppHost. All existing assertions
(exactly-once, no message loss, balance conservation, drain, per-key ordering) must still pass; the
backend swap must not change system behavior.

**Build gate**: `dotnet build` and `dotnet test` on `CoreBankDemo.Rebuild.slnf` green.

**Manual verification checklist** (record the actual result of each; a step that cannot run is
recorded as skipped, never as passed):

1. LoadTests AppHost: `lgtm` turns healthy, the APIs wait for it, and
   `http://localhost:3000/d/corebank` opens without a login.
2. During the k6 run, every dashboard row shows data, the in-vs-out lines meet after drain, the
   logs panel shows entries, and a log line links to its Tempo trace.
3. One payment renders as one trace in Tempo, including Dapr sidecar spans (NFR-2).
4. Shared container: stop LoadTests, start Regular; `docker ps` shows the same `corebank-lgtm`
   container ID, and data from the load-test run is still visible.
5. MCP: `opentelemetry-mcp` returns traces from Tempo; `mcp-grafana` returns a PromQL result. Both
   need `uvx` on PATH.
6. DemoRunner: the LGTM button opens the dashboard; Doctor treats port 3000 held by the persistent
   container as the normal steady state.

## Out of scope

Recorded as deferred items in `docs/backlog.md`:

- **Inbox/Outbox backlog-depth gauge.** A per-store observable gauge of pending rows, so the LGTM
  dashboard can show true backlog depth instead of in-vs-out rates. It adds an instrument to the
  banking services, so it needs its own design.
- **Metrics and logs in `corebank-trace-analysis`.** Extend the skill to check error rate, retries,
  and terminal failures through `mcp-grafana` before analyzing traces.

Not planned:

- Collecting Dapr sidecar logs (needs a stdout or file collector).
- k6 metrics export to LGTM.
- Exemplars linking histogram buckets to traces.
