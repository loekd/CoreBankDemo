---
title: 'Assertion API realignment'
type: 'feature'
created: '2026-08-31'
status: 'done'
review_loop_iteration: 0
context: []
baseline_commit: 'b4e6a0fe7e48cac56589c28ce03715642c0f3d32'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** LoadTestSupport's `/assert/drain` and `poll_until_drained` only poll 2 of the 4 message stores (`paymentsDb.OutboxMessages`, `coreBankDb.InboxMessages`) — they never check `coreBankDb.MessagingOutboxMessages` or `paymentsDb.InboxMessages`, so drain can report done while CoreBank's outbound events or Payments' inbound processing are still in flight. Separately, the assertion/inbox/outbox/MCP-tool logic (unlike the reset coordinator, which has 100% coverage) has zero test coverage, and the five-invariant balance-replay/pass-fail logic is duplicated verbatim between the REST handler and the MCP tool.

**Approach:** Extend drain to poll all four message stores for true "zero non-terminal" semantics; extract the duplicated invariant-check/balance-replay logic into one shared, testable class used by both the REST endpoint and the MCP tool; add Docker-free unit tests for the pure logic and PostgreSQL Testcontainers integration tests for the EF-backed queries, following the existing `LoadTestDatabaseResetterTests` pattern.

## Boundaries & Constraints

**Always:**
- Reset stays the sole truncate/reseed owner of the 10 `NL..LOAD` accounts; it must never touch the 3 CoreBankAPI-seeded demo accounts.
- `Status.Failed` means transport-exhausted only (AD-11); a business-rejected-but-`Completed` row (cached failure payload) is never counted as `Failed`.
- Drain checks all four stores — `paymentsDb.OutboxMessages`, `paymentsDb.InboxMessages`, `coreBankDb.InboxMessages`, `coreBankDb.MessagingOutboxMessages` — and is "drained" only when all four have zero `Pending`/`Processing` rows.
- REST (`/assert/results`, `/assert/drain`) and the MCP tools (`get_assertion_results`, `poll_until_drained`) must stay behaviorally identical; eliminate the current duplication by having both call one shared class rather than maintaining two copies.
- Pure invariant/balance-replay calculation is Docker-free unit-tested; EF-backed persistence queries are integration-tested against seeded PostgreSQL via Testcontainers, following `LoadTestDatabaseResetterTests`'s dual-`DbContext` + `RedisContainerFixture` pattern.
- Five-invariant business semantics (dedupe rule, balance math, Failed definition) are unchanged — this story realigns coverage and drain completeness, not the assertion rules themselves.

**Ask First:** None anticipated — this story's scope (drain completeness, shared assertion logic, test coverage) has no plausible path to an architectural or cross-service decision. If one surfaces mid-implementation, halt and ask.

**Never:**
- Do not touch `CoreBankDbContext`/`PaymentsDbContext` schema.
- Do not fix `CoreBankDemo.LoadTestSupport/Properties/launchSettings.json`'s port mismatch (5180 vs Aspire's 5181) — out of scope, log to `deferred-work.md`.
- Do not make LoadTestSupport a dependency of the main AppHost or any banking service.
- Do not build Story 7.2's MCP-tool realignment or Story 7.3's k6/Aspire run here — this story only covers the HTTP assertion API and its Docker-free/integration test coverage.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Drain with in-flight CoreBank outbound event | 1 row `Pending` in `coreBankDb.MessagingOutboxMessages`, other 3 stores empty | `/assert/drain` and `poll_until_drained` report not drained | N/A |
| Drain with in-flight Payments inbound event | 1 row `Processing` in `paymentsDb.InboxMessages`, other 3 stores empty | Not drained | N/A |
| Drain with all 4 stores terminal | 0 `Pending`/`Processing` rows across all 4 stores | Drained = true | N/A |
| Duplicate idempotency key replay | `expectedUnique=N`, seeded data includes N unique + duplicate-key rows | `NoDuplicateProcessing=true`; distinct processed count == N | N/A |
| Genuine transport failure | 1 row `Status=Failed` after retries exhausted | `NoFailedMessages=false`; overall pass=false | N/A |
| Business rejection, not a transport failure | `Completed` row with cached failure payload | `NoFailedMessages=true` (not counted as Failed) | N/A |
| Reset called twice in a row | `POST /reset` issued a second time before any new activity | Second call is idempotent: no double gate-release, no account drift | Coordinator's existing poison-state handling is unaffected |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.LoadTestSupport/Endpoints/AssertEndpoints.cs:18` -- `/assert/drain` handler; extend to check all 4 stores instead of 2.
- `CoreBankDemo.LoadTestSupport/Endpoints/AssertEndpoints.cs:47-230` -- `/assert/results?expectedUnique=N`; invariant checks + `CalculateExpectedBalances` replay logic (line ~204) to extract into a shared class.
- `CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs:65-157` -- `poll_until_drained`; same 2-of-4-store gap as `/assert/drain`.
- `CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs:164-299` -- `get_assertion_results`; duplicate copy of the balance-replay math to consolidate.
- `CoreBankDemo.LoadTestSupport/Endpoints/InboxEndpoints.cs`, `OutboxEndpoints.cs` -- raw inspection endpoints, currently untested.
- `CoreBankDemo.LoadTestSupport/DatabaseResetCoordinator.cs:8-102` -- reset/gate coordination; read-only reference, already 100% covered, do not modify.
- `CoreBankDemo.LoadTestSupport/LoadTestConstants.cs` -- `InitialBalance`, `AccountCount`; reference for balance math.
- `CoreBankDemo.CoreBankAPI/CoreBankDbContext.cs:15-19` -- `Accounts`, `InboxMessages`, `MessagingOutboxMessages` DbSets.
- `CoreBankDemo.PaymentsAPI/PaymentsDbContext.cs:8-11` -- `OutboxMessages`, `InboxMessages` DbSets.
- `CoreBankDemo.Messaging/MessageConstants.cs:21-30` -- `Status.Failed` = transport-exhausted-only definition (AD-11); `MaxRetryCount`.
- `tests/CoreBankDemo.LoadTestSupport.Tests/DatabaseResetCoordinatorTests.cs` -- existing 5-test, 100%-coverage precedent; new unit tests for extracted assertion logic go in this project, coverage scope in `.csproj:4` must widen beyond `DatabaseResetCoordinator*`.
- `tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/LoadTestDatabaseResetterTests.cs:17-28` -- dual-`DbContext` + `[Collection("Processor start gate Redis")]` + `PostgresContainerFixture.CreateDatabaseAsync` pattern to copy for new drain/assert/inbox/outbox integration tests.
- `tests/CoreBankDemo.Persistence.IntegrationTests/Infrastructure/PostgresContainerFixture.cs:32,72,92`, `RedisContainerFixture.cs` -- fixtures the new integration tests reuse (do not modify).
- `docs/bmad/constraints.md:5-11` -- canonical five-invariant wording; `:21-23` CoreBank↔Payments transport (Dapr pub/sub retained, out of this story's scope).

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs` (new) -- extract drain-check and five-invariant/balance-replay logic out of `AssertEndpoints.cs` and `LoadTestTools.cs`, eliminating the REST/MCP duplication. Implemented as two types in this file rather than literally one (see Spec Change Log): `LoadTestAssertionService` (EF-backed `CheckDrainAsync`/`GetResultsAsync`, ctor-injects both DbContexts) delegates its pure math to `LoadTestAssertionCalculator` (`ComputeAssertionResult`/`CalculateExpectedBalances`, no DbContext dependency) -- the split is what gives the pure math an actually Docker-free unit-testable seam under this repo's per-type coverage-tier filters.
- [x] `CoreBankDemo.LoadTestSupport/Endpoints/AssertEndpoints.cs` -- `/assert/drain` now checks `coreBankDb.MessagingOutboxMessages` and `paymentsDb.InboxMessages` alongside the existing 2 stores; both endpoints delegate to `LoadTestAssertionService` (DI-registered in `Program.cs`).
- [x] `CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs` -- `poll_until_drained` and `get_assertion_results` delegate to `LoadTestAssertionService`; duplicated inline logic removed. Both now serialize with `JsonSerializerDefaults.Web` (camelCase) so their JSON shape matches the REST endpoints' ASP.NET Core minimal-API default.
- [x] `tests/CoreBankDemo.LoadTestSupport.Tests/LoadTestAssertionServiceTests.cs` (new) -- 19 Docker-free unit tests for `LoadTestAssertionCalculator`'s pure logic: dedupe/exactly-once, all-submitted-processed, balance conservation, zero-failed vs business-rejected-but-Completed, balances-correct-by-replay, null-vs-set `expectedUnique`.
- [x] `tests/CoreBankDemo.LoadTestSupport.Tests/CoreBankDemo.LoadTestSupport.Tests.csproj` -- widened the coverage-collection filter (was `DatabaseResetCoordinator*` only) to add `Services.LoadTestAssertionCalculator*` (the pure half; measured 99.21% line / 100% method here). `LoadTestAssertionService*` itself (EF-backed) was added to `tests/Directory.Build.props`'s `$(PersistenceTierFilters)` instead, alongside `LoadTestDatabaseResetter*` -- same tier split already established for that sibling class.
- [x] `tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/AssertEndpointsIntegrationTests.cs` (new) -- 9 integration tests for the EF-backed drain (all 4 stores, individually and combined) and `GetResultsAsync` against seeded PostgreSQL, constructing `LoadTestAssertionService` directly (mirrors `LoadTestDatabaseResetterTests`'s dual-context pattern; omits the Redis collection since these tests never touch the processor-start gate). `LoadTestAssertionService` measured 100% line/branch/method here.
- [x] `tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/InboxOutboxEndpointsIntegrationTests.cs` (new) -- 6 integration tests for `InboxEndpoints`/`OutboxEndpoints`, driven through a real minimal-API `TestServer` (not LoadTestSupport's actual `Program.cs`, which also wires Redis/MCP that these read-only endpoints don't need) against seeded PostgreSQL: ordering, and the 50-row cap.
- [x] `docs/bmad/implementation-artifacts/deferred-work.md` -- appended an entry noting the `launchSettings.json` 5180-vs-5181 port mismatch, deferred out of this story's scope.

**Acceptance Criteria:**
- Given the rebuilt DbContexts and seeded PostgreSQL data with one non-terminal row in `coreBankDb.MessagingOutboxMessages` or `paymentsDb.InboxMessages`, when `/assert/drain` or `poll_until_drained` runs, then it reports not-drained (current code would report drained).
- Given all four message stores hold zero `Pending`/`Processing` rows, when drain runs, then it reports drained.
- Given `/assert/results?expectedUnique=N` and `get_assertion_results` are called against the same seeded dataset, when compared, then both return identical structured output (same fields, same pass/fail values) because both call `LoadTestAssertionService`.
- Given a business-rejected transaction that completed with a cached failure payload, when assertion results run, then it is never counted toward `NoFailedMessages=false`.
- Given `POST /reset` runs, when the 10 `NL..LOAD` accounts and message stores are inspected, then only those accounts and the four message stores are affected; the 3 CoreBankAPI demo accounts are untouched.
- Given `dotnet test CoreBankDemo.Rebuild.slnf`, when it runs, then all tests pass and the coverage gate is met, including the new `LoadTestAssertionService` unit tests and the new Testcontainers integration tests.

## Spec Change Log

- Design Notes described `LoadTestAssertionService` as a single class taking both `DbContext`s and exposing `CheckDrainAsync`/`GetResultsAsync`. Implemented as two types instead: `LoadTestAssertionService` (EF-backed, ctor-injects `CoreBankDbContext`/`PaymentsDbContext`) delegates its balance-replay/invariant math to a new sibling, `LoadTestAssertionCalculator` (pure, no DbContext). Reason: this repo's coverage gate (`tests/Directory.Build.props`) is enforced per-project via coverlet type-name filters, which can't split coverage measurement within a single type. `constraints.md` §4 requires the EF-backed half to be measured by the persistence integration tier and explicitly forbids "blanket exclusion" tricks; a single class mixing EF I/O with pure math would force it entirely into one tier or the other, defeating "pure invariant/balance-replay calculation is Docker-free unit-tested" (this spec's own Boundaries). The split mirrors the codebase's existing `DatabaseResetCoordinator`/`LoadTestDatabaseResetter` precedent and keeps the external contract (both public types live in `CoreBankDemo.LoadTestSupport.Services`, `LoadTestAssertionService` is still what `AssertEndpoints`/`LoadTestTools` call) unchanged from the design's intent.
- `LoadTestAssertionService.GetResultsAsync` takes `int? expectedUnique` (nullable), not the `int expectedUnique` shown in Design Notes. Reason: preserves `/assert/results`'s pre-existing optional-query-parameter behavior (omitted `expectedUnique` = the expected-unique check always passes) exactly, per this spec's own Boundaries ("Five-invariant business semantics ... are unchanged"). `get_assertion_results` (MCP) still always supplies a value; only the shared signature's nullability changed to accommodate REST's existing optional case.
- `DrainResult`/`AssertionResult` field names were chosen to be byte-identical to the pre-story-7.1 JSON shape wherever a real consumer parses them by name (k6's `k6/script.js` reads `outboxPending`/`inboxPending`/`completed`/`failed`/`isDrained` and `allPassed`/`checks.*.passed`/`checks.balancesCorrect.discrepancies`; the load-test skill's docs quote the same `poll_until_drained` field set) — not stated explicitly in this spec's frozen Intent, but required by constraints.md §2's "same externally observable behavior as main." The two new stores' pending counts were added as new, additive fields (`coreBankOutboxPending`/`paymentsInboxPending` equivalents: `CoreBankOutboxPending`/`PaymentsInboxPending`) rather than renaming/restructuring the existing ones.

## Design Notes

`LoadTestAssertionService` takes both `DbContext`s as constructor dependencies (mirroring `DatabaseResetCoordinator`'s existing pattern) and exposes `Task<DrainResult> CheckDrainAsync()` and `Task<AssertionResult> GetResultsAsync(int expectedUnique)`. Both records are plain DTOs so the REST minimal-API handlers and the MCP tool methods can each serialize the same object without re-deriving fields. Balance-replay math (currently `CalculateExpectedBalances`, `AssertEndpoints.cs:204`) moves into this service unchanged — only its location and callers change, not its algorithm.

## Verification

**Commands:**
- `dotnet build CoreBankDemo.LoadTestSupport/CoreBankDemo.LoadTestSupport.csproj` -- expected: 0 warnings/errors.
- `dotnet test tests/CoreBankDemo.LoadTestSupport.Tests` -- expected: new `LoadTestAssertionServiceTests` pass, Docker-free, coverage now includes the extracted service.
- `dotnet test tests/CoreBankDemo.Persistence.IntegrationTests --filter LoadTestSupport` -- expected: new drain/assert/inbox/outbox integration tests pass against Testcontainers PostgreSQL + Redis.
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: all gates green, coverage threshold met.
- `git diff --check` -- expected: no whitespace errors.

## Suggested Review Order

**Drain completeness fix (the story's core correctness fix)**

- New `CheckDrainAsync` polls all four message stores instead of two, closing the false-drained gap.
  [`LoadTestAssertionService.cs:134`](../../CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs#L134)

**Shared assertion logic and restored JSON contract (eliminates REST/MCP duplication, fixes a review-caught regression)**

- Pure invariant/balance-replay math extracted here; note `ComputeAssertionRequest` replaces 11 positional same-typed params.
  [`LoadTestAssertionService.cs:264`](../../CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs#L264)

- `NoDuplicateProcessingCheck`/`BalancesCorrectCheck` nest `Duplicates`/`Discrepancies` back inside their check objects — restores the pre-story JSON shape `k6/script.js` reads by path.
  [`LoadTestAssertionService.cs:59`](../../CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs#L59)

- `/assert/drain` and `/assert/results` now delegate entirely to the shared service.
  [`AssertEndpoints.cs:11`](../../CoreBankDemo.LoadTestSupport/Endpoints/AssertEndpoints.cs#L11)

- `poll_until_drained` now wraps its whole body in try/catch, matching `get_assertion_results`' existing error envelope.
  [`LoadTestTools.cs:69`](../../CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs#L69)

- `get_assertion_results` delegates to the shared service and serializes with `JsonSerializerDefaults.Web` to match REST's shape.
  [`LoadTestTools.cs:167`](../../CoreBankDemo.LoadTestSupport/McpTools/LoadTestTools.cs#L167)

**New test coverage (peripherals)**

- HTTP round-trip test locking the restored JSON contract in place and proving REST/MCP output are field-for-field identical.
  [`AssertEndpointsHttpIntegrationTests.cs`](../../tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/AssertEndpointsHttpIntegrationTests.cs)

- Docker-free unit tests for the extracted pure invariant/balance-replay logic.
  [`LoadTestAssertionServiceTests.cs`](../../tests/CoreBankDemo.LoadTestSupport.Tests/LoadTestAssertionServiceTests.cs)

- Testcontainers integration tests for the EF-backed drain/assert queries against seeded PostgreSQL.
  [`AssertEndpointsIntegrationTests.cs`](../../tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/AssertEndpointsIntegrationTests.cs)

- Real-`TestServer` coverage for the previously-untested inbox/outbox inspection endpoints.
  [`InboxOutboxEndpointsIntegrationTests.cs`](../../tests/CoreBankDemo.Persistence.IntegrationTests/LoadTestSupport/InboxOutboxEndpointsIntegrationTests.cs)

- Coverage-tier filter widened to include the new pure calculator and EF-backed service.
  [`CoreBankDemo.LoadTestSupport.Tests.csproj:1`](../../tests/CoreBankDemo.LoadTestSupport.Tests/CoreBankDemo.LoadTestSupport.Tests.csproj#L1), [`Directory.Build.props:24`](../../tests/Directory.Build.props#L24)
