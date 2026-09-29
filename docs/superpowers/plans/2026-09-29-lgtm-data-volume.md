# LGTM Data Volume — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The `corebank-lgtm-data` volume stops growing without bound: Tempo, Loki and Prometheus keep one day, and idle services no longer write ~38 million log lines a day.

**Architecture:** Three repository-owned copies of the LGTM image's component config files (`observability/lgtm/`), each the image's file plus a retention block, bind-mounted read-only over the image's files by both AppHosts — the mechanism the provisioned dashboards already use. In the services, the per-tick lock lines move to Debug and EF Core command logging to Warning. Drift tests in `CoreBankDemo.ServiceDefaults.Tests` pin the mounts, the retention values and the log level.

**Tech Stack:** .NET 10, Aspire 13.5.3 (`WithBindMount`), xUnit v3 + AwesomeAssertions + Moq, `grafana/otel-lgtm:0.33.0` (Tempo 3.0.3, Loki 3.7.7, Prometheus 3.14.0).

**Spec:** `docs/superpowers/specs/2026-09-29-lgtm-data-volume-design.md` — read it first.

## Global Constraints

- Branch: `feature/lgtm-log-size` (already cut from `origin/main` with `--no-track`; the AGENTS.md fix and this spec/plan are its first commits). Never commit to `main`. Push and open the PR only when asked.
- Build order (the `build` skill): `dotnet tool restore` once, then `dotnet test CoreBankDemo.UnitTests.slnf`. Run tests with `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1` set, so `ServiceDefaults.Tests` does not push metrics into the live collector.
- Run one test class with `dotnet test tests/CoreBankDemo.ServiceDefaults.Tests/CoreBankDemo.ServiceDefaults.Tests.csproj --filter "FullyQualifiedName~<ClassName>"` (name the `.csproj`; the folder holds a second, lowercase csproj artefact).
- TDD: write the failing test, watch it fail, then implement. Coverage ≥ 90 % per logic project (coverlet-enforced).
- Retention values are exactly: Tempo `block_retention: 24h` (both `backend_scheduler.provider.compaction.compaction` and `backend_worker.compaction`), Loki `limits_config.retention_period: 24h` with `compactor.retention_enabled: true`, `retention_delete_delay: 1h`, `delete_request_store: filesystem`, `working_directory: /data/loki/compactor`; Prometheus `storage.tsdb.retention.time: 1d`.
- The three config files are byte-identical to the image's files apart from a two-line header comment and the retention block. Container paths: `/otel-lgtm/tempo-config.yaml`, `/otel-lgtm/loki-config.yaml`, `/otel-lgtm/prometheus.yaml`. Mounted `isReadOnly: true`.
- The two `lgtm` declarations in `CoreBankDemo.AppHost/AppHost.cs` and `CoreBankDemo.LoadTests/AppHost.cs` stay equivalent (ADR-022): add the same three lines to both.
- Log-level changes are exactly: `Acquired lock …` and `Released lock …` → `LogDebug`; `Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command` = `"Warning"` in both services' `appsettings.json`. No other log line or level changes.
- Do not delete anything from the volume by hand; retention does it. Do not touch Pyroscope/OBI.
- Commit messages end with: `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`

## File Structure

| File | Responsibility |
|---|---|
| `CoreBankDemo.ServiceDefaults/RedisDistributedLockService.cs:71,111` | Acquire/release lines at Debug. |
| `tests/CoreBankDemo.ServiceDefaults.Tests/DistributedLock/RedisDistributedLockServiceTests.cs` | Two new log-level tests (existing `VerifyLogged` helper). |
| `CoreBankDemo.PaymentsAPI/appsettings.json`, `CoreBankDemo.CoreBankAPI/appsettings.json` | EF Core command category at Warning. |
| `observability/lgtm/tempo-config.yaml`, `loki-config.yaml`, `prometheus.yaml` (new) | Image config + retention. |
| `CoreBankDemo.AppHost/AppHost.cs`, `CoreBankDemo.LoadTests/AppHost.cs` | Three read-only bind mounts each. |
| `tests/CoreBankDemo.ServiceDefaults.Tests/CoreBankDemo.ServiceDefaults.Tests.csproj` | Links the three config files and the two `appsettings.json` into the test output. |
| `tests/CoreBankDemo.ServiceDefaults.Tests/Lgtm/LgtmRetentionTests.cs` (new) | Mount theory, retention-value tests, appsettings test. |
| `docs/adr/ADR-025-lgtm-one-day-retention.md` (new), `docs/adr/ADR-022-lgtm-observability-backend.md` | Decision record; ADR-022 gets an "amended by" line. |

---

### Task 1: Lock acquire/release lines at Debug

**Files:**
- Modify: `CoreBankDemo.ServiceDefaults/RedisDistributedLockService.cs:71` and `:111`
- Test: `tests/CoreBankDemo.ServiceDefaults.Tests/DistributedLock/RedisDistributedLockServiceTests.cs`

**Interfaces:** none new. Uses the file's existing `CreateSut()`, `CreateHandleMock()`, `SetupAcquire(...)` and `VerifyLogged(logger, LogLevel, string, Times)` helpers.

- [ ] **Step 1: Write the failing tests** — add after `Lock_not_acquired_is_logged_at_debug_level`:

```csharp
    [Fact]
    public async Task Lock_acquired_is_logged_at_debug_level()
    {
        // Every 200 ms poll tick of every partition acquires and releases a lock;
        // at Information the two lines were two thirds of all log volume while idle.
        var (factory, logger, sut) = CreateSut();
        var handle = CreateHandleMock();
        SetupAcquire(factory, "corebankdemo:lock:quiet", TimeSpan.FromSeconds(30), handle.Object);

        await sut.ExecuteWithLockAsync("quiet", 30, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        VerifyLogged(logger, LogLevel.Debug, "Acquired lock", Times.Once());
        VerifyLogged(logger, LogLevel.Information, "Acquired lock", Times.Never());
    }

    [Fact]
    public async Task Lock_released_is_logged_at_debug_level()
    {
        var (factory, logger, sut) = CreateSut();
        var handle = CreateHandleMock();
        SetupAcquire(factory, "corebankdemo:lock:quiet", TimeSpan.FromSeconds(30), handle.Object);

        await sut.ExecuteWithLockAsync("quiet", 30, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        VerifyLogged(logger, LogLevel.Debug, "Released lock", Times.Once());
        VerifyLogged(logger, LogLevel.Information, "Released lock", Times.Never());
    }
```

If `SetupAcquire` in this file takes different parameters than shown, copy the call shape from `Lock_acquired_workload_succeeds_returns_true_and_disposes_the_handle` — the intent is a successful acquire with a never-lost handle.

- [ ] **Step 2: Run them and watch them fail**

Run: `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1 dotnet test tests/CoreBankDemo.ServiceDefaults.Tests/CoreBankDemo.ServiceDefaults.Tests.csproj --filter "FullyQualifiedName~RedisDistributedLockServiceTests.Lock_acquired_is_logged|FullyQualifiedName~RedisDistributedLockServiceTests.Lock_released_is_logged"`
Expected: both FAIL on the `LogLevel.Debug … Times.Once()` verification (the lines are logged at Information today).

- [ ] **Step 3: Change the two log calls**

In `RedisDistributedLockService.cs`, line 71: `logger.LogInformation("Acquired lock {LockName} with {ExpirySeconds}s lease", …)` → `logger.LogDebug(…)` (same template, same arguments). Line 111: `logger.LogInformation("Released lock {LockName}", lockName)` → `logger.LogDebug(…)`. Add one comment above the first:

```csharp
            // Debug, not Information: every 200 ms poll tick of every partition acquires
            // and releases a lock, and at Information these two lines were two thirds of
            // all log volume while the system sat idle. Lost ownership stays a Warning.
```

- [ ] **Step 4: Run the whole class**

Run: `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1 dotnet test tests/CoreBankDemo.ServiceDefaults.Tests/CoreBankDemo.ServiceDefaults.Tests.csproj --filter "FullyQualifiedName~RedisDistributedLockServiceTests"`
Expected: PASS, all tests. If an existing test verified these lines at Information, change that test's expected level to Debug — the spec decides the level, not the old test.

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.ServiceDefaults/RedisDistributedLockService.cs tests/CoreBankDemo.ServiceDefaults.Tests/DistributedLock/RedisDistributedLockServiceTests.cs
git commit -m "fix(logging): lock acquire and release lines at Debug, not Information

Every 200 ms poll tick of every partition acquires and releases a lock; at
Information the two lines were 1.06 M of the 1.59 M log lines an idle hour
produced. Lost ownership stays a Warning.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: EF Core command logging at Warning, pinned by a drift test

**Files:**
- Modify: `CoreBankDemo.PaymentsAPI/appsettings.json`, `CoreBankDemo.CoreBankAPI/appsettings.json`
- Modify: `tests/CoreBankDemo.ServiceDefaults.Tests/CoreBankDemo.ServiceDefaults.Tests.csproj`
- Create: `tests/CoreBankDemo.ServiceDefaults.Tests/Lgtm/LgtmRetentionTests.cs`

**Interfaces:**
- Produces: the test class `CoreBankDemo.ServiceDefaults.Tests.Lgtm.LgtmRetentionTests` and its helper `static string Linked(params string[] parts)` (path under `AppContext.BaseDirectory`), which Tasks 3 and 4 add tests to.

- [ ] **Step 1: Link both appsettings files into the test output** — in the csproj `ItemGroup` that already links the dashboards, add:

```xml
    <!-- Idle poll ticks must not log: the EF Core command category is pinned at Warning. -->
    <Content Include="..\..\CoreBankDemo.PaymentsAPI\appsettings.json" Link="Services\CoreBankDemo.PaymentsAPI\appsettings.json" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="..\..\CoreBankDemo.CoreBankAPI\appsettings.json" Link="Services\CoreBankDemo.CoreBankAPI\appsettings.json" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 2: Write the failing test** — create `tests/CoreBankDemo.ServiceDefaults.Tests/Lgtm/LgtmRetentionTests.cs`:

```csharp
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.Lgtm;

/// <summary>
/// Drift tests for the LGTM data-volume decisions (spec: lgtm-data-volume): one-day
/// retention in the component configs both AppHosts mount over the image's, and no
/// per-tick logging from the services. Sources and configs are linked into this
/// project's output so a renamed file or a dropped setting fails the build.
/// </summary>
public class LgtmRetentionTests
{
    private static string Linked(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, .. parts]);

    [Theory]
    [InlineData("CoreBankDemo.PaymentsAPI")]
    [InlineData("CoreBankDemo.CoreBankAPI")]
    public void EF_Core_command_logging_is_at_Warning(string service)
    {
        // The claim query of every poll tick was a third of all idle log volume at
        // Information; the query text is on the postgresql span of any recorded trace.
        using var settings = JsonDocument.Parse(File.ReadAllText(Linked("Services", service, "appsettings.json")));
        var level = settings.RootElement.GetProperty("Logging").GetProperty("LogLevel")
            .GetProperty("Microsoft.EntityFrameworkCore.Database.Command").GetString();

        level.Should().Be("Warning");
    }
}
```

- [ ] **Step 3: Run it and watch it fail**

Run: `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1 dotnet test tests/CoreBankDemo.ServiceDefaults.Tests/CoreBankDemo.ServiceDefaults.Tests.csproj --filter "FullyQualifiedName~LgtmRetentionTests"`
Expected: FAIL for both services with `KeyNotFoundException` (the category is not set).

- [ ] **Step 4: Set the level in both appsettings.json** — the `Logging` block becomes, in each:

```json
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  },
```

(`appsettings.Development.json` does not set this category; leave it alone.)

- [ ] **Step 5: Run it and watch it pass**

Run: same command as Step 3. Expected: PASS ×2.

- [ ] **Step 6: Commit**

```bash
git add CoreBankDemo.PaymentsAPI/appsettings.json CoreBankDemo.CoreBankAPI/appsettings.json tests/CoreBankDemo.ServiceDefaults.Tests/CoreBankDemo.ServiceDefaults.Tests.csproj tests/CoreBankDemo.ServiceDefaults.Tests/Lgtm/LgtmRetentionTests.cs
git commit -m "fix(logging): EF Core command logging at Warning in both services

The claim query of every 200 ms poll tick was logged at Information: 0.53 M of
the 1.59 M lines an idle hour produced. Query text stays on the postgresql
span of any recorded trace. A drift test pins the level.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: The three LGTM config files with one-day retention

**Files:**
- Create: `observability/lgtm/tempo-config.yaml`, `observability/lgtm/loki-config.yaml`, `observability/lgtm/prometheus.yaml`
- Modify: `tests/CoreBankDemo.ServiceDefaults.Tests/CoreBankDemo.ServiceDefaults.Tests.csproj`
- Test: `tests/CoreBankDemo.ServiceDefaults.Tests/Lgtm/LgtmRetentionTests.cs`

**Interfaces:**
- Consumes: `LgtmRetentionTests.Linked(...)` from Task 2.
- Produces: the three files at the paths above, which Task 4 mounts.

- [ ] **Step 1: Link the config files into the test output** — add to the same csproj `ItemGroup`:

```xml
    <!-- ADR-025: the LGTM component configs both AppHosts mount over the image's own are
         checked for their one-day retention so a re-sync after an image bump cannot drop it. -->
    <Content Include="..\..\observability\lgtm\tempo-config.yaml" Link="Lgtm\tempo-config.yaml" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="..\..\observability\lgtm\loki-config.yaml" Link="Lgtm\loki-config.yaml" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="..\..\observability\lgtm\prometheus.yaml" Link="Lgtm\prometheus.yaml" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 2: Write the failing tests** — add to `LgtmRetentionTests`:

```csharp
    [Fact]
    public void Tempo_keeps_blocks_for_one_day()
    {
        // Tempo 3 has no `compactor` block; retention lives with the backend scheduler
        // (which plans the retention jobs) and the backend worker (which runs them).
        var config = File.ReadAllText(Linked("Lgtm", "tempo-config.yaml"));

        config.Should().MatchRegex(@"backend_scheduler:\s*\n\s+provider:\s*\n\s+compaction:\s*\n\s+compaction:\s*\n\s+block_retention: 24h");
        config.Should().MatchRegex(@"backend_worker:\s*\n\s+compaction:\s*\n\s+block_retention: 24h");
    }

    [Fact]
    public void Loki_deletes_chunks_older_than_one_day()
    {
        var config = File.ReadAllText(Linked("Lgtm", "loki-config.yaml"));

        config.Should().MatchRegex(@"compactor:\s*\n(?:\s+\S.*\n)*?\s+retention_enabled: true");
        config.Should().MatchRegex(@"compactor:\s*\n(?:\s+\S.*\n)*?\s+delete_request_store: filesystem");
        config.Should().MatchRegex(@"limits_config:\s*\n\s+retention_period: 24h");
    }

    [Fact]
    public void Prometheus_keeps_samples_for_one_day()
    {
        // Prometheus 3.14 deprecates --storage.tsdb.retention.time for this config field.
        var config = File.ReadAllText(Linked("Lgtm", "prometheus.yaml"));

        config.Should().MatchRegex(@"storage:\s*\n\s+tsdb:\s*\n\s+retention:\s*\n\s+time: 1d");
    }
```

- [ ] **Step 3: Run them and watch them fail**

Run: `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1 dotnet test tests/CoreBankDemo.ServiceDefaults.Tests/CoreBankDemo.ServiceDefaults.Tests.csproj --filter "FullyQualifiedName~LgtmRetentionTests"`
Expected: the three new tests FAIL with `FileNotFoundException` (the build copies nothing because the files do not exist yet; if MSBuild fails on the missing `Content` items instead, that is the same red — proceed).

- [ ] **Step 4: Create the three files from the image's own copies** — take the base text from the running container (or `docker run --rm --entrypoint cat grafana/otel-lgtm:0.33.0 /otel-lgtm/<file>` when it is not running), then add the header and the retention block:

```bash
mkdir -p observability/lgtm
for f in tempo-config.yaml loki-config.yaml prometheus.yaml; do
  docker exec corebank-lgtm cat /otel-lgtm/$f > observability/lgtm/$f
done
```

Prepend to each file (adjusting the component name):

```yaml
# grafana/otel-lgtm:0.33.0's /otel-lgtm/tempo-config.yaml, plus one-day retention (ADR-025).
# Mounted read-only over the image's file by both AppHosts; re-diff against the image on a bump.
```

Append to `tempo-config.yaml`:

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

Append to `loki-config.yaml`:

```yaml
compactor:
  working_directory: /data/loki/compactor
  retention_enabled: true
  retention_delete_delay: 1h
  delete_request_store: filesystem
limits_config:
  retention_period: 24h
```

In `prometheus.yaml`, change the existing `storage:` block to:

```yaml
storage:
  tsdb:
    retention:
      time: 1d
    # A 10min time window is enough because it can easily absorb retries and network delays.
    out_of_order_time_window: 10m
```

- [ ] **Step 5: Verify each file against the component binaries** (the container is running; these are the commands the spec's values were validated with):

```bash
docker cp observability/lgtm/tempo-config.yaml corebank-lgtm:/tmp/t.yaml && docker exec corebank-lgtm ./tempo/tempo -config.file=/tmp/t.yaml -config.verify=true && echo tempo ok
docker cp observability/lgtm/loki-config.yaml  corebank-lgtm:/tmp/l.yaml && docker exec corebank-lgtm ./loki/loki -config.file=/tmp/l.yaml -verify-config 2>&1 | grep -q "config is valid" && echo loki ok
docker cp observability/lgtm/prometheus.yaml   corebank-lgtm:/tmp/p.yaml && docker exec corebank-lgtm ./prometheus/promtool check config /tmp/p.yaml
```

Expected: `tempo ok`, `loki ok`, `SUCCESS: /tmp/p.yaml is valid prometheus config file syntax`.

- [ ] **Step 6: Run the tests and watch them pass**

Run: same command as Step 3. Expected: PASS for all `LgtmRetentionTests`.

- [ ] **Step 7: Commit**

```bash
git add observability/lgtm tests/CoreBankDemo.ServiceDefaults.Tests/CoreBankDemo.ServiceDefaults.Tests.csproj tests/CoreBankDemo.ServiceDefaults.Tests/Lgtm/LgtmRetentionTests.cs
git commit -m "feat(observability): LGTM component configs with one-day retention

Copies of grafana/otel-lgtm:0.33.0's tempo-config.yaml, loki-config.yaml and
prometheus.yaml plus a retention block each: Tempo block_retention 24h (backend
scheduler and worker; Tempo 3 has no compactor block), Loki compactor retention
with retention_period 24h, Prometheus storage.tsdb.retention.time 1d (the flag
is deprecated). Verified with tempo -config.verify, loki -verify-config and
promtool. Drift tests pin the values.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Both AppHosts mount the configs; a theory pins the mounts

**Files:**
- Modify: `CoreBankDemo.AppHost/AppHost.cs` (after the second dashboard `WithBindMount`, before `.WithVolume`), `CoreBankDemo.LoadTests/AppHost.cs` (same place)
- Test: `tests/CoreBankDemo.ServiceDefaults.Tests/Lgtm/LgtmRetentionTests.cs`

**Interfaces:**
- Consumes: the three files from Task 3; the AppHost sources already linked as `AppHosts/<project>/AppHost.cs` in the test output.

- [ ] **Step 1: Write the failing theory** — add to `LgtmRetentionTests`:

```csharp
    [Theory]
    [InlineData("CoreBankDemo.AppHost", "tempo-config.yaml")]
    [InlineData("CoreBankDemo.AppHost", "loki-config.yaml")]
    [InlineData("CoreBankDemo.AppHost", "prometheus.yaml")]
    [InlineData("CoreBankDemo.LoadTests", "tempo-config.yaml")]
    [InlineData("CoreBankDemo.LoadTests", "loki-config.yaml")]
    [InlineData("CoreBankDemo.LoadTests", "prometheus.yaml")]
    public void AppHost_mounts_the_component_config_read_only_over_the_images_file(string appHost, string file)
    {
        // ADR-022 keeps the two lgtm declarations equivalent; ADR-025 adds these mounts.
        var source = File.ReadAllText(Linked("AppHosts", appHost, "AppHost.cs"));
        var mount = new System.Text.RegularExpressions.Regex(
            @"\.WithBindMount\(\s*Path\.GetFullPath\(Path\.Combine\(builder\.AppHostDirectory, ""\.\."", ""observability"", ""lgtm"", """
            + System.Text.RegularExpressions.Regex.Escape(file)
            + @"""\)\),\s*""/otel-lgtm/" + System.Text.RegularExpressions.Regex.Escape(file) + @""",\s*isReadOnly: true\)");

        mount.IsMatch(source).Should().BeTrue($"{appHost} must mount observability/lgtm/{file} read-only at /otel-lgtm/{file}");
    }
```

- [ ] **Step 2: Run it and watch it fail**

Run: `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1 dotnet test tests/CoreBankDemo.ServiceDefaults.Tests/CoreBankDemo.ServiceDefaults.Tests.csproj --filter "FullyQualifiedName~LgtmRetentionTests.AppHost_mounts"`
Expected: FAIL ×6 ("must mount …").

- [ ] **Step 3: Add the mounts to both AppHosts** — in each `AppHost.cs`, directly after the `.WithBindMount(... "dashboards"), "/otel-lgtm/grafana/conf/provisioning/dashboards/custom", isReadOnly: true)` call and before `.WithVolume("corebank-lgtm-data", "/data")`, insert (identical text in both files):

```csharp
    // ADR-025: the image ships Tempo, Loki and Prometheus with no retention, so the data
    // volume only grew (13.7 GB in thirteen days). These are the image's own config files
    // plus one-day retention, mounted over the originals the run scripts read.
    .WithBindMount(
        Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "observability", "lgtm", "tempo-config.yaml")),
        "/otel-lgtm/tempo-config.yaml",
        isReadOnly: true)
    .WithBindMount(
        Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "observability", "lgtm", "loki-config.yaml")),
        "/otel-lgtm/loki-config.yaml",
        isReadOnly: true)
    .WithBindMount(
        Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "observability", "lgtm", "prometheus.yaml")),
        "/otel-lgtm/prometheus.yaml",
        isReadOnly: true)
```

- [ ] **Step 4: Run the theory and the whole unit tier**

Run: same command as Step 2 → PASS ×6. Then `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1 dotnet test CoreBankDemo.UnitTests.slnf` → all projects `Passed!`, no coverage error, no new warnings. Also `dotnet build CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj` → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.AppHost/AppHost.cs CoreBankDemo.LoadTests/AppHost.cs tests/CoreBankDemo.ServiceDefaults.Tests/Lgtm/LgtmRetentionTests.cs
git commit -m "feat(apphost): both AppHosts mount the one-day-retention LGTM configs

Read-only over the image's tempo-config.yaml, loki-config.yaml and
prometheus.yaml, next to the dashboard mounts; a theory over both AppHost
sources pins the six mounts (ADR-022 equivalence).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: ADR-025 and the ADR-022 amendment

**Files:**
- Create: `docs/adr/ADR-025-lgtm-one-day-retention.md`
- Modify: `docs/adr/ADR-022-lgtm-observability-backend.md` (status block)

- [ ] **Step 1: Write ADR-025** — follow the header style of ADR-022 (`# ADR-025: …`, `**Date:**`, `**Status:** Accepted`, `**Deciders:**`, `**Amends:** ADR-022`), then:

```markdown
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
```

- [ ] **Step 2: Amend ADR-022** — under its `**Status:** Accepted` line add `**Amended by:** ADR-025 (retention: one day; component configs mounted from `observability/lgtm/`)`.

- [ ] **Step 3: Commit**

```bash
git add docs/adr/ADR-025-lgtm-one-day-retention.md docs/adr/ADR-022-lgtm-observability-backend.md
git commit -m "docs(adr): ADR-025 LGTM keeps one day; amends ADR-022

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: Live verification on the running AppHost

No code. Confirms the spec's "Testing and verification" section; results go in the PR description.

- [ ] **Step 1: Recreate the LGTM container with the new mounts.** Stop and start the regular AppHost (`aspire stop --apphost CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj --non-interactive`, then `aspire start … --non-interactive`, then `aspire wait payments-api --non-interactive`). Check the container was recreated: `docker inspect corebank-lgtm --format '{{.Created}}'` is after the start, and `docker inspect corebank-lgtm --format '{{json .Mounts}}' | grep -c otel-lgtm/` prints `5`. If it was **not** recreated, stop, report to the user, and only with their go-ahead run `docker rm -f corebank-lgtm` (the volume survives) and start again.

- [ ] **Step 2: Confirm the components run the mounted configs.**

```bash
curl -s "http://localhost:3200/status/config" | grep -E "block_retention" | sort -u        # expect 24h0m0s
curl -s "http://localhost:3000/api/datasources/proxy/uid/loki/config" | grep -E "retention_period|retention_enabled"   # 24h, true
curl -s "http://localhost:9090/api/v1/status/runtimeinfo" 2>/dev/null || curl -s "http://localhost:3000/api/datasources/proxy/uid/prometheus/api/v1/status/runtimeinfo" | grep -o '"storageRetention":"[^"]*"'   # 1d
```

- [ ] **Step 3: Confirm idle silence.** Wait one hour with no payments, then:

```bash
curl -s -G "http://localhost:3000/api/datasources/proxy/uid/loki/loki/api/v1/query" --data-urlencode 'query=sum(count_over_time({service_name=~"CoreBank.*"}[1h]))'
```

Expected: no result or a value in the low hundreds (startup and health lines), not ~1.6 M.

- [ ] **Step 4: Confirm the backlog drains.** Two hours after Step 1:

```bash
sudo du -sh /var/lib/docker/volumes/corebank-lgtm-data/_data/*
```

Expected: `tempo` and `loki` down to the last day's data (hundreds of MB at most), `prometheus` down to ≤ 2 days of blocks (its compaction removes the rest over the next cycles).

- [ ] **Step 5: Record the numbers** — paste the Step 2–4 outputs into the PR description under "Verification".
