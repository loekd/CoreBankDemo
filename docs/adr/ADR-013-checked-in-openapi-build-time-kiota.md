# ADR-013: Checked-in OpenAPI with build-time Kiota generation

**Date:** 2026-08-29
**Status:** Accepted
**Deciders:** Architecture team
**Supersedes:** Hand-written CoreBank HTTP transport models and paths in PaymentsAPI

## Context

PaymentsAPI must call every public CoreBankAPI operation without duplicating route, payload, and response knowledge in a hand-written client. Runtime discovery or a live contract-diff test would make clean local builds depend on a running service. Committing generated code would create large, noisy diffs and allow stale generated files to survive contract changes.

## Decision

CoreBankAPI owns one checked-in OpenAPI document covering all public account and transaction operations with explicit operation IDs, status codes, JSON content types, schemas, required fields, and nullability.

PaymentsAPI uses a repository-pinned Kiota CLI during build to generate the C# transport client beneath `$(IntermediateOutputPath)`. OpenAPI-driven generation is incremental, cleans obsolete output when the contract changes, and never writes generated source into the working tree. A Kiota tool-version, class-name, namespace, or generation-option change requires a clean build unless that setting is also added to the MSBuild target's tracked inputs.

Generated types remain behind `ICoreBankApiClient`. `KiotaCoreBankApiClient` maps application-owned requests/results, resolves Aspire's logical `corebank-api` endpoint, propagates W3C trace headers, propagates caller-requested cancellation, and converts other transport failures into explicit retry-shaped outcomes. There is no parallel hand-written production client and no CI dependency on a live CoreBankAPI instance.

## Implementation

- `CoreBankDemo.CoreBankAPI/OpenApi/corebank-api.json` is the machine-readable contract owner.
- `.config/dotnet-tools.json` pins `microsoft.openapi.kiota`.
- `CoreBankDemo.PaymentsAPI/CoreBankDemo.PaymentsAPI.csproj` tracks the OpenAPI document for incremental generation, cleans obsolete generated output when it reruns, and includes generated sources only for compilation.
- `Directory.Packages.props` pins the Kiota runtime dependency.
- `CoreBankDemo.PaymentsAPI/Outbox/ICoreBankApiClient.cs`, `CoreBankApiContracts.cs`, and `KiotaCoreBankApiClient.cs` isolate generated transport code from application logic.
- `tests/CoreBankDemo.PaymentsAPI.Tests/CoreBankApiClientTests.cs` exercises all operations and transport outcomes with an in-memory HTTP handler.

## Consequences

### Positive
- Contract drift becomes a compile/test failure without requiring a running service.
- Generated route and serialization code is reproducible but absent from Git history.
- Application code depends on stable, owned types rather than generator output.

### Negative / Trade-offs
- Clean builds require restoring the pinned Kiota tool.
- MSBuild generation adds build complexity and must remain cross-platform.
- An OpenAPI change is an architecture/contract change and requires review alongside its adapter changes.

## Key takeaway

> Check in the contract and the generator version, not the generated client; keep generator types behind an application-owned port.
