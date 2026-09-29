# LGTM keeps one day, and idle services stop writing logs

**Date:** 2026-09-29
**Status:** Draft
**Author:** Claude (Fable 5.1), on behalf of Loek Duys

## Context

The `corebank-lgtm-data` volume (ADR-022) is **13.66 GB** after thirteen days of local
development. Measured on 2026-09-29:

| Store | Size | Notes |
|---|---|---|
| Tempo blocks | 10 GB | 134 blocks since 2026-09-16; 0.2–2.6 GB per active day |
| Loki chunks + WAL | 1.9 GB | ~200 MB per day **while idle** (28–29 Sep, no payments sent) |
| Prometheus TSDB | 0.8 GB | |
| Pyroscope | 137 MB | not written by our services; out of scope |

Two causes, independent of each other:

1. **Nothing is ever deleted.** The `grafana/otel-lgtm:0.33.0` image ships Tempo, Loki and
   Prometheus with no retention configured. Tempo's own default is 14 days
   (`block_retention: 336h`), Prometheus's is 15 days, Loki's retention is off entirely (no
   compactor retention), so the volume only grows.
2. **Idle services write ~38 million log lines a day.** In the last 24 h Loki ingested
   21.07 M lines, all at Information, all from the 200 ms poll ticks of the outbox/inbox
   processors (2 services × 2 replicas × 4 partitions × inbox + outbox × 5 ticks/s). In the
   last hour: 1,589,977 lines, of which

   | Source | Lines/hour | Where it is logged |
   |---|---|---|
   | `Acquired lock {LockName} with {ExpirySeconds}s lease` / `Released lock {LockName}` | 1,062,198 (67 %) | `RedisDistributedLockService`, `LogInformation` |
   | `Executed DbCommand (…) SELECT …` (the claim query) | 527,674 (33 %) | EF Core `Microsoft.EntityFrameworkCore.Database.Command`, Information |
   | anything else | 0 | |

The trace side of the same poll ticks was removed on 2026-09-25/27 (`TraceNoiseFilter`,
`TraceSuppression`); this spec is the storage and logging counterpart.

## Decisions

Confirmed with the user on 2026-09-29: **retention one day for every signal**; **lock lines
demoted to Debug**; **EF Core command logging raised to Warning**.

### 1. One-day retention in Tempo, Loki and Prometheus

Retention is configured in the components' own config files, which the image's run scripts
read from fixed paths (`/otel-lgtm/tempo-config.yaml`, `/otel-lgtm/loki-config.yaml`,
`/otel-lgtm/prometheus.yaml`). The repository owns full copies of those three files under
`observability/lgtm/`, each equal to the image's file plus one retention block, and **both
AppHosts** bind-mount them read-only over the image's files — the same mechanism the
provisioned dashboards already use. The copies are tied to image tag `0.33.0`; bumping the
image means re-diffing them against the new image (a one-line note at the top of each file).

Exact additions, each verified against the binaries in the running `0.33.0` container:

- **Tempo 3.0.3** — `tempo -config.verify=true` accepts:
  ```yaml
  backend_scheduler:
    provider:
      compaction:
        compaction:
          block_retention: 24h
  backend_worker:
    compaction:
      block_retention: 24h
  ```
  Tempo 3 removed the `compactor` block (that spelling is rejected: "field compactor not
  found in type app.Config"); retention lives with the backend scheduler/worker pair.
  `compacted_block_retention` keeps its 1 h default.
- **Loki 3.7.7** — `loki -verify-config` reports "config is valid" for:
  ```yaml
  compactor:
    working_directory: /data/loki/compactor
    retention_enabled: true
    retention_delete_delay: 1h
    delete_request_store: filesystem
  limits_config:
    retention_period: 24h
  ```
- **Prometheus 3.14.0** — the `--storage.tsdb.retention.time` flag is deprecated in favour of
  the config file; `promtool check config` accepts:
  ```yaml
  storage:
    tsdb:
      retention:
        time: 1d
      out_of_order_time_window: 10m   # already there
  ```

Why config files rather than `*_EXTRA_ARGS` environment variables: the image supports
`TEMPO_EXTRA_ARGS`/`LOKI_EXTRA_ARGS`/`PROMETHEUS_EXTRA_ARGS`, but Prometheus's flag is
deprecated and Loki's retention needs a compactor block that is awkward as flags. One
mechanism for all three is simpler to read and to test.

**The existing 13 GB is reclaimed by the components themselves**, not by hand: Aspire
recreates a persistent container when its configuration changes (aspire.dev, "Configuration
changes can recreate persistent resources"), the new mounts are such a change, and on the next
start Tempo's hourly retention cycle marks blocks older than 24 h and deletes them one cycle
later (`compacted_block_retention: 1h`), Loki's compactor marks chunks for deletion and removes
them after the 1 h delay, and Prometheus drops out-of-retention blocks at its next compaction
(≤ 2 h). Observed on 2026-09-29: Tempo 10.2 GB → 2 MB at +2 h, Loki 1.74 GB → 380 MB at +75 min. Expected steady state after the logging changes below:
well under 1 GB. If Aspire does not recreate the container, `docker rm -f corebank-lgtm`
(the volume is untouched) and start the AppHost again — a manual step, to be confirmed with
the user first.

**Consequence for ADR-022:** "telemetry history carries across both AppHosts" still holds, for
one day. A short ADR records the retention decision and amends ADR-022 rather than editing it.

### 2. Lock acquire/release lines at Debug

`RedisDistributedLockService` logs `Acquired lock …` and `Released lock …` at Information on
every successful tick. Both move to `LogDebug`, next to the existing
`Failed to acquire lock` Debug line. Everything that signals a problem stays where it is:
`Lock … ownership was lost …` (Warning), `… operation cancelled by the caller`
(Information; rare), and the Error paths. Nothing else in the lock service changes.

### 3. EF Core command logging at Warning

Both services' `appsettings.json` gain
`"Microsoft.EntityFrameworkCore.Database.Command": "Warning"` under `Logging:LogLevel`.
`appsettings.Development.json` does not set that category, so the base file's value applies in
Development too. Query text stays available on the `postgresql` spans (`db.query.text`) of any
recorded trace; slow-query warnings and command errors still log.

### 4. Both AppHosts, and the drift tests that keep them equal

`CoreBankDemo.AppHost/AppHost.cs` and `CoreBankDemo.LoadTests/AppHost.cs` each gain three
`WithBindMount(..., isReadOnly: true)` lines next to the dashboard mounts (ADR-022: the two
`lgtm` declarations stay equivalent). `CoreBankDemo.ServiceDefaults.Tests` already links both
`AppHost.cs` files into its output and checks the home-dashboard path in each; it gains:

- a theory over both AppHosts asserting each of the three files is mounted at its exact
  container path, read-only;
- a test per config file that the retention setting is present with the agreed value, so a
  re-sync after an image bump cannot silently drop it;
- a test that both `appsettings.json` files set the EF Core command category to Warning
  (the two files are linked into the test output the same way).

## Naming rule

Names say what the thing is, not how it came about: `observability/lgtm/tempo-config.yaml`,
`loki-config.yaml`, `prometheus.yaml` (the image's own file names, so the mount lines read as
"this file over that file"). No "override", "custom" or "fixed" in names.

## Out of scope

- Pyroscope and OBI (not fed by our services; 137 MB in 13 days).
- Reducing the poll rate or the number of round-trips per tick — a messaging-kernel
  design question, not a logging one.
- Log volume under load (payment-path Information lines are the demo's signal).
- The Aspire dashboard's own in-memory telemetry.

## Testing and verification

- Unit tier (`dotnet test CoreBankDemo.UnitTests.slnf`): lock-service log levels (existing
  Moq logger verification pattern), the AppHost mount theory, the three config-file checks,
  the appsettings check. Coverage gate unchanged.
- Live, on the running AppHost, after both services are rebuilt and the LGTM container has
  been recreated: (a) `curl -s -G http://localhost:3000/api/datasources/proxy/uid/loki/loki/api/v1/query --data-urlencode 'query=sum(count_over_time({service_name=~"CoreBank.*"}[1h]))'`
  after one idle hour returns 0 (or only genuine business lines if payments were sent);
  (b) `sudo du -sh /var/lib/docker/volumes/corebank-lgtm-data/_data/*` two hours after the
  restart shows Tempo blocks and Loki chunks reduced to the last day;
  (c) `curl -s http://localhost:3200/status/config | grep block_retention` reports `24h`, and
  Prometheus's `/api/v1/status/flags`/runtime info reports the 1 d retention.
