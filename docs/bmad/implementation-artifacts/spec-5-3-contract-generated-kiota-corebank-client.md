---
title: 'Story 5.3: Contract-generated Kiota CoreBank client'
type: 'feature'
created: '2026-08-29'
status: 'done'
review_loop_iteration: 1
followup_review_recommended: false
baseline_commit: '1bc3c56e4f63af22fc336cf838b636d91b3ec383'
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-5-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** PaymentsAPI has no contract-driven CoreBank transport, so later forwarding cannot call the frozen account and transaction operations without coupling application logic to hand-written HTTP details.

**Approach:** Check in an explicit OpenAPI description of CoreBankAPI, generate a version-pinned Kiota client into intermediate build output, and wrap it with the sole application-owned CoreBank client port and adapter.

## Boundaries & Constraints

**Always:** Describe all four public operations and frozen wire shapes, including operation IDs, status codes, JSON content types, nullability, and success/error schemas. Generate before compilation beneath `$(IntermediateOutputPath)`, clean obsolete generated files, and keep generation incremental and working-tree-clean. Resolve `http://corebank-api` through configured service discovery; propagate ambient `traceparent` and `tracestate`; expose only application-owned request/results; treat every 2xx as transport success and every non-2xx, timeout, cancellation not requested by the caller, or exception as an explicit retry outcome.

**Ask First:** Changing a frozen route, verb, field, validation constraint, success status, content type, or response meaning; selecting transport behavior that conflicts with AD-11; adding a public operation.

**Never:** Commit generated sources; leak Kiota models outside the adapter; add a hand-written or Dapr CoreBank client, `Features:UseDapr`, replica addresses, forwarding processor logic, retries inside the adapter, or a live contract-diff dependency.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|---------------|---------------------------|----------------|
| Valid operation | Any of four operations returns representative 2xx JSON | Map to the corresponding application-owned result | Empty or malformed required success data returns retry outcome |
| Transport rejection | CoreBank returns 4xx or 5xx, including structured error JSON | Return retry outcome without throwing into application logic | Preserve status/diagnostic context without generated types |
| Interrupted call | Timeout, transport exception, or cancellation | Distinguish caller cancellation from transport failure | Propagate caller cancellation; classify other interruption for retry |
| Traced call | `Activity.Current` has trace state | Send current W3C `traceparent` and optional `tracestate` | Do not invent headers when no activity exists |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.CoreBankAPI/Controllers/AccountsController.cs:15-58` and `Controllers/TransactionsController.cs:14-60` -- read-only route, verb, and status behavior for the four operations.
- `CoreBankDemo.CoreBankAPI/Models/*.cs` -- read-only frozen request/response fields, validation constraints, and status shape represented by the contract.
- `CoreBankDemo.CoreBankAPI/OpenApi/corebank-api.json` -- add the checked-in generation source; this file does not exist yet.
- `.config/dotnet-tools.json` -- add the repository-pinned Kiota CLI tool manifest.
- `Directory.Packages.props` -- pin the Kiota runtime dependencies consumed by generated code and the adapter.
- `CoreBankDemo.PaymentsAPI/CoreBankDemo.PaymentsAPI.csproj` -- declare incremental pre-compile generation, stale-output cleanup, generated compile items, and intermediate-only output.
- `CoreBankDemo.PaymentsAPI/Outbox/ICoreBankApiClient.cs` and `CoreBankApiContracts.cs` -- add the sole application port and transport-independent inputs/results for all four operations.
- `CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs` -- adapt generated operations, logical service URL, trace headers, response mapping, and AD-11 transport outcomes.
- `CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs` and `Program.cs:1-21` -- register the generated request adapter and application client through the existing service-discovery HTTP defaults.
- `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankApiClientTests.cs` -- exercise the adapter with an in-memory HTTP handler; no live service.
- `tests/Directory.Build.props:21-37` -- existing generated-code exclusion applies; do not exclude application-owned adapter logic.

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.CoreBankAPI/OpenApi/corebank-api.json` -- encode the four frozen operations and explicit success/error contracts.
- [x] `.config/dotnet-tools.json`, `Directory.Packages.props`, and `CoreBankDemo.PaymentsAPI/CoreBankDemo.PaymentsAPI.csproj` -- pin Kiota and make clean/incremental intermediate generation part of compilation.
- [x] `CoreBankDemo.PaymentsAPI/Outbox/ICoreBankApiClient.cs`, `CoreBankApiContracts.cs`, and `KiotaCoreBankApiClient.cs` -- implement the isolated application port and generated-client adapter for every operation and matrix outcome.
- [x] `CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs` and `Program.cs` -- wire one logical `corebank-api` client through service discovery.
- [x] `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankApiClientTests.cs` -- cover every operation, representative 2xx/4xx/5xx, malformed success, headers, caller cancellation, timeout, and exceptions.

**Acceptance Criteria:**
- Given a clean checkout, when PaymentsAPI builds twice, then pinned Kiota generation compiles from the checked-in contract, the second build is incremental, obsolete generated files cannot survive, and Git remains clean.
- Given application forwarding code, when it uses `ICoreBankApiClient`, then no generated model crosses the port and all four CoreBank operations are available through application-owned types.
- Given `dotnet test CoreBankDemo.Rebuild.slnf`, when Story 5.3 is complete, then all rebuild tests pass and PaymentsAPI remains above its coverage threshold.

### Review Findings

- [x] [Review][Decision] Global HTTP resilience handler retries beneath the adapter, conflicting with AD-11 and the spec's "no retries inside the adapter" constraint — ServiceDefaults' `ConfigureHttpClientDefaults` (`CoreBankDemo.ServiceDefaults/Extensions.cs:43-50`) applies `AddStandardResilienceHandler()` to every named `HttpClient`, and `AddCoreBankApiClient` (`CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs`) registers `"corebank-api"` through that same factory with no override disabling its embedded Retry policy. **Resolved (2026-08-30):** user decision — remove the resilience handler for this client. `AddCoreBankApiClient` now calls `.RemoveAllResilienceHandlers()` on the `"corebank-api"` `HttpClientBuilder` with a code comment explaining the AD-11 rationale, so all retry semantics stay in the outbox layer. Location: `CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs`; `CoreBankDemo.ServiceDefaults/Extensions.cs:43-50`.
- [x] [Review][Decision] `TransactionStatusResponse`'s "free to shape" doc comment conflicts with `corebank-api.json`'s new ADR-gated frozen-contract declaration — `CoreBankDemo.CoreBankAPI/Models/TransactionStatusResponse.cs:1-9` documented itself as "free to shape — not frozen by any AD, unlike `TransactionResponse`" (from spec-4-4). This commit's `corebank-api.json` (`info.description`, line 5) declares the whole document, including the schema mirroring this exact model (`TransactionQueryResult`), "must only change alongside an ADR that renegotiates the underlying wire contract." **Resolved (2026-08-30):** user decision — the contract is frozen. Updated the doc comment to state that the shape mirrors `TransactionQueryResult` in `corebank-api.json` and is now frozen under AD-12, requiring an ADR and Kiota regeneration to change. Location: `CoreBankDemo.CoreBankAPI/Models/TransactionStatusResponse.cs:1-9`; `CoreBankDemo.CoreBankAPI/OpenApi/corebank-api.json:5`.
- [x] [Review][Patch] `ValidateAccountAsync`/`GetAccountDetailsAsync`/`GetTransactionStatusAsync` don't guard against null or blank string parameters, unlike `ProcessTransactionAsync`'s explicit null check [`CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs:41-42,67-68,142-143`] — `ProcessTransactionAsync` (lines 101-107) explicitly guards `request` with `ArgumentNullException.ThrowIfNull(request)` specifically because "a null request is a programmer error, not a transport outcome" per its own comment. The other three methods take a plain `string accountNumber`/`string idempotencyKey` with no equivalent guard; a null/blank value reaching the Kiota path-parameter indexer would fall into `ExecuteAsync`'s generic `catch (Exception)` (lines 219-231) and be silently reported as a `Retry` rather than surfacing as the caller bug it is. Fix: add `ArgumentException.ThrowIfNullOrWhiteSpace(...)` at the top of the three affected methods, mirroring the existing pattern. **Resolved (2026-08-30):** all three methods now fail fast for null, empty, or whitespace input, with transport-isolation tests.
- [x] [Review][Patch] Malformed (not merely incomplete) 2xx JSON body is classified as `TransportException` instead of `MalformedResponse` [`CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs:219-231`] — `ExecuteAsync`'s null-value path (lines 183-186) correctly classifies a 2xx response with missing/blank required fields as `MalformedResponse`, but if the 2xx body itself throws during Kiota's JSON deserialization (e.g. wrong JSON type for a field), that exception lands in the generic `catch (Exception)` and is reported as `TransportException`, not `MalformedResponse`, even though this is exactly the "malformed required success data" case the edge-case matrix assigns to `MalformedResponse`. Both outcomes still resolve to `CoreBankClientOutcome.Retry` (only the diagnostic `RetryReason` differs). Fix: catch a JSON/deserialization-specific exception type inside `ExecuteAsync` and classify it as `MalformedResponse`. **Resolved (2026-08-30):** `JsonException` from a 2xx response is classified as `MalformedResponse`; captured non-2xx status still takes precedence as `TransportRejection`.
- [x] [Review][Patch] Misleading comment: `Microsoft.Kiota.Bundle` package version doesn't "match" the Kiota CLI version as claimed [`Directory.Packages.props:52-54`] — the comment says the Bundle package is "Pinned to match the microsoft.openapi.kiota CLI version in .config/dotnet-tools.json," but the CLI is pinned to `1.34.1` while the Bundle package is pinned to `2.0.0` — independent versioning schemes never meant to be numerically equal. Fix: reword the comment to state the actual intent (known-compatible pinning, not numeric match). **Resolved (2026-08-30):** the comment now documents known-compatible, independently versioned pins.
- [x] [Review][Patch] Asymmetric test coverage: blank-required-string guard and trace-header propagation aren't tested for all four operations [`tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankApiClientTests.cs`] — the blank-string edge case is tested only for `ValidateAccountAsync` and `GetTransactionStatusAsync`, not `GetAccountDetailsAsync`/`ProcessTransactionAsync`, despite the same `IsBlank(...)` guard shape applying to all four. Likewise, `traceparent`/`tracestate` propagation is asserted only through `ValidateAccountAsync`, even though the same `ConfigureTraceContext` delegate is passed by all four operations — a future edit dropping it from one operation wouldn't be caught. Fix: add the missing blank-string cases and at least one trace-header assertion per remaining operation. **Resolved (2026-08-30):** blank required success fields and ambient trace propagation are now covered for every operation.
- [x] [Review][Defer] Two separate `dotnet-tools.json` tool manifests exist at different paths [`dotnet-tools.json` (root, pre-existing); `.config/dotnet-tools.json` (new)] — deferred, pre-existing. This commit adds `.config/dotnet-tools.json` (standard location, `microsoft.openapi.kiota` only). A pre-existing root-level `/dotnet-tools.json` (confirmed via `git log`, last touched at `e38a7b1`, predates this commit) holds `dotnet-outdated-tool` and is not consolidated. Standard `dotnet tool restore` only resolves `.config/dotnet-tools.json`, so the root manifest was already orphaned from standard tooling before this change.
- [x] [Review][Defer] `TransactionStatusResponse.ReceivedAt`/`ProcessedAt` use plain `DateTime`, unlike every other CoreBankAPI wire timestamp (`DateTimeOffset`) [`CoreBankDemo.CoreBankAPI/Models/TransactionStatusResponse.cs:12-13`] — deferred, pre-existing. `AccountDetailsResponse.CreatedAt/UpdatedAt` and `TransactionResponse.ProcessedAt` are `DateTimeOffset`; this model (unchanged by this diff, from story 4.4) is plain `DateTime`. If the value's `Kind` is ever `Unspecified` when serialized, an offset-less ISO string could be misinterpreted using local machine offset when parsed into `DateTimeOffset?` on the Payments side. `TransactionIntakeHandler.cs:100` sets it via `now.UtcDateTime` at creation, but round-trip Kind-preservation isn't verified.
- [x] [Review][Defer] `LastResponseStatusHandler`'s single `AsyncLocal` capture slot would cross-contaminate under concurrent `ICoreBankApiClient` calls from the same scope [`CoreBankDemo.PaymentsAPI/Outbox/LastResponseStatusHandler.cs`] — deferred, pre-existing latent risk, not reachable today. One static `AsyncLocal<StatusCapture?>` slot is set synchronously by `BeginCapture()`; two concurrent calls in the same scope before either sends its request would let the second overwrite the first's capture. Not reachable today — story 5.4's delivery strategy and every test in this diff call sequentially (confirmed via a full-suite run: 146/146 passing, 100% line / 98.91% branch coverage).

## Spec Change Log

- 2026-08-30 (code review): resolved both decision-needed findings. `AddCoreBankApiClient` now calls `.RemoveAllResilienceHandlers()` on the `"corebank-api"` client to keep all retry semantics in the outbox layer per AD-11. `TransactionStatusResponse`'s doc comment now states the shape is frozen under AD-12, mirroring `corebank-api.json`.
- 2026-08-30 (review patches): added fail-fast string argument guards, malformed-2xx JSON classification, symmetric malformed/trace tests for all four operations, and corrected the Kiota runtime/CLI compatibility comment. The focused suite passed 125 tests at 100% PaymentsAPI line coverage; the full rebuild gate passed 680 tests with one pre-existing Redis skip.
- 2026-08-30 (completion): all review findings are resolved; Story 5.3 is closed with no follow-up review required.

## Design Notes

Keep delivery classification transport-only. Account validity and transaction status remain successful business responses when received over 2xx; Story 5.4 decides how forwarding uses them. Caller-requested cancellation must remain cooperative rather than being converted into a retry-shaped result.

## Verification

**Commands:**
- `dotnet tool restore && dotnet clean CoreBankDemo.PaymentsAPI/CoreBankDemo.PaymentsAPI.csproj && dotnet build CoreBankDemo.PaymentsAPI/CoreBankDemo.PaymentsAPI.csproj && dotnet build CoreBankDemo.PaymentsAPI/CoreBankDemo.PaymentsAPI.csproj` -- expected: generation succeeds, stale output is replaced, and the second build is incremental.
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: all projects pass and PaymentsAPI coverage remains at least 90%.
- `git status --short && git diff --check` -- expected: only intended tracked source/spec changes and no generated files or whitespace errors.

## Suggested Review Order

**Transport boundary**

- Start with four-operation mapping, malformed-response guards, and retry classification.
  [`KiotaCoreBankApiClient.cs:39`](../../../CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs#L39)

- Preserve non-2xx status even when Kiota cannot parse the error body.
  [`LastResponseStatusHandler.cs:1`](../../../CoreBankDemo.PaymentsAPI/Outbox/LastResponseStatusHandler.cs#L1)

- Keep generated models behind an invariant-safe application result contract.
  [`CoreBankApiContracts.cs:24`](../../../CoreBankDemo.PaymentsAPI/Outbox/CoreBankApiContracts.cs#L24)

**Contract and generation**

- Define the frozen four-operation HTTP source consumed by Kiota.
  [`corebank-api.json:13`](../../../CoreBankDemo.CoreBankAPI/OpenApi/corebank-api.json#L13)

- Generate clean intermediate sources incrementally before compilation.
  [`CoreBankDemo.PaymentsAPI.csproj:49`](../../../CoreBankDemo.PaymentsAPI/CoreBankDemo.PaymentsAPI.csproj#L49)

- Compose Kiota over the logical Aspire endpoint and shared HTTP pipeline.
  [`CoreBankClientServiceCollectionExtensions.cs:24`](../../../CoreBankDemo.PaymentsAPI/CoreBankClientServiceCollectionExtensions.cs#L24)

**Verification**

- Exercise payloads, malformed responses, transport failures, cancellation, and tracing.
  [`CoreBankApiClientTests.cs:29`](../../../tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankApiClientTests.cs#L29)

- Prove requests flow through the DI-composed named client pipeline.
  [`CoreBankClientRegistrationTests.cs:78`](../../../tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankClientRegistrationTests.cs#L78)
