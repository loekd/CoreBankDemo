---
title: 'MCP server tools: true thin wrappers over LoadTestSupport REST endpoints'
type: 'refactor'
created: '2026-08-31'
status: 'done'
review_loop_iteration: 0
context: []
baseline_commit: 'd618d5a7776726390a77bdae915664ed5f905101'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Three of `LoadTestTools`' six MCP tools don't actually wrap their REST counterparts — they reimplement the logic and have drifted. `reset_database` reimplements `/reset`'s truncate+balance-reset SQL directly, skipping `DatabaseResetCoordinator`: no transaction wrapping, no idempotent-replay guard, and no `IProcessorStartGatePublisher.ReleaseAsync()` — a load test driven through MCP's `reset_database` would leave every processor blocked on its start gate forever. The four `get_*_inbox`/`get_*_outbox` tools re-query the same tables with a different shape (field-projected, `{count, messages}`-wrapped, configurable `limit`/`status`) instead of matching the REST endpoints' plain 50-row entity array. Story 7.1 fixed this exact drift for `poll_until_drained`/`get_assertion_results`; this closes the gap for the remaining four tools.

**Approach:** `reset_database` delegates to the existing, already-100%-covered `DatabaseResetCoordinator.ResetAndReleaseAsync`. The four inspection tools run the exact same query as their REST counterpart (no extra params, no reshaping). Both proven field-for-field identical, mirroring story 7.1's `Rest_and_mcp_produce_field_for_field_identical_json` proof for `/assert/results`.

## Boundaries & Constraints

**Always:** `reset_database` calls `DatabaseResetCoordinator.ResetAndReleaseAsync` (never touches raw SQL/transactions/the gate directly), preserving the guarantees `DatabaseResetCoordinatorTests.cs` already covers at 100%. Each of the five fixed tools produces JSON field-for-field identical (`JsonNode.DeepEquals`) to its REST counterpart for the same seeded data. REST routes, shapes, and the 50-row cap are unchanged — `k6/script.js`, `LoadTestInitializer/Program.cs`, and `DemoRunner` consume them byte-for-byte today. `poll_until_drained`/`get_assertion_results` (already correct per 7.1) are untouched.

**Ask First:** If byte-for-byte parity for any tool turns out to require changing a REST endpoint's shape or route rather than only the MCP side — halt and ask; real consumers depend on the current REST shapes.

**Never:** Add `limit`/`status` filtering to the REST endpoints or keep it on the MCP side — dropping it is this story's deliberate simplification. Modify `DatabaseResetCoordinator`, `LoadTestDatabaseResetter`, `ProcessorStartGate`/`RedisProcessorStartGate`, `LoadTestAssertionService`, `AssertEndpoints.cs`, or any of their existing tests. Fix the pre-existing `NoFailedMessages` single-store gap (deferred, targeted at story 7.3). Touch `k6/script.js`, `LoadTestInitializer/Program.cs`, or `DemoRunner`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| `reset_database` on a fresh load-test AppHost | No prior reset this generation | Same fields/values as `POST /reset` (`message`, `accountsReset`, `totalBalance`, `initialBalancePerAccount`); processor start gate is released exactly once | N/A |
| `reset_database` called twice | Second call after the first already succeeded | Idempotent: returns the cached first result via `DatabaseResetCoordinator`'s existing replay guard, no second gate-release | N/A |
| `reset_database` when the coordinator throws | e.g. processors already released outside this generation | MCP call does not crash the tool invocation | Returns `{error, detail}` JSON, matching `poll_until_drained`'s existing exception-wrapping convention |
| `get_corebank_inbox` / `get_corebank_outbox` / `get_payments_inbox` / `get_payments_outbox` | 55 seeded rows in the relevant table | Returns the 50 most recent rows, newest-first, as a plain JSON array of full entities — identical to the REST endpoint's response for the same data | N/A |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs:22-59` -- `ResetDatabase`: replace the raw `ExecuteSqlRawAsync` block and `CoreBankDbContext`/`PaymentsDbContext` params with a `DatabaseResetCoordinator coordinator` param + `coordinator.ResetAndReleaseAsync(ct)`; try/catch like `PollUntilDrained` (156-159) returning `{error="reset_failed", detail}`; shape success JSON to match `ResetEndpoints.cs`'s fields.
- `CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs:184-322` -- the four `Get*Inbox`/`Get*Outbox` tools: drop `limit`/`status` params and the `{count, messages}`/projection shape; use the same `OrderByDescending(...).Take(50).ToListAsync(ct)` query (no `.Select()`) as the matching REST endpoint, serialized with the existing `McpJsonOptions`.
- `CoreBankDemo.LoadTestSupport/Endpoints/ResetEndpoints.cs`, `InboxEndpoints.cs`, `OutboxEndpoints.cs` -- read-only reference for target field names/shapes; do not modify.
- `CoreBankDemo.LoadTestSupport/DatabaseResetCoordinator.cs:55-102` -- `ResetAndReleaseAsync`; already DI-registered (`Program.cs:29`), already 100%-covered by `DatabaseResetCoordinatorTests.cs`; call it, do not modify it.
- `CoreBankDemo.LoadTestSupport/LoadTestConstants.cs:5-6` -- `InitialBalance`; already used by `LoadTestTools` for the success JSON's `initialBalancePerAccount` field.
- `tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/AssertEndpointsHttpIntegrationTests.cs:24-40,156-186` -- pattern to replicate: `TestServer` hosting the real endpoint(s), MCP tool's static method called directly against a parallel `DbContext` on the same seeded data, `JsonNode.DeepEquals` proof. Copy into a new file; this one stays `/assert/*`-scoped.
- `tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/InboxOutboxEndpointsIntegrationTests.cs` -- existing 6 REST-only tests (ordering + 50-row cap); unaffected, must keep passing unmodified.
- `tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/LoadTestDatabaseResetterTests.cs:116-177` -- existing real-Redis proof that `DatabaseResetCoordinator` releases the gate; don't duplicate, only prove `reset_database` delegates to the same coordinator.
- `tests/CoreBankDemo.LoadTestSupport.Tests/DatabaseResetCoordinatorTests.cs` -- existing 5-test, 100%-coverage suite for the coordinator (mocked deps); reference only, do not modify.

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs` -- `ResetDatabase` delegates to `DatabaseResetCoordinator.ResetAndReleaseAsync`; result shaped to match `/reset`'s JSON fields; exceptions wrapped as `{error, detail}`.
- [x] `CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs` -- `GetCoreBankInbox`, `GetCoreBankOutbox`, `GetPaymentsInbox`, `GetPaymentsOutbox` each run the identical query to their REST counterpart (50-row cap, no filter params, plain array output).
- [x] `tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/McpToolsHttpIntegrationTests.cs` (new) -- `AssertEndpointsHttpIntegrationTests.cs`'s pattern: `TestServer` hosting `MapResetEndpoints`/`MapInboxEndpoints`/`MapOutboxEndpoints` plus a `DatabaseResetCoordinator` wired with a mocked `IProcessorStartGatePublisher` (no real Redis, mirrors `DatabaseResetCoordinatorTests.cs`'s style); `JsonNode.DeepEquals` proofs for all five tools vs REST on the same seeded data; one test proving `reset_database` calls `ReleaseAsync`; one test proving repeated `reset_database` calls stay idempotent through MCP.
- [x] `tests/Directory.Build.props` -- no change; `LoadTestTools` stays outside `PersistenceTierFilters`, matching the existing endpoint-lambda classes, none of which are individually coverage-gated today.

**Acceptance Criteria:**
- Given the MCP server at the LoadTestSupport root, when `reset_database`, `poll_until_drained`, `get_assertion_results`, and the four `get_*_inbox`/`get_*_outbox` tools are invoked against the same seeded PostgreSQL data as their REST counterpart, then each pair's JSON output is field-for-field identical.
- Given a load test driven entirely through MCP tools (no REST calls), when `reset_database` runs, then the processor start gate is released exactly as it is when `/reset` is called directly.
- Given `reset_database` is called a second time in the same generation, when compared to the first call's result, then the second call returns the same cached result without a second gate-release (existing `DatabaseResetCoordinator` behavior, now reachable through MCP too).
- Given `dotnet test CoreBankDemo.Rebuild.slnf`, when it runs, then all tests pass including the new parity tests, and the coverage gate is met.

## Spec Change Log

- **Implementation deviation (not a spec renegotiation):** the Code Map's `ResetDatabase(DatabaseResetCoordinator coordinator, CancellationToken ct)` signature doesn't compile — `DatabaseResetCoordinator` is `internal`, and a `public static` MCP tool method cannot expose an internal type as a parameter (CS0051). Making the class (and, transitively, its constructor's `ILoadTestDatabaseResetter`/`DatabaseResetState` dependencies) `public` would touch three of the "Never Modify" types. Instead `ResetDatabase` takes `IServiceProvider serviceProvider` and resolves `serviceProvider.GetRequiredService<DatabaseResetCoordinator>()` inside the method body — confirmed via the MCP SDK's own XML docs (`ModelContextProtocol.Core`, `McpServerTool`) that `IServiceProvider`-typed parameters are a documented special case: bound from the request's `RequestContext`, excluded from the tool's JSON schema, same mechanism ASP.NET Core minimal APIs use. `DatabaseResetCoordinator.cs` itself is untouched (`git diff` confirms zero lines changed). Net effect on behavior and JSON output: none — `ResetAndReleaseAsync` is still the only thing that runs the reset.

## Design Notes

No new types. This is a delegation fix inside `LoadTestTools`: `reset_database` starts calling `DatabaseResetCoordinator` (like `/reset` already does); the four inspection tools run the identical EF query their REST counterpart runs, copied verbatim rather than extracted into a new shared service (the REST endpoints are un-extracted three-line lambdas, so a service here would be a bigger abstraction than the REST side has). The safety net against future re-drift is the new `JsonNode.DeepEquals` parity tests, the same mechanism story 7.1 used for `/assert/*`.

## Verification

**Commands:**
- `dotnet build CoreBankDemo.LoadTestSupport/CoreBankDemo.LoadTestSupport.csproj` -- expected: 0 warnings/errors.
- `dotnet test tests/CoreBankDemo.Persistence.IntegrationTests --filter LoadTestSupport` -- expected: new `McpToolsHttpIntegrationTests` pass alongside the existing, unmodified `InboxOutboxEndpointsIntegrationTests`/`AssertEndpointsHttpIntegrationTests`/`LoadTestDatabaseResetterTests`.
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: all gates green, coverage threshold met.
- `git diff --check` -- expected: no whitespace errors.

## Suggested Review Order

**`reset_database` now delegates to the real coordinator**

- Entry point: the CS0051 workaround and the delegation itself — resolves the already-100%-covered coordinator instead of reimplementing its transaction/idempotency/gate-release logic.
  [`LoadTestTools.cs:28`](../../CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs#L28)

- The actual fix: this one call now gets transactional truncation, idempotent replay, and processor-start-gate release for free.
  [`LoadTestTools.cs:39`](../../CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs#L39)

- Proves the fix: a load test driven purely through MCP now releases the gate exactly once, same as REST.
  [`McpToolsHttpIntegrationTests.cs:248`](../../tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/McpToolsHttpIntegrationTests.cs#L248)

- Proves the pre-existing idempotent-replay guard is now reachable through MCP, not just REST.
  [`McpToolsHttpIntegrationTests.cs:260`](../../tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/McpToolsHttpIntegrationTests.cs#L260)

- Closes the review-caught matrix gap: the coordinator's failure path now has a covering test, not just its success paths.
  [`McpToolsHttpIntegrationTests.cs:273`](../../tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/McpToolsHttpIntegrationTests.cs#L273)

- REST/MCP JSON parity for the reset success shape, byte-for-byte via `JsonNode.DeepEquals`.
  [`McpToolsHttpIntegrationTests.cs:215`](../../tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/McpToolsHttpIntegrationTests.cs#L215)

**The four inspection tools now run the REST endpoints' exact query**

- `get_corebank_inbox`/`outbox` and `get_payments_inbox`/`outbox` dropped `limit`/`status`/projection; identical to their REST counterpart now.
  [`LoadTestTools.cs:181`](../../CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs#L181)

- [`LoadTestTools.cs:195`](../../CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs#L195)
- [`LoadTestTools.cs:209`](../../CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs#L209)
- [`LoadTestTools.cs:223`](../../CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs#L223)

- Parity proof at the 50-row cap boundary — the exact scenario the old MCP shape would have diverged on.
  [`McpToolsHttpIntegrationTests.cs:113`](../../tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/McpToolsHttpIntegrationTests.cs#L113)

- [`McpToolsHttpIntegrationTests.cs:139`](../../tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/McpToolsHttpIntegrationTests.cs#L139)
- [`McpToolsHttpIntegrationTests.cs:163`](../../tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/McpToolsHttpIntegrationTests.cs#L163)
- [`McpToolsHttpIntegrationTests.cs:189`](../../tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/McpToolsHttpIntegrationTests.cs#L189)

**Peripherals**

- Review-caught doc drift: examples/parameter tables still described the removed `limit`/`status` filtering.
  [`SKILL.md:186`](../../.claude/skills/load-test/SKILL.md#L186)
  [`README.md:69`](../../CoreBankDemo.LoadTestSupport/README.md#L69)
