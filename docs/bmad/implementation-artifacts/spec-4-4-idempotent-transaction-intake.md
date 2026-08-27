---
title: 'Story 4.4: Idempotent transaction intake'
type: 'feature'
created: '2026-08-25'
status: 'done'
baseline_commit: '23bbed3628731b71406eacb6358534dacf08616b'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-4-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `POST /api/transactions/process` must dedupe before any business logic runs (FR-9, FR-10; AD-4, AD-11), so PaymentsAPI's retries can never execute the same transfer twice. Nothing in `CoreBankDemo.CoreBankAPI` yet accepts a request, stores an `InboxMessage`, or reports status — stories 4.1–4.3 built the domain model, validation, and execution primitives, but no intake path exists. Legacy's `TransactionsController` also violates AD-2 (business logic — dedupe branching, response-payload deserialization, activity enrichment — lives directly in the controller) and has dead code: its manual `ModelState.IsValid` check is unreachable under `[ApiController]`'s default automatic-400 behavior, so legacy never actually returns its intended `BadRequest(new { Errors })` shape for invalid requests — a genuine brownfield defect this story fixes, not preserves.

**Approach:** Recreate `TransactionRequest` (frozen DataAnnotations shape, byte-for-byte from `main`). Extend story 4.1's `InboxMessage`/`CoreBankDbContext` wiring with a concrete `InboxMessageRepository : InboxMessageRepositoryBase<InboxMessage, CoreBankDbContext>` (the kernel base story 2.2 built) that also implements a new `IInboxMessageRepository` port exposing exactly what intake needs: `StoreIfNewAsync` (inherited from the kernel base, already race-safe per AD-4) and a new `FindByIdempotencyKeyAsync` (the kernel base only offers `FindByIdAsync(Guid)` — lookup by the row's internal id, not by the string idempotency key intake needs). Add a `TransactionIntakeHandler` — pure business logic (dedupe check, store, build responses), unit-tested with a mocked `IInboxMessageRepository`, per the `conventions` skill's "business logic lives in handler/executor classes returning domain types, not `IActionResult`". `TransactionsController` stays thin: bind, check `ModelState` (with `ApiBehaviorOptions.SuppressModelStateInvalidFilter = true` set in `Program.cs` so the manual check is actually reachable — the fix for legacy's dead-code defect), call the handler, map its result to an `IActionResult`.

## Boundaries & Constraints

**Always:**
- `CoreBankDemo.CoreBankAPI/Models/TransactionRequest.cs`: recreated byte-for-byte from `git show 121e3b3^:CoreBankDemo.CoreBankAPI/Models/TransactionRequest.cs` (`FromAccount`/`ToAccount` `[Required][StringLength(34, MinimumLength = 15)]`, `Amount` `[Required][Range(0.01, 1000000)]`, `Currency` `[Required][StringLength(3, MinimumLength = 3)][RegularExpression(@"^[A-Z]{3}$")]`, `TransactionId` `[Required][StringLength(100, MinimumLength = 1)]`).
- `IInboxMessageRepository` (new, `CoreBankDemo.CoreBankAPI/Inbox/`): `Task<bool> StoreIfNewAsync(InboxMessage message, CancellationToken cancellationToken)`; `Task<InboxMessage?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken)`. `InboxMessageRepository` implements both this port and `InboxMessageRepositoryBase<InboxMessage, CoreBankDbContext>` (the kernel base, satisfied automatically — `StoreIfNewAsync`'s inherited signature matches the new port) — the same class is both the future kernel processor's store (story 4.6) and intake's repository, exactly mirroring legacy's dual role.
- `TransactionIntakeHandler` (new): constructor-injected with `IInboxMessageRepository`, `IOptions<InboxProcessingOptions>` (for `PartitionHelper.GetPartitionId`), `TimeProvider`, `ILogger<TransactionIntakeHandler>` — no `IActionResult` anywhere in its surface (`conventions` skill). Two methods:
  - `ProcessAsync(TransactionRequest request, CancellationToken)` → `TransactionIntakeResult` (new internal record: `Outcome` enum `Accepted | Replayed | InFlight | TransportFailed`, `TransactionResponse? Response`, `string[]? Errors`). Logic: capture `var now = timeProvider.GetUtcNow();` once; check `FindByIdempotencyKeyAsync(request.TransactionId)` first — if found, branch exactly like AD-11's contract (`Completed` + non-empty `ResponsePayload` → `Replayed` with the deserialized cached `TransactionResponse`; `Pending`/`Processing` → `InFlight` with a fresh `TransactionResponse(request.TransactionId, existing.Status, new DateTimeOffset(existing.ReceivedAt, TimeSpan.Zero))`; `Failed` → `TransportFailed` with `Errors = [existing.LastError ?? "Transaction failed"]`); otherwise build a new `InboxMessage` (`IdempotencyKey = TransactionId = request.TransactionId`, `PartitionId` via `PartitionHelper.GetPartitionId(request.TransactionId, options.PartitionCount)`, `Status = MessageConstants.Status.Pending`, `ReceivedAt = now.UtcDateTime`, `TraceParent = Activity.Current?.Id`, `TraceState = Activity.Current?.TraceStateString`) and call `StoreIfNewAsync`; `true` → `Accepted` with `TransactionResponse(request.TransactionId, MessageConstants.Status.Pending, now)`; `false` (lost the race) → re-query via `FindByIdempotencyKeyAsync` and branch exactly as the found-on-first-check path above (the race winner's row is now visible).
  - `GetStatusAsync(string transactionId, CancellationToken)` → `TransactionStatusResult` (new internal record: `Found` bool, `CachedResponse`/`StatusResponse` — one or the other, never both). `Completed` + non-empty `ResponsePayload` → deserialized `TransactionResponse`; any other existing status (including `Failed` — GET never special-cases it, matching legacy) → new `TransactionStatusResponse(TransactionId, Status, ReceivedAt, ProcessedAt)` (new record, not frozen by any AD — free to shape); not found → `Found = false`.
- `TransactionsController` (`api/transactions`, matching `TransactionsController`'s legacy `[Route("api/[controller]")]`): `POST process` binds `TransactionRequest`; if `!ModelState.IsValid`, `BadRequest(new { Errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage) })` (conventions skill's exact shape); else call the handler and map `Accepted`→`Accepted($"/api/transactions/{id}", response)` (202), `Replayed`→`Ok(response)` (200), `InFlight`→`Accepted($"/api/transactions/{id}", response)` (202, AD-11: "202 with current status"), `TransportFailed`→`BadRequest(new { Errors })`. `GET {idempotencyKey}` (route parameter name matches FR-10's literal text and legacy exactly, even though it's the same value as `TransactionId` elsewhere) calls the handler; not found → `NotFound(new { Errors = ["Transaction not found"] })`; else `Ok(cachedResponse or statusResponse)`.
- `Program.cs`: add `builder.Services.AddControllers();`, `builder.Services.Configure<ApiBehaviorOptions>(o => o.SuppressModelStateInvalidFilter = true);` (the fix — without this, ASP.NET's default filter would 400 before the controller's manual check ever runs, using the framework's shape instead of `{ Errors }`), `builder.AddInboxProcessingOptions();`, DI registrations for `IInboxMessageRepository`→`InboxMessageRepository` and `TransactionIntakeHandler` (both `AddScoped`, matching the Scoped `CoreBankDbContext`), and `app.MapControllers();` in the pipeline. No hosted services yet (`InboxProcessor` is story 4.6).
- Activity enrichment: `Activity.Current?.SetTag(...)` for `transaction.id`/`from_account`/`to_account`/`amount`/`currency` on intake and an `outcome` tag on every branch (matches legacy's `EnrichCurrentActivity`, observability skill's tag-enrichment pattern) — no new `ActivitySource` needed (ASP.NET Core's own request `Activity`, already instrumented via `AddServiceDefaults`, is enriched in place, never a new child span).

**Ask First:** None — every decision (handler split, dead-code-fix for `ModelState`, GET's always-200 semantics, `TransactionStatusResponse`'s shape, `ProcessedAt`'s value for non-terminal rows) is resolved inline above with a documented rationale.

**Never:** Call `TransactionValidator.Validate` or `TransactionExecutor.ExecuteAsync` from this story — those require loaded/locked `Account` snapshots and only run during actual execution (story 4.6); this story only accepts, dedupes, and stores `Pending` rows. Touch `Account.cs`, `CoreBankDbContext.cs`, `Inbox/InboxMessage.cs`, `Outbox/MessagingOutboxMessage.cs`, `DemoAccountSeeder.cs`, `Inbox/TransactionValidator.cs`, `Inbox/TransactionExecutor.cs`, `Inbox/AccountRepository.cs`, or `Models/TransactionResponse.cs` (stories 4.1–4.3, done, frozen). Register `IAccountRepository`, `ITransactionExecutor`, `InboxProcessor`, or `IEventPublisher` in `Program.cs` — those are stories 4.6/4.7.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output |
|----------|--------------|------------------|
| Valid request, no existing row | Fresh `TransactionId` | `StoreIfNewAsync` called with a `Pending` `InboxMessage`; `202 Accepted` with `TransactionResponse(TransactionId, Pending, now)` |
| Invalid request (any DataAnnotations violation) | e.g. `Amount = -1`, missing `Currency` | `400 BadRequest(new { Errors })` listing every violation, handler never called |
| Duplicate, existing row `Completed` with cached payload | `FindByIdempotencyKeyAsync` returns `Completed` + non-empty `ResponsePayload` | `200 Ok` with the deserialized cached `TransactionResponse`, verbatim — no new store attempt |
| Duplicate, existing row `Pending`/`Processing` | in-flight | `202 Accepted` with `TransactionResponse(TransactionId, existing.Status, existing.ReceivedAt)` — AD-11 |
| Duplicate, existing row `Failed` | transport gave up | `400 BadRequest(new { Errors = [existing.LastError ?? "Transaction failed"] })` |
| `StoreIfNewAsync` returns `false` (lost a concurrent race) | two requests race on the same `TransactionId` | Re-query and branch exactly as the found-on-first-check row above (never a 500 for this case) |
| `GET {id}`, unknown id | no matching row | `404 NotFound(new { Errors = ["Transaction not found"] })` |
| `GET {id}`, `Completed` with cached payload | | `200 Ok` with the deserialized `TransactionResponse` |
| `GET {id}`, any other status including `Failed` | | `200 Ok` with `TransactionStatusResponse(TransactionId, Status, ReceivedAt, ProcessedAt)` — GET never special-cases `Failed` |
| `Completed` row whose `ResponsePayload` is null/empty (should not happen, defensive) | data corruption / bug elsewhere | Falls through to the status-response branch rather than crashing on a null deserialize |

</frozen-after-approval>

## Code Map

- New: `CoreBankDemo.CoreBankAPI/Models/TransactionRequest.cs`, `CoreBankDemo.CoreBankAPI/Models/TransactionStatusResponse.cs`
- New: `CoreBankDemo.CoreBankAPI/Inbox/InboxMessageRepository.cs` (`IInboxMessageRepository` + `InboxMessageRepository`)
- New: `CoreBankDemo.CoreBankAPI/Inbox/TransactionIntakeHandler.cs` (`TransactionIntakeHandler` + `TransactionIntakeResult`/`TransactionIntakeOutcome`/`TransactionStatusResult`)
- New: `CoreBankDemo.CoreBankAPI/Controllers/TransactionsController.cs`
- Modify: `CoreBankDemo.CoreBankAPI/Program.cs` — `AddControllers()`, `ApiBehaviorOptions.SuppressModelStateInvalidFilter = true`, `AddInboxProcessingOptions()`, DI for `IInboxMessageRepository`/`TransactionIntakeHandler`, `app.MapControllers()`
- New: `tests/CoreBankDemo.CoreBankAPI.Tests/TransactionIntakeHandlerTests.cs` (Moq against `IInboxMessageRepository`, tier 1), `tests/CoreBankDemo.CoreBankAPI.Tests/InboxMessageRepositoryTests.cs` (SQLite, tier 2 — `FindByIdempotencyKeyAsync` found/not-found, `StoreIfNewAsync` duplicate-race behavior), `tests/CoreBankDemo.CoreBankAPI.Tests/TransactionsControllerTests.cs` (thin — `ModelState` invalid → `BadRequest` shape; valid → delegates to a mocked handler and maps each outcome correctly)
- Not touched: `Account.cs`, `CoreBankDbContext.cs`, `Inbox/InboxMessage.cs`, `Outbox/MessagingOutboxMessage.cs`, `DemoAccountSeeder.cs`, `Inbox/TransactionValidator.cs`, `Inbox/TransactionExecutor.cs`, `Inbox/AccountRepository.cs`, `Models/TransactionResponse.cs` (stories 4.1–4.3, done)

## Tasks & Acceptance

**Execution:**
- [x] `Models/TransactionRequest.cs`, `Models/TransactionStatusResponse.cs`
- [x] Tests first: `InboxMessageRepository` (SQLite tier 2), `TransactionIntakeHandler` (Moq tier 1) covering every I/O matrix row
- [x] `Inbox/InboxMessageRepository.cs`, `Inbox/TransactionIntakeHandler.cs`
- [x] `Controllers/TransactionsController.cs` + its own thin mapping tests
- [x] `Program.cs` wiring (`AddControllers`, `ApiBehaviorOptions`, DI, `MapControllers`)

**Acceptance Criteria:**
- Given a valid `TransactionRequest`, when POSTed, then the inbox row is stored via `StoreIfNewAsync` and `202` returns the frozen `TransactionResponse` with `Pending` status
- Given the same `TransactionId` POSTed again, then `Completed` replays the cached `ResponsePayload` verbatim, `Pending`/`Processing` returns `202` with current status (AD-11)
- Given `GET /api/transactions/{id}`, then it reports status; validation errors return `BadRequest(new { Errors })` with all errors
- Given the controller, it contains no business logic — proven by handler-level unit tests (mocked repository) plus thin controller-mapping tests

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — passed; `CoreBankDemo.CoreBankAPI.Tests` 69/69 at 97.47% line / 88% branch / 100% method (≥90% line gate met); no regressions in `Messaging.Tests` (153/153), `ServiceDefaults.Tests` (117/117), `PaymentsAPI.Tests` (1/1)
- `dotnet build CoreBankDemo.Messaging/CoreBankDemo.Messaging.csproj` — passed, no source changes needed

## Spec Change Log

- `TransactionIntakeHandler.TryDeserializeResponse` was patched post-implementation: the original version only guarded `string.IsNullOrEmpty(responsePayload)` before calling `JsonSerializer.Deserialize`, so a non-empty but malformed `ResponsePayload` on a `Completed` row threw an uncaught `JsonException`, contradicting this spec's own I/O matrix row ("`Completed` row whose `ResponsePayload` is null/empty ... falls through to the status-response branch rather than crashing"). Added a `try/catch (JsonException)` around the deserialize call so corrupt payloads degrade to the same defensive fallback as null/empty ones, with a `LogWarning` on catch. Two tests added (`ProcessAsync_falls_through_to_in_flight_when_a_completed_duplicates_response_payload_is_corrupt`, `GetStatusAsync_falls_through_to_a_status_response_when_a_completed_rows_payload_is_corrupt`) to prove it.

## Review Triage Log

Three independent review lenses (blind-hunter, edge-case-hunter, verification-gap) ran against the implementation.

**Patched (1, convergent across all three lenses):**
- `TransactionIntakeHandler.TryDeserializeResponse` uncaught `JsonException` on corrupt `ResponsePayload` — see Spec Change Log above.

**Deferred (see `deferred-work.md` for full detail):**
- The 202-Accepted-with-body-`Status: "Completed"` self-contradiction on the corrupt/null-payload fallback path (convergent: blind-hunter + edge-case-hunter). Not fixed — the frozen outcome model has no "corrupt data" outcome, and this path is expected to be unreachable in practice per AD-5's atomic-write guarantee.
- Seven single-lens findings from blind-hunter (unescaped `TransactionId` in `Location` header, missing `PartitionId` in log scopes, no route-parameter validation on `GET`, `DateTime`/`DateTimeOffset` asymmetry between `TransactionResponse`/`TransactionStatusResponse`, `Failed`-duplicate mapped to `400` client-fault semantics, `LogInformation` used for a terminal-failure event, no genuinely-concurrent race test) — all low severity, demo-scoped, or matching a documented precedent (kernel-level race-safety proof from story 4.1).

**Rejected (verification-gap, confirmed correct, no action):** null-body POST NRE risk was checked and found to route through the existing `ModelState.IsValid` / `[Required]` validation path rather than reaching `EnrichActivityWithRequest` unguarded; the controller's default `switch` arm (`throw` on an unmapped outcome) is dead code given the four-member enum and matches the existing pattern used elsewhere in this codebase; concurrency safety for `StoreIfNewAsync` was independently re-confirmed as race-safe via the kernel's unique-index-plus-catch implementation.

## Auto Run Result

**Summary:** Implemented `TransactionRequest`/`TransactionStatusResponse`, `IInboxMessageRepository`/`InboxMessageRepository` (extending the kernel's `InboxMessageRepositoryBase`), `ITransactionIntakeHandler`/`TransactionIntakeHandler` (dedupe-first intake logic per AD-4/AD-11), and a thin `TransactionsController`. Fixed the legacy `ModelState.IsValid` dead-code defect via `ApiBehaviorOptions.SuppressModelStateInvalidFilter = true`. One correctness bug found by review (uncaught `JsonException` on corrupt cached payload) was patched directly.

**Files changed:** see Code Map above, plus this spec file and `sprint-status.yaml`.

**Review findings breakdown:** 1 patched (high-confidence, convergent across all three lenses), 1 deferred (convergent, low severity, scope/reachability-limited), 7 deferred (single-lens, low severity) — see `## Review Triage Log` above.

**Verification performed:**
- `dotnet build CoreBankDemo.Messaging/CoreBankDemo.Messaging.csproj` — passed.
- `dotnet test CoreBankDemo.Rebuild.slnf` — passed; `CoreBankDemo.CoreBankAPI.Tests` 69/69 at 97.47% line / 88% branch / 100% method coverage; no regressions elsewhere.
- Direct read of every new/changed file against this spec's frozen Boundaries & Constraints and I/O matrix; confirmed `TransactionRequest.cs` is byte-for-byte identical to `git show 121e3b3^:CoreBankDemo.CoreBankAPI/Models/TransactionRequest.cs`.

**Residual risks:** The deferred 202/"Completed"-body inconsistency on the corrupt-payload fallback path remains, gated on AD-5's atomicity guarantee holding (tracked in `deferred-work.md`).
