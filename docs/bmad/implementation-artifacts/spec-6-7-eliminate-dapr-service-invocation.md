---
title: 'Story 6.7: Eliminate Dapr service invocation'
type: 'refactor'
created: '2026-08-29'
status: 'review'
baseline_commit: '312ce8e1b6aa81269fd07c46dfdc09566d11595b'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/constraints.md'
  - '{project-root}/docs/bmad/planning-artifacts/architecture/architecture-CoreBankDemo-2026-08-21/ARCHITECTURE-SPINE.md'
  - '{project-root}/docs/adr/ADR-008-single-http-corebank-integration.md'
  - '{project-root}/docs/adr/ADR-013-checked-in-openapi-build-time-kiota.md'
  - '{project-root}/docs/bmad/implementation-artifacts/spec-5-3-contract-generated-kiota-corebank-client.md'
  - '{project-root}/docs/bmad/implementation-artifacts/epic-6-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Story 5.3 already implemented the contract-generated Kiota client as the sole PaymentsAPI-to-CoreBankAPI adapter, and ADR-008 already forbids Dapr service invocation. The repository nevertheless retains live AppHost `Features__UseDapr` overrides, a misleading proxy comment, stale current-facing guidance, and no automated rule preventing invocation APIs or an alternate client from returning. The Dapr .NET SDK now marks the `InvokeMethodAsync` family obsolete and recommends a native HTTP or gRPC client, turning those remnants into an upgrade and maintenance hazard.

**Approach:** Complete and enforce the existing decision rather than creating another transport. Remove every executable/configuration remnant of Dapr request/response invocation; retain the checked-in OpenAPI→Kiota→`ICoreBankApiClient` route as the only production banking API-to-API request/response path; add a focused architecture guard; and preserve Dapr only for CloudEvent publication/subscription.

## Existing-Story Audit

This story was created only after checking whether the work was already owned:

- Story 5.3 is `done` and already created `KiotaCoreBankApiClient`, removed alternative production clients from PaymentsAPI, and forbids `Features:UseDapr` and a Dapr CoreBank client.
- ADR-008 already accepts a single HTTP integration, says Dapr is pub/sub-only, and explicitly requires removal of `Features__UseDapr` from `CoreBankDemo.AppHost/AppHost.cs`.
- ADR-013 already establishes checked-in OpenAPI plus build-time Kiota generation.
- Current production source contains no `InvokeMethodAsync`, `CreateInvokeMethodRequest`, `CreateInvokeHttpClient`, or Dapr invocation handler.
- `CoreBankDemo.AppHost/AppHost.cs` still sets `Features__UseDapr` to both `false` and `true`; `deferred-work.md` records this as an unresolved ADR-008 violation.

Therefore Story 6.7 owns completion, cleanup, and regression prevention. It must not rewrite or duplicate Story 5.3's client.

## Boundaries & Constraints

**Always:** Route PaymentsAPI→CoreBankAPI request/response operations through `ICoreBankApiClient` backed by the generated Kiota client; resolve the logical Aspire `http://corebank-api` endpoint through the standard service-discovery/resilience pipeline; retain caller-cancellation, trace propagation, and transport-outcome behavior from Stories 5.3/5.4; remove dead invocation flags from both normal and DevProxy orchestration; keep Dapr pub/sub behavior and tests green; use an automated guard to prevent reintroduction.

**Ask First:** Replacing Dapr pub/sub; changing the checked-in CoreBank HTTP contract; adding another production request/response adapter; changing AD-11 retry/outcome semantics; removing a Dapr package that is still used for `PublishEventAsync`, subscription delivery, CloudEvent middleware, or sidecar hosting; changing DevProxy routing behavior beyond removal of the dead switch.

**Never:** Call a Dapr service-invocation SDK API; construct `/v1.0/invoke/{appId}/method/...`; add a `dapr-app-id` request header; send banking API calls through localhost Dapr HTTP/gRPC ports; add `Features:UseDapr`/`Features__UseDapr`; introduce a Dapr invocation handler/client, hand-written CoreBank HTTP client, or transport fallback; generate Kiota sources into the working tree; replace the event pub/sub hop with Kiota under this story.

## Scope Definition

"Kiota only" means every production request/response integration from one banking API to another is represented by checked-in OpenAPI and executed through its generated Kiota client behind an application-owned port. It does not mean that external demo/test clients become Kiota clients: `.http` files, k6, LoadTestSupport, health probes, and DemoRunner are harness/user clients rather than production API-to-API adapters.

The following Dapr uses remain explicitly allowed:

- CoreBankAPI publishing CloudEvents through `DaprClient.PublishEventAsync` behind `IEventPublisher`.
- PaymentsAPI receiving subscribed CloudEvents through Dapr ASP.NET integration.
- Dapr sidecars, app ids, pubsub components/subscriptions, and their telemetry/configuration.
- Dapr-focused tests and documentation that describe the retained pub/sub boundary or past decisions accurately.

The following are forbidden in executable code, live configuration, scripts, and current developer guidance:

- `InvokeMethodAsync`, `InvokeMethodWithResponseAsync`, `CreateInvokeMethodRequest`, `CreateInvokeHttpClient`, `DaprInvokeHandler`, or equivalent Dapr request/response APIs.
- `/v1.0/invoke/`, `dapr-app-id`, or direct request/response traffic to the Dapr sidecar ports.
- `Features:UseDapr`, `Features__UseDapr`, `DaprCoreBankApiClient`, or any runtime transport selector/fallback.
- A second production CoreBank client beside `KiotaCoreBankApiClient`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Result | Forbidden Result |
|---|---|---|---|
| Normal local run | DevProxy disabled | PaymentsAPI uses the same Kiota client through logical `corebank-api` | No `Features__UseDapr=true` or sidecar invocation |
| Fault-injection run | DevProxy enabled | The same Kiota HTTP path is proxied; transport behavior remains observable | No transport switch or alternate client |
| Replicated CoreBankAPI | Two local replicas behind Aspire ingress | Kiota resolves the logical service endpoint, never a replica/sidecar address | No pinned replica or localhost Dapr port |
| CoreBankAPI unavailable | Kiota request fails/times out | Existing AD-11 retry-shaped outcome is preserved | No fallback to Dapr invocation |
| Caller cancellation | Work token is cancelled | Cancellation propagates per Story 5.3 | No retry/fallback invocation |
| Event publication | CoreBank outbox delivers a CloudEvent | Existing Dapr `PublishEventAsync` path succeeds unchanged | No attempt to send the event through Kiota |
| Event subscription | PaymentsAPI receives a duplicate/new CloudEvent | Story 5.5's Dapr subscription and dedupe behavior is unchanged | No removal of required ASP.NET Dapr wiring |
| DevProxy environment | Proxy variables and local exclusions are inspected | Only still-required proxy/local-infrastructure settings remain, with accurate comments | No service-invocation rationale remains |
| Package audit | Dapr references are enumerated per project | Every remaining reference has a concrete pub/sub or sidecar owner | No invocation-only package/reference remains |
| Source/config guard | Forbidden token/path/client is introduced | Focused architecture test fails with the offending file/token | No allow-list for executable invocation code |
| Documentation audit | Current guidance is searched | Current diagrams/text show Kiota request/response and Dapr pub/sub | Historical statements are not presented as current behavior |
| Full demo smoke | Payment completes end to end | Kiota command hop and Dapr event hop both remain visible in one trace | No change to public responses or durable semantics |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.AppHost/AppHost.cs` -- remove both `Features__UseDapr` environment assignments and simplify the conditional so DevProxy changes proxy routing only; reassess the `NO_PROXY` value but retain it where required for local pub/sub/telemetry and replace its obsolete service-invocation comment.
- `CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs`, `Outbox/ICoreBankApiClient.cs`, `Outbox/KiotaCoreBankApiClient.cs`, and `Outbox/HttpForwardOutboxDeliveryStrategy.cs` -- preserve the sole generated-client route and prove there is no fallback or alternate adapter.
- `CoreBankDemo.CoreBankAPI/OpenApi/corebank-api.json`, `.config/dotnet-tools.json`, and `CoreBankDemo.PaymentsAPI/CoreBankDemo.PaymentsAPI.csproj` -- retain the checked-in contract, pinned generator, intermediate generated output, and Kiota runtime; remove only dependencies proven invocation-only.
- `CoreBankDemo.CoreBankAPI/Program.cs`, `CoreBankDemo.ServiceDefaults/DaprEventPublisher.cs`, PaymentsAPI subscription endpoints, Dapr component files, and relevant project references -- retained pub/sub path; use these files to justify each remaining Dapr dependency.
- `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankClientRegistrationTests.cs` and `CoreBankApiClientTests.cs` -- continue proving DI composition, logical endpoint, trace headers, cancellation, and response classification through Kiota.
- `tests/CoreBankDemo.PaymentsAPI.Tests/NoDaprServiceInvocationArchitectureTests.cs` (new or equivalent focused guard) -- inspect production source/config/AppHosts for the forbidden API, route, header, flag, and alternate-client vocabulary while excluding generated/build output and explicitly historical documents.
- `tests/CoreBankDemo.CoreBankAPI.Tests`, `tests/CoreBankDemo.ServiceDefaults.Tests`, and PaymentsAPI subscription tests -- retain focused Dapr pub/sub regression coverage so "no service invocation" cannot be misimplemented as "remove Dapr entirely."
- `CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs`, `CoreBankDemo.LoadTests/README.md`, `.claude/skills/conventions/SKILL.md`, active BMAD contexts/planning, and current README guidance -- replace stale claims that payment commands are forwarded via Dapr; distinguish the Kiota command hop from the Dapr event hop.
- `docs/bmad/implementation-artifacts/deferred-work.md` -- resolve/remove the ADR-008 AppHost cleanup item only after the implementation and guard are green.
- `ARCHITECTURE.md`, accepted ADRs, and frozen completed specs -- retain explicitly labelled historical decision context until Story 8.1 regenerates the brownfield snapshot; never edit frozen Story 5.3 merely to make a zero-text-match search pass.

## Tasks & Acceptance

**Execution:**
- [x] Inventory every Dapr reference and classify it as retained pub/sub/sidecar behavior, forbidden request/response invocation, stale current guidance, or explicitly historical record; attach the inventory to the story record. Inventory (via `rg -i dapr` across `CoreBankDemo.*`/`tests`/`docs`, excluding `bin`/`obj`): **retained pub/sub/sidecar** — `CoreBankDemo.ServiceDefaults/DaprEventPublisher.cs` (`IEventPublisher`/`DaprClient.PublishEventAsync`), `CoreBankDemo.CoreBankAPI/Outbox/DaprOutboxDeliveryStrategy.cs`, `CoreBankDemo.PaymentsAPI/Program.cs`'s `app.UseCloudEvents()` + `TransactionEventsController`, both APIs' `WithDaprSidecar`/`AddDaprPubSub` in `CoreBankDemo.AppHost/AppHost.cs`, `dapr/components*/*.yaml`, and the Dapr pub/sub tests under `tests/CoreBankDemo.CoreBankAPI.Tests`, `tests/CoreBankDemo.ServiceDefaults.Tests`, and `tests/CoreBankDemo.PaymentsAPI.Tests`. **Forbidden request/response invocation (removed by this story)** — `CoreBankDemo.AppHost/AppHost.cs`'s two `Features__UseDapr` environment overrides (the only executable/config remnants found; no `InvokeMethodAsync`/`CreateInvokeHttpClient`/`DaprInvokeHandler`/`/v1.0/invoke/`/`dapr-app-id` existed anywhere). **Stale current guidance (updated by this story)** — `.claude/skills/conventions/SKILL.md`'s `Features:UseDapr`/`DaprCoreBankApiClient`/`HttpCoreBankApiClient` feature-flag doc, `CoreBankDemo.LoadTests/README.md`'s "uses HTTP (not Dapr)" phrasing, and `CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs`'s "forwarding to CoreBank via Dapr" tool description. **Explicitly historical record (left untouched)** — ADR-008/ADR-013, `docs/bmad/constraints.md` ruling A1, the architecture spine's AD-6, `docs/bmad/planning-artifacts/epics.md`, spec-5-3/spec-3-2/spec-3-3/spec-3-4, `deferred-work.md`'s other Dapr-related entries (locking, pub/sub validation gaps — unrelated to this story), and `ARCHITECTURE.md`'s labelled brownfield snapshot.
- [x] Remove `Features__UseDapr` from both AppHost branches and simplify normal/DevProxy orchestration without changing the single Kiota client registration or DevProxy's ability to intercept the real command path. [`AppHost.cs`](../../../CoreBankDemo.AppHost/AppHost.cs): both `WithEnvironment("Features__UseDapr", ...)` calls removed; `.WithReference(coreBankApi)` hoisted unconditionally above the `if (devProxy is not null)` branch, which now only adds proxy env vars/`WaitFor(devProxy)` — no transport selection remains in either branch.
- [x] Remove any invocation-only configuration, environment binding, sidecar URL/header, package/reference, helper, test fixture, script, or comment found by the inventory; keep each Dapr dependency that has a proven pub/sub owner. The two `Features__UseDapr` assignments and the misleading "Dapr sidecar circumvents proxy" comment were the only invocation-only remnants; `ICoreBankApiClient.cs`'s doc comment was reworded to drop the literal (dead) flag name it referenced. No package/reference/test fixture was invocation-only — every remaining `Dapr.Client`/`Dapr.AspNetCore` use maps to `PublishEventAsync`, subscription delivery, or sidecar hosting (unchanged).
- [x] Add a focused architecture guard for all forbidden APIs/routes/headers/flags/client names across production source, live configuration, scripts, and AppHosts; do not scan build output or fail on clearly historical ADR/spec context. [`NoDaprServiceInvocationArchitectureTests.cs`](../../../tests/CoreBankDemo.PaymentsAPI.Tests/NoDaprServiceInvocationArchitectureTests.cs) scans every `CoreBankDemo.*` project directory plus `tests/`/`scripts/` (excluding `bin`/`obj`, mirroring this spec's own verification command's scope) for the exact forbidden-signal regexes, excluding `docs/` (historical/current-guidance record, audited separately, not token-matched).
- [x] Strengthen composition tests so `ICoreBankApiClient` resolves only to `KiotaCoreBankApiClient`, uses the logical service-discovery client, and has no alternate production registration. `CoreBankClientRegistrationTests.AddCoreBankApiClient_resolves_kiota_backed_client` (pre-existing, story 5.3) already asserts this exactly; confirmed still green and that `AddCoreBankApiClient` is the sole registration path (`grep`-confirmed no second `services.AddScoped<ICoreBankApiClient, ...>` anywhere).
- [x] Run Kiota adapter/forwarder tests for success, non-2xx, malformed response, timeout, exception, trace propagation, and cancellation; prove no outcome falls back to Dapr. `CoreBankApiClientTests` and `HttpForwardOutboxDeliveryStrategyTests` (pre-existing, stories 5.3/5.4) pass unchanged (146/146 in `CoreBankDemo.PaymentsAPI.Tests`); no Dapr client/type appears anywhere in their outcome paths.
- [x] Run Dapr event publisher/subscription tests and an end-to-end smoke to prove pub/sub remains intact and one trace spans the Kiota command hop plus Dapr event hop. `dotnet test` green for `CoreBankDemo.CoreBankAPI.Tests` (113/113) and `CoreBankDemo.ServiceDefaults.Tests` (107/108, 1 real-Redis integration test skipped as designed). Live smoke via `aspire start --apphost CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj` (DevProxy disabled — the `devproxy` binary is unavailable in this sandbox, a pre-existing environment limitation, not a story regression) then `POST /api/payments` (idempotency key `story-6-7-smoke-1788083716`) returned `202`, and `GET /api/transactions/{id}` on CoreBankAPI returned `Completed`. Jaeger trace `4540e809c56f0bcea9ec26c3a3371985` (tagged with the same idempotency key) contains, in one trace: `CoreBank.PaymentsAPI POST api/Payments` → `ProcessOutboxMessage` → `CoreBank.CoreBankAPI POST api/Accounts/validate` → `POST api/Transactions/process` (the Kiota HTTP hop) → `CoreBankAPI ProcessOutboxMessage` → `dapr.proto.runtime.v1.Dapr/PublishEvent` (CoreBank's `DaprClient.PublishEventAsync`) → `payments-api-dapr-cli pubsub/transaction-events` → `PaymentsAPI POST events/transactions/completed`/`balance-updated` → `ProcessInboxMessage` (the Dapr event hop) — proving both hops share one trace end to end.
- [x] Update current guidance, diagrams, test-tool descriptions, and the conventions skill; resolve the deferred-work item after verification while preserving frozen completed stories and accepted ADR history. Updated `.claude/skills/conventions/SKILL.md`, `CoreBankDemo.LoadTests/README.md`, and `CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs` per the inventory above; removed the resolved `spec-6-2` entry from `deferred-work.md` after the guard and full rebuild gate went green. No frozen story or accepted ADR was edited.
- [x] Run the focused projects and full rebuild gate with warnings treated according to the repository's existing policy. `dotnet test` green for `CoreBankDemo.PaymentsAPI.Tests` (146/146), `CoreBankDemo.CoreBankAPI.Tests` (113/113), `CoreBankDemo.ServiceDefaults.Tests` (107/108, 1 skipped), and `dotnet test CoreBankDemo.Rebuild.slnf` (all 5 projects green, coverage thresholds met). `dotnet build CoreBankDemo.Rebuild.slnf` produced no Dapr-obsolete-API warning. `git diff --check` reported no whitespace errors.

**Acceptance Criteria:**
- Given the current repository, when the story audit runs, then it records that Story 5.3 already implemented Kiota and that Story 6.7 removed only incomplete remnants and added enforcement.
- Given production source, live configuration, AppHosts, and scripts, when the architecture guard runs, then none of the forbidden service-invocation APIs, paths, headers, flags, or alternate client names are present.
- Given PaymentsAPI's composition root, when `ICoreBankApiClient` is resolved and both forwarding operations execute, then the generated Kiota adapter over logical `corebank-api` is the only production route and existing transport outcomes/tracing remain unchanged.
- Given normal and DevProxy AppHost branches, when their resource models start, then neither branch selects a transport; DevProxy merely proxies the same Kiota request path.
- Given every remaining Dapr package, registration, sidecar, component, and test, when reviewed, then it maps to retained event publication/subscription behavior and no request/response invocation responsibility.
- Given CoreBank publishes and Payments consumes an event, when regression and smoke tests run, then Dapr pub/sub behavior, duplicate handling, durable processing, and trace continuity remain green.
- Given current documentation and tooling descriptions, when read, then they show Payments→CoreBank via Kiota HTTP and CoreBank→Payments via Dapr pub/sub; historical records remain clearly historical.
- Given `dotnet test CoreBankDemo.Rebuild.slnf`, when Story 6.7 is complete, then the full gate passes at the existing coverage threshold and no Dapr service-invocation deprecation warning originates from repository code.

## Design Notes

The key implementation is deletion and proof, not another adapter. Story 5.3 already chose the right seam: application code depends on `ICoreBankApiClient`, and Kiota owns HTTP serialization beneath it. Story 6.7 should be small in production code and strong in regression protection.

Do not interpret the SDK deprecation as deprecation of the entire Dapr runtime or its pub/sub APIs. `DaprClient.PublishEventAsync`, ASP.NET subscription integration, the pubsub component, and sidecars remain architectural dependencies. Package removal is evidence-driven per project, not a repository-wide deletion of `Dapr.Client`/`Dapr.AspNetCore`.

The architecture guard should scan semantic danger signals, not the bare word `Dapr`. It must fail on invocation APIs/config while permitting retained pub/sub code and historical records. This keeps the invariant executable without erasing useful architecture history.

## External Evidence

- Dapr .NET SDK v1.18.4 release notes: the `InvokeMethodAsync` family is marked obsolete and native HTTP/gRPC clients are recommended: https://github.com/dapr/dotnet-sdk/releases/tag/v1.18.4
- Current DaprClient source applies `[Obsolete("Recommended guidance is to use a native HTTP or gRPC client for service invocation")]` to invocation methods: https://github.com/dapr/dotnet-sdk/blob/master/src/Dapr.Client/DaprClient.cs

## Verification

**Commands:**
- `rg -n 'Features(:|__)UseDapr|DaprCoreBankApiClient|InvokeMethodAsync|InvokeMethodWithResponseAsync|CreateInvokeMethodRequest|CreateInvokeHttpClient|DaprInvokeHandler|/v1\\.0/invoke/|dapr-app-id' CoreBankDemo.* tests scripts --glob '!**/bin/**' --glob '!**/obj/**'`
- `dotnet test tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankDemo.PaymentsAPI.Tests.csproj`
- `dotnet test tests/CoreBankDemo.CoreBankAPI.Tests/CoreBankDemo.CoreBankAPI.Tests.csproj`
- `dotnet test tests/CoreBankDemo.ServiceDefaults.Tests/CoreBankDemo.ServiceDefaults.Tests.csproj`
- `dotnet test CoreBankDemo.Rebuild.slnf`
- `git diff --check`

## Suggested Review Order

1. Existing-story audit and the exact boundary between forbidden invocation and retained pub/sub.
2. AppHost normal/DevProxy simplification and absence of transport selection.
3. Architecture guard coverage for APIs, routes, headers, flags, and alternate clients.
4. Kiota registration/forwarding regression tests and absence of fallback behavior.
5. Dapr package ownership plus pub/sub regression and end-to-end trace proof.
6. Current documentation cleanup and deferred-work resolution without rewriting history.
