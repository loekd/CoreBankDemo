---
title: 'Story 5.3: Contract-generated Kiota CoreBank client'
type: 'feature'
created: '2026-08-29'
status: 'done'
review_loop_iteration: 0
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

## Spec Change Log

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
