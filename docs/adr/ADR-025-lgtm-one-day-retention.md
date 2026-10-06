# ADR-025: LGTM keeps one day, and idle services stop writing logs

**Date:** 2026-09-29
**Status:** Accepted
**Deciders:** Architecture team
**Amends:** ADR-022 (telemetry history now carries across both AppHosts for one day; the
component configs are mounted from `observability/lgtm/`)

## Context

After thirteen days of local development the `corebank-lgtm-data` volume held 13.66 GB:
Tempo 10 GB, Loki 1.9 GB, Prometheus 0.8 GB. `grafana/otel-lgtm:0.33.0` ships the three
stores with no retention (Tempo defaults to 14 days, Prometheus to 15, Loki to none), and
the services wrote ~38 million Information lines a day while idle — the lock acquire/release
lines and EF Core's command log of every 200 ms poll tick.

## Decision

- Tempo, Loki and Prometheus keep **one day**: `block_retention: 24h` (Tempo 3's
  `backend_scheduler`/`backend_worker`), Loki compactor retention with
  `retention_period: 24h`, Prometheus `storage.tsdb.retention.time: 1d`.
- The retention lives in repository-owned copies of the image's config files
  (`observability/lgtm/`), bind-mounted read-only over the originals by both AppHosts,
  the way the provisioned dashboards are. The copies track image tag `0.33.0`; an image
  bump re-diffs them.
- `RedisDistributedLockService` logs acquire and release at Debug; both services set
  `Microsoft.EntityFrameworkCore.Database.Command` to Warning.
- Drift tests in `CoreBankDemo.ServiceDefaults.Tests` pin the mounts, the retention values
  and the log level.

## Consequences

- ADR-022's "telemetry history carries across both AppHosts" holds for one day. A rehearsal
  older than that is gone; that is the trade the demo makes for a bounded volume.
- Retention deletes the backlog on its own after the container is recreated with the new
  mounts (Aspire recreates a persistent container on a configuration change); no manual
  deletion.
- Lock diagnostics are still available by raising `CoreBankDemo.ServiceDefaults` to Debug.
