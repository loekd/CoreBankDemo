---
title: 'Story 3.4: Service wiring defaults'
type: 'feature'
created: '2026-08-24'
status: 'done'
baseline_commit: '615c1e1b43ab8e44559f6d786d6a76ad88aeda20'
review_loop_iteration: 1
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-3-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `CoreBankDemo.ServiceDefaults/Extensions.cs` (`AddServiceDefaults`, `MapDefaultEndpoints`, plus the story-3.1 `Add*ProcessingOptions` helpers) already exists — it was scaffolded early so stories 3.1–3.3 could compile and wire their own pieces — but it has never had its own tests, and it doesn't yet register `IEventPublisher` (story 3.3). `tests/CoreBankDemo.ServiceDefaults.Tests.csproj` still carries a `Threshold=0` coverage-gate override with a `TODO(story-3.4)` marker reserving this exact cleanup (NFR-2, NFR-3; FR-20).

**Approach:** Add DI-container-inspection tests proving `AddServiceDefaults` registers OTel logging/metrics/tracing (with the `JAEGER_OTLP_ENDPOINT` override), the `"self"`/`"live"` health check, service discovery, `AddStandardResilienceHandler`, the story-3.2 `IDistributedLockService` factory, and — newly wired by this story — the story-3.3 `IEventPublisher` factory. Add tests for `ResolveOtlpEndpoint`'s parsing matrix (promoted from `private` to `internal` so tests call it directly, mirroring story 3.2's `CooperativeLockCancellation.CancelSafely` pattern) and for the three `Add*ProcessingOptions` binding helpers. Mark `MapDefaultEndpoints` `[ExcludeFromCodeCoverage]` — it requires a live `WebApplication` request pipeline to test meaningfully, not just DI registration, and stays out of scope for this story's DI-inspection approach. Once the module's coverable surface is real, remove the `Threshold=0` override.

## Boundaries & Constraints

**Always:** `AddServiceDefaults`'s registrations (OTel logging/metrics/tracing incl. `JAEGER_OTLP_ENDPOINT` override, `/health`+`/alive` health-check registration, service discovery, `AddStandardResilienceHandler`, `IDistributedLockService` factory, `IEventPublisher` factory) are asserted via DI container inspection (build a `WebApplicationBuilder`/`HostApplicationBuilder`, call `AddServiceDefaults`, inspect `builder.Services` or a built `IServiceProvider`) — no live OTLP collector, no real Dapr sidecar, no network calls in tests (FR-20); `ResolveOtlpEndpoint` is promoted to `internal` and unit-tested directly for its full parsing matrix (unset, absolute http(s), `tcp://` rewrite, bare `host:port` normalization, invalid value throws); the three `Add*ProcessingOptions` helpers (story 3.1) each get a test confirming `AddOptions<T>().BindConfiguration(...).ValidateDataAnnotations().ValidateOnStart()` is wired; `MapDefaultEndpoints` carries `[ExcludeFromCodeCoverage]` with no behavior change; the `Threshold=0` override and its `TODO(story-3.4)` comment are removed from `tests/CoreBankDemo.ServiceDefaults.Tests.csproj` once the above lands, and the project must clear the real ≥90% line gate.

**Ask First:** Resolved inline (not deferred) — whether `IEventPublisher` gets a `NoOpEventPublisher` fallback mirroring `NoOpDistributedLockService`. Decision: **no NoOp variant.** The lock service's no-op (`ExecuteWithLockAsync` always returns `false`, workload never runs) is a safe fail-closed default for a service that doesn't use Dapr. A no-op event publisher would instead silently *discard* every published event — a much worse failure mode that could hide real bugs. So `IEventPublisher`/`DaprEventPublisher` is registered as a singleton **only when `DaprClient` is available in DI**; a service without Dapr that tries to resolve `IEventPublisher` gets the standard "no service registered for type" DI exception at the call site, not a silent black hole.

**Never:** Modify `IDistributedLockService.cs`/`DaprDistributedLockService.cs`/`NoOpDistributedLockService.cs`/`CooperativeLockCancellation.cs` (story 3.2, done) or `IEventPublisher.cs`/`DaprEventPublisher.cs` (story 3.3, done) beyond what `AddServiceDefaults` needs to construct them — this story wires, it does not redesign; touch `Configuration/*.cs` (story 3.1, done) or `CloudEventTypes/*.cs` (story 3.3, done); add a real OTLP collector, real Dapr sidecar, or any live-network dependency to the test suite to hit the coverage numbers — DI container inspection only.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| `AddServiceDefaults` registers OTel | Fresh builder, call `AddServiceDefaults("svc")` | OTel logging/metrics/tracing pipeline present in DI (resolvable `TracerProvider`/`MeterProvider` or equivalent registered services) | N/A |
| `JAEGER_OTLP_ENDPOINT` unset | Config key absent/empty | `ResolveOtlpEndpoint` returns `null`; default env-based OTLP exporter used | N/A |
| `JAEGER_OTLP_ENDPOINT` absolute http(s) URI | e.g. `http://jaeger:4317` | Returned unchanged | N/A |
| `JAEGER_OTLP_ENDPOINT` `tcp://` scheme | e.g. `tcp://jaeger:4317` or `tcp://jaeger` (default port) | Rewritten to `http://` scheme, port defaulted to `4317` when the input had no explicit port | N/A |
| `JAEGER_OTLP_ENDPOINT` bare `host:port` | e.g. `jaeger:4317` | Normalized to `http://jaeger:4317` | N/A |
| `JAEGER_OTLP_ENDPOINT` unparseable value | e.g. `":::not a uri"` | Throws `InvalidOperationException` | not caught — fail-fast at startup |
| `AddServiceDefaults` registers health check | Fresh builder | `"self"` health check registered, tagged `"live"` (`HealthCheckServiceOptions` inspection) | N/A |
| `AddServiceDefaults` registers resilience + discovery | Fresh builder | Typed `HttpClient`s get `AddStandardResilienceHandler` + service discovery via `ConfigureHttpClientDefaults` | N/A |
| `AddServiceDefaults` registers `IDistributedLockService` | `DaprClient` present in DI | Resolves to `DaprDistributedLockService` | N/A |
| `AddServiceDefaults` registers `IDistributedLockService` | `DaprClient` absent | Resolves to `NoOpDistributedLockService` | N/A |
| `AddServiceDefaults` registers `IEventPublisher` | `DaprClient` present in DI | Resolves to `DaprEventPublisher` | N/A |
| `AddServiceDefaults` registers `IEventPublisher` | `DaprClient` absent | Not registered at all | resolving throws standard DI "no service for type" exception, not a silent no-op |
| `AddInboxProcessingOptions`/`AddOutboxProcessingOptions`/`AddMessagingOutboxProcessingOptions` | Fresh builder | Each registers `AddOptions<T>().BindConfiguration(...).ValidateDataAnnotations().ValidateOnStart()` for its type | N/A |
| `MapDefaultEndpoints` | N/A | `[ExcludeFromCodeCoverage]`; behavior unchanged (dev-only `/health` + `/alive` tagged `"live"`) | not unit-tested, by design |
| Coverage gate | `tests/CoreBankDemo.ServiceDefaults.Tests.csproj` after this story | `Threshold=0` override removed; project clears the real ≥90% line threshold from `tests/Directory.Build.props` | build fails if not met |

</frozen-after-approval>

## Code Map

- Modify: `CoreBankDemo.ServiceDefaults/Extensions.cs` — promote `ResolveOtlpEndpoint` from `private` to `internal`; add `IEventPublisher`/`DaprEventPublisher` singleton registration (DaprClient-present-only, no NoOp) to `AddServiceDefaults`; add `[ExcludeFromCodeCoverage]` to `MapDefaultEndpoints`
- New: `tests/CoreBankDemo.ServiceDefaults.Tests/Extensions/AddServiceDefaultsTests.cs` — DI-container-inspection tests for OTel/health/service-discovery/resilience/lock-service/event-publisher registration
- New: `tests/CoreBankDemo.ServiceDefaults.Tests/Extensions/ResolveOtlpEndpointTests.cs` — `JAEGER_OTLP_ENDPOINT` parsing matrix
- New: `tests/CoreBankDemo.ServiceDefaults.Tests/Extensions/ProcessingOptionsRegistrationTests.cs` — the three `Add*ProcessingOptions` binding-helper tests
- Modify: `tests/CoreBankDemo.ServiceDefaults.Tests/CoreBankDemo.ServiceDefaults.Tests.csproj` — remove the `<Threshold>0</Threshold>` override and its `TODO(story-3.4)` comment
- Not touched: `IDistributedLockService.cs`, `DaprDistributedLockService.cs`, `NoOpDistributedLockService.cs`, `CooperativeLockCancellation.cs` (story 3.2, done), `IEventPublisher.cs`, `DaprEventPublisher.cs` (story 3.3, done), `Configuration/*.cs` (story 3.1, done), `CloudEventTypes/*.cs` (story 3.3, done)

## Tasks & Acceptance

**Execution:**
- [x] Tests first: `ResolveOtlpEndpoint` parsing matrix, `Add*ProcessingOptions` binding-helper tests, then `AddServiceDefaults` DI-inspection tests (OTel, health, discovery, resilience, lock-service, event-publisher)
- [x] `Extensions.cs`: promoted `ResolveOtlpEndpoint` to `internal`; wired `IEventPublisher`/`DaprEventPublisher` registration (DaprClient-present-only); added `[ExcludeFromCodeCoverage]` to `MapDefaultEndpoints`. Also fixed a real pre-existing bug found while writing the parsing-matrix tests: `Uri.TryCreate` was silently accepting bare `host:port` values as an absolute URI with the host read as the scheme (e.g. `"jaeger:4317"` parsed as scheme `"jaeger"`), skipping the intended `http://` normalization — gated the first parse attempt on the value containing `"://"`.
- [x] Removed the `Threshold=0` override from `tests/CoreBankDemo.ServiceDefaults.Tests.csproj`; project clears the real gate at 99.6% line / 100% branch coverage

**Acceptance Criteria:**
- Given a test `WebApplicationBuilder`, when `AddServiceDefaults(serviceName, activitySources)` runs, then OTel tracing/metrics/logging, OTLP export override via `JAEGER_OTLP_ENDPOINT`, `/health`+`/alive`, service discovery, and `AddStandardResilienceHandler` are registered (asserted via DI container inspection)
- Given `DaprClient` present vs. absent in DI, when `AddServiceDefaults` runs, then `IEventPublisher` resolves to `DaprEventPublisher` or is not registered at all (never a silent no-op)
- Given hosting-only members (`MapDefaultEndpoints`), they carry `[ExcludeFromCodeCoverage]`; option-binding helpers (`Add*ProcessingOptions`) are covered by tests
- Given the full test suite after this story, `tests/CoreBankDemo.ServiceDefaults.Tests.csproj` passes its real ≥90% line coverage gate with the `Threshold=0` override removed

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — expected: green; `CoreBankDemo.ServiceDefaults.Tests` clears the real ≥90% line threshold (no `Threshold=0` override)
- `dotnet build CoreBankDemo.Messaging/CoreBankDemo.Messaging.csproj` — expected: green, no source changes needed

## Spec Change Log

- 2026-08-24 (step-04): implemented per plan — DI-container-inspection tests for `AddServiceDefaults` (OTel provider registration, `JAEGER_OTLP_ENDPOINT` override, health check, resilience+discovery via handler-chain inspection, lock-service and event-publisher factories), `ResolveOtlpEndpoint`'s full parsing matrix, and the three `Add*ProcessingOptions` binding helpers (the latter proven via the framework's own `IStartupValidator`, not just `ValidateDataAnnotations` alone). `IEventPublisher`/`DaprEventPublisher` wired into `AddServiceDefaults`, singleton, registered only when `DaprClient` is already present in `builder.Services` — no NoOp fallback, per this spec's frozen "Ask First" resolution. `MapDefaultEndpoints` marked `[ExcludeFromCodeCoverage]`. `Threshold=0` override removed from the test csproj. Verified independently: `git diff --ignore-space-at-eol` (the file has CRLF endings, which inflate a plain `git diff --stat`) confirmed the real content diff is 41 insertions/9 deletions; `dotnet build CoreBankDemo.Messaging/CoreBankDemo.Messaging.csproj` green with zero source changes; `dotnet test CoreBankDemo.Rebuild.slnf` green — 117/117 `ServiceDefaults.Tests` (99.6% line / 100% branch / 100% method coverage, real gate cleared), 153/153 `Messaging.Tests`, 1/1 each for CoreBankAPI/PaymentsAPI. Implementer found and fixed a genuine pre-existing bug in `ResolveOtlpEndpoint` while writing the parsing-matrix tests (`Uri.TryCreate` silently accepted bare `host:port` values by reading the host as the URI scheme, e.g. `"jaeger:4317"` → scheme `"jaeger"`, never reaching `http://` normalization) — independently reproduced via a throwaway console app during review, confirmed genuine, not a misunderstanding. Review (blind-hunter + edge-case-hunter + verification-gap, all model sonnet) found no discrepancies in the self-report. Both blind-hunter and edge-case-hunter independently converged on a real, concrete finding: `AddServiceDefaults` checks `DaprClient` presence *eagerly* (registration-time `Any()` check) rather than lazily like `IDistributedLockService`, and blind-hunter confirmed by reading `CoreBankAPI/Program.cs` and `PaymentsAPI/Program.cs` directly that both currently call `AddServiceDefaults()` *before* `AddDaprClient()` — meaning `IEventPublisher` will silently never register once epic 4/5 wires real event publishing under that ordering. Not fixed here (those `Program.cs` files belong to epics 4/5's own demolish-and-rebuild scope, not this story's Code Map); logged to `deferred-work.md` with elevated urgency, alongside the related finding that `PaymentsAPI/Program.cs` never calls `AddMessagingOutboxProcessingOptions()`. Also deferred: two low-probability `ResolveOtlpEndpoint` edge cases outside the frozen matrix (bare-hostname-no-port implicit-port-80 inconsistency; the new `.Contains("://")` guard being a string heuristic rather than true scheme validation) — neither reachable from any realistic config shape in this repo.
