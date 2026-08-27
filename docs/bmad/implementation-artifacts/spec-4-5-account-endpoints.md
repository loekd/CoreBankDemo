---
title: 'Story 4.5: Account endpoints'
type: 'feature'
created: '2026-08-27'
status: 'done'
baseline_commit: '747728a56e95b63b6e35be392c44fc8cd9ed8044'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-4-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** PaymentsAPI depends on `POST /api/accounts/validate` for destination-account checks before forwarding, and operators/tests depend on `GET /api/accounts/{accountNumber}` for account-detail lookup (epic-4-context.md's cross-epic contract). Nothing in the rebuilt `CoreBankDemo.CoreBankAPI` exposes either endpoint yet — story 4.3 built `IAccountRepository`/`AccountRepository` for the execution path (row-locking reads used by `TransactionExecutor`), but nothing wires a read-only query surface to HTTP. Legacy's `AccountsController` also violates AD-2 (business logic — the lookup, the `IsValid` computation, and response-shape assembly — lives directly in the controller against an injected `CoreBankDbContext`), which this story does not preserve: the fix is a thin controller over a new handler, matching the pattern already established in story 4.4.

**Approach:** Recreate `AccountValidationRequest`, `AccountValidationResponse`, `AccountDetailsResponse` (frozen DataAnnotations/record shapes, byte-for-byte from `main`, confirmed identical to `git show 121e3b3^`). Add a new `AccountQueryHandler` — pure business logic (lookup via the existing internal `IAccountRepository.FindByAccountNumberAsync`, `IsValid` computation, response assembly), unit-tested with a mocked `IAccountRepository`, returning domain types only (never `IActionResult`). `AccountsController` stays thin: bind, check `ModelState` (already reachable — `ApiBehaviorOptions.SuppressModelStateInvalidFilter = true` was set globally in story 4.4's `Program.cs`), call the handler, map its result to an `IActionResult`. No new repository, no changes to `IAccountRepository`/`AccountRepository` (story 4.3, frozen) — `FindByAccountNumberAsync` is exactly the read this story needs; `LockForUpdateAsync` stays execution-only.

## Boundaries & Constraints

**Always:**
- `CoreBankDemo.CoreBankAPI/Models/AccountValidationRequest.cs`: recreated byte-for-byte from `git show 121e3b3^:CoreBankDemo.CoreBankAPI/Models/AccountValidationRequest.cs` (`AccountNumber` `[Required][StringLength(34, MinimumLength = 15)]`).
- `CoreBankDemo.CoreBankAPI/Models/AccountValidationResponse.cs`: recreated byte-for-byte from `git show 121e3b3^:CoreBankDemo.CoreBankAPI/Models/AccountValidationResponse.cs` (`record AccountValidationResponse(string AccountNumber, bool IsValid, string? AccountHolderName = null, decimal? Balance = null)`).
- `CoreBankDemo.CoreBankAPI/Models/AccountDetailsResponse.cs`: recreated byte-for-byte from `git show 121e3b3^:CoreBankDemo.CoreBankAPI/Models/AccountDetailsResponse.cs` (`record AccountDetailsResponse(string AccountNumber, string AccountHolderName, decimal Balance, string Currency, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt)`).
- `IAccountQueryHandler`/`AccountQueryHandler` (new, `CoreBankDemo.CoreBankAPI/Inbox/AccountQueryHandler.cs`): public interface (mirrors `ITransactionIntakeHandler` — `AccountsController` is a public MVC-discovered controller, so its constructor-injected dependency cannot be less accessible than the class itself), internal implementation constructor-injected with the existing internal `IAccountRepository` only (no new repository, no `CoreBankDbContext` dependency). Two methods:
  - `ValidateAsync(AccountValidationRequest request, CancellationToken)` → `AccountValidationResponse` directly (no wrapper needed — legacy's shape already carries every case: not-found and inactive both produce `IsValid = false` with null `AccountHolderName`/`Balance` when the account doesn't exist, or the real values when it does but is inactive). Logic: `FindByAccountNumberAsync(request.AccountNumber)`; `IsValid = account is not null && account.IsActive`; `new AccountValidationResponse(request.AccountNumber, IsValid, account?.AccountHolderName, account?.Balance)` — matches legacy exactly, including that an inactive account still surfaces its real `AccountHolderName`/`Balance` alongside `IsValid = false`.
  - `GetDetailsAsync(string accountNumber, CancellationToken)` → new `AccountDetailsResult` record (`bool Found`, `AccountDetailsResponse? Response`). `FindByAccountNumberAsync(accountNumber)`; not found → `Found = false`, `Response = null`; found → `Found = true` with `AccountDetailsResponse` built from the entity (`new DateTimeOffset(account.CreatedAt, TimeSpan.Zero)`; `account.UpdatedAt.HasValue ? new DateTimeOffset(account.UpdatedAt.Value, TimeSpan.Zero) : null` — same `DateTime`→`DateTimeOffset` (UTC) conversion legacy used).
- `AccountsController` (new, `api/accounts`, matching legacy's `[Route("api/[controller]")]`):
  - `POST validate` binds `AccountValidationRequest`; if `!ModelState.IsValid`, `BadRequest(new { Errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage) })` (conventions skill's exact shape, matches story 4.4); else call `ValidateAsync` and `Ok(response)` — always 200, even when `IsValid` is `false` (legacy never 4xx's a "not valid" outcome — validity is reported *in* the 200 body, not via status code).
  - `GET {accountNumber}` — route parameter keeps `[StringLength(34, MinimumLength = 15, ErrorMessage = "AccountNumber must be between 15 and 34 characters")]` directly on the route-bound parameter (legacy's exact placement, byte-for-byte messages); if `!ModelState.IsValid`, same `BadRequest(new { Errors })` shape; else call `GetDetailsAsync` — not found → `NotFound(new { Errors = new[] { $"Account {accountNumber} not found" } })` (legacy's exact message); found → `Ok(response)`.
- `Program.cs`: add DI registrations for `IAccountQueryHandler`→`AccountQueryHandler` (`AddScoped`, matching the existing Scoped `IAccountRepository`/`AccountRepository` registration pattern from story 4.3 — check whether `IAccountRepository` itself needs a registration added in this story, since story 4.3 built the type but story 4.3's spec's Never-list forbade *this* story's controller-facing wiring, not `IAccountRepository`'s own DI entry; if `IAccountRepository` has no existing `Program.cs` registration, add both). No new `ActivitySource`, no hosted service, no changes to intake wiring from story 4.4.

**Ask First:** None — every decision (handler split, `AccountDetailsResult`'s shape, always-200 on `validate` regardless of `IsValid`, reuse of `IAccountRepository.FindByAccountNumberAsync` instead of a new repository) is resolved inline above with a documented rationale.

**Never:** Touch `Account.cs`, `CoreBankDbContext.cs`, `Inbox/AccountRepository.cs` (`IAccountRepository`/`AccountRepository`, story 4.3, done, frozen — `LockForUpdateAsync` stays execution-only, unused by this story), `Inbox/InboxMessage.cs`, `Inbox/InboxMessageRepository.cs`, `Inbox/TransactionIntakeHandler.cs`, `Controllers/TransactionsController.cs`, `Models/TransactionRequest.cs`, `Models/TransactionResponse.cs`, `Models/TransactionStatusResponse.cs`, `Inbox/TransactionValidator.cs`, `Inbox/TransactionExecutor.cs`, `DemoAccountSeeder.cs`, `Outbox/MessagingOutboxMessage.cs` (stories 4.1–4.4, done, frozen). Register `ITransactionExecutor`'s consumer, `InboxProcessor`, or `IEventPublisher` in `Program.cs` — those are stories 4.6/4.7. Add row-locking or any write path to the new handler — these endpoints are read-only.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output |
|----------|--------------|------------------|
| `POST validate`, account exists and active | valid `AccountNumber` | `200 Ok` with `AccountValidationResponse(AccountNumber, true, AccountHolderName, Balance)` |
| `POST validate`, account exists but inactive | `IsActive = false` | `200 Ok` with `AccountValidationResponse(AccountNumber, false, AccountHolderName, Balance)` — real holder/balance still surfaced, per legacy |
| `POST validate`, account does not exist | no matching row | `200 Ok` with `AccountValidationResponse(AccountNumber, false, null, null)` |
| `POST validate`, invalid request (DataAnnotations violation) | e.g. `AccountNumber` too short | `400 BadRequest(new { Errors })` listing every violation, handler never called |
| `GET {accountNumber}`, account exists | valid route value | `200 Ok` with full `AccountDetailsResponse` |
| `GET {accountNumber}`, account does not exist | no matching row | `404 NotFound(new { Errors = [$"Account {accountNumber} not found"] })` |
| `GET {accountNumber}`, route value fails `[StringLength(34, MinimumLength = 15)]` | too short/long | `400 BadRequest(new { Errors })` — handler never called |

</frozen-after-approval>

## Code Map

- New: `CoreBankDemo.CoreBankAPI/Models/AccountValidationRequest.cs`, `CoreBankDemo.CoreBankAPI/Models/AccountValidationResponse.cs`, `CoreBankDemo.CoreBankAPI/Models/AccountDetailsResponse.cs`
- New: `CoreBankDemo.CoreBankAPI/Inbox/AccountQueryHandler.cs` (`IAccountQueryHandler` + `AccountQueryHandler` + `AccountDetailsResult`)
- New: `CoreBankDemo.CoreBankAPI/Controllers/AccountsController.cs`
- Modify: `CoreBankDemo.CoreBankAPI/Program.cs` — DI for `IAccountQueryHandler` (and `IAccountRepository` if not already registered)
- New: `tests/CoreBankDemo.CoreBankAPI.Tests/AccountQueryHandlerTests.cs` (Moq against `IAccountRepository`, tier 1, covering every I/O matrix row), `tests/CoreBankDemo.CoreBankAPI.Tests/AccountsControllerTests.cs` (thin — `ModelState` invalid → `BadRequest` shape; valid → delegates to a mocked handler and maps found/not-found correctly)
- Not touched: `Account.cs`, `CoreBankDbContext.cs`, `Inbox/AccountRepository.cs`, `Inbox/InboxMessage.cs`, `Inbox/InboxMessageRepository.cs`, `Inbox/TransactionIntakeHandler.cs`, `Controllers/TransactionsController.cs`, `Models/TransactionRequest.cs`, `Models/TransactionResponse.cs`, `Models/TransactionStatusResponse.cs`, `DemoAccountSeeder.cs` (stories 4.1–4.4, done)

## Tasks & Acceptance

**Execution:**
- [x] `Models/AccountValidationRequest.cs`, `Models/AccountValidationResponse.cs`, `Models/AccountDetailsResponse.cs`
- [x] Tests first: `AccountQueryHandler` (Moq tier 1) covering every I/O matrix row
- [x] `Inbox/AccountQueryHandler.cs`
- [x] `Controllers/AccountsController.cs` + its own thin mapping tests
- [x] `Program.cs` wiring (DI for `IAccountQueryHandler`/`IAccountRepository`)

**Acceptance Criteria:**
- Given `POST /api/accounts/validate` with an existing active account, then `200 Ok` reports `IsValid = true` with holder/balance; inactive or missing accounts report `IsValid = false` (missing → null holder/balance, inactive → real holder/balance) — always `200`, never a 4xx for a "not valid" business outcome
- Given `GET /api/accounts/{accountNumber}` with an existing account, then `200 Ok` returns full details; a missing account returns `404 NotFound(new { Errors })`; a malformed route value returns `400 BadRequest(new { Errors })`
- Given the controller, it contains no business logic — proven by handler-level unit tests (mocked repository) plus thin controller-mapping tests
- No regressions: full `CoreBankDemo.Rebuild.slnf` suite stays green with ≥90% line coverage on `CoreBankDemo.CoreBankAPI.Tests`

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — passed; `CoreBankDemo.CoreBankAPI.Tests` 91/91 at 97.87% line / 90.1% branch / 100% method (≥90% line gate met); no regressions in `Messaging.Tests` (153/153), `ServiceDefaults.Tests` (117/117), `PaymentsAPI.Tests` (1/1)
- Byte-for-byte diff of all three recreated model files against `git show 121e3b3^:CoreBankDemo.CoreBankAPI/Models/<name>.cs` — identical (exit code 0, zero output) for `AccountValidationRequest.cs`, `AccountValidationResponse.cs`, `AccountDetailsResponse.cs`
- `git diff CoreBankDemo.CoreBankAPI/Program.cs` — confirmed the only change is the two new `AddScoped` registrations plus an explanatory comment; no other line touched
- `git status --short` / `git diff --stat HEAD` — confirmed no file on the spec's Never-list was created, modified, or deleted

## Spec Change Log

- Added `tests/CoreBankDemo.CoreBankAPI.Tests/AccountValidationAttributeTests.cs` post-implementation (not in the original Code Map): the review panel's `AccountsControllerTests` only ever hand-injects `ModelState.AddModelError(...)` to simulate an invalid request, so nothing in the original test set actually exercised the real `[Required]`/`[StringLength(34, MinimumLength = 15)]` attributes at their boundary values (14/15/34/35 chars) — an off-by-one drift in either bound would have passed CI silently. Fixed by adding reflection-based tests that pull the real `ValidationAttribute` instances directly (from the `AccountValidationRequest` primary-constructor parameter, and from the `GetAccountDetails` route parameter) and invoke `.IsValid(...)` on them at each boundary. Discovered along the way: C# attaches attributes written on a positional record's constructor parameter to the *parameter*, not the synthesized property (confirmed empirically — the property carries zero validation attributes), so a naïve `Validator.TryValidateObject(...)`-based test would have silently reported every input as valid; the working test reflects over the constructor parameter instead, matching how ASP.NET Core's own model-metadata provider actually resolves record validation.

## Review Triage Log

Three independent review lenses (blind-hunter, edge-case-hunter, verification-gap) ran against the implementation. Blind-hunter additionally spun up an isolated ASP.NET Core probe app to empirically test two hypotheses (a null JSON body on `POST validate`, and a too-short `GetDetails` route value) rather than relying on static reading alone.

**Patched (1, convergent across two lenses):**
- No test proved the `[StringLength(34, MinimumLength = 15)]` boundary against the real validation-attribute instances (blind-hunter + edge-case-hunter, both independently flagged the same root gap: every "invalid" test hand-fakes `ModelState` instead of exercising real attributes). Fixed — see Spec Change Log above.

**Rejected (empirically disproven):**
- Edge-case-hunter's highest-severity claim — a literal JSON `null` body on `POST validate` would throw an unhandled `NullReferenceException` (500) because `[Nullable]` annotations are "compile-time only and erased at runtime" — was directly refuted by blind-hunter's live HTTP probe against an equivalently-configured ASP.NET Core app: a `null`/empty body correctly returns `400` ("A non-empty request body is required."). `[Nullable]` metadata is in fact preserved at runtime via compiler-emitted attributes, and `[ApiController]`'s implicit-required inference for non-nullable `[FromBody]` reference-type parameters uses exactly that metadata — the same mechanism already verified for `TransactionRequest`/`TransactionsController` in story 4.4's review. A short/malformed `GetDetails` route value was likewise empirically confirmed to correctly 400 rather than bypass validation.

**Deferred (see `deferred-work.md` for full detail):**
- Whole-service lack of authentication/authorization/rate-limiting enabling account/balance enumeration (convergent: blind-hunter + edge-case-hunter, same underlying exposure from two angles). Not fixed — inherited, frozen, byte-for-byte legacy behavior; adding auth is a genuine external-behavior change out of scope for this rebuild story.
- A bundle of 8 single-lens, low-severity findings (missing `ILogger<T>` in `AccountQueryHandler`; `validate`/`GetDetails` divergence on whitespace-only input; case-sensitive/untrimmed lookups; route-character edge cases; a latent `DateTimeKind.Local` throw path; no `Balance` scale normalization; UTF-16 surrogate-pair length-counting; one redundant controller test) — none crossed the ≥2-lens convergence bar, all inherited-from-legacy, by-design, or cosmetic.

## Auto Run Result

**Summary:** Implemented `AccountValidationRequest`/`AccountValidationResponse`/`AccountDetailsResponse` (byte-for-byte legacy recreations), `IAccountQueryHandler`/`AccountQueryHandler` (read-only business logic over the existing frozen `IAccountRepository`), and a thin `AccountsController` — fixing legacy's AD-2 violation (business logic previously lived directly in the controller against an injected `DbContext`). Registered `IAccountRepository` in DI for the first time (built in story 4.3, never wired) alongside the new handler. One test-coverage gap found by review (convergent across two lenses — validation attributes never exercised at their real boundary values) was patched directly with reflection-based boundary tests, which also surfaced a subtle C#-records fact (validation attributes on positional-record parameters attach to the parameter, not the property).

**Files changed:** see Code Map above, plus this spec file, `deferred-work.md`, and `sprint-status.yaml`.

**Review findings breakdown:** 1 patched (convergent, high-confidence), 1 rejected (empirically disproven via live HTTP probe), 1 deferred-convergent (no-auth enumeration, frozen/out-of-scope), 8 deferred single-lens (low severity) — see `## Review Triage Log` above.

**Verification performed:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — passed; `CoreBankDemo.CoreBankAPI.Tests` 91/91 at 97.87% line / 90.1% branch / 100% method coverage; no regressions elsewhere.
- Direct read of every new/changed file against this spec's frozen Boundaries & Constraints and I/O matrix; confirmed all three model files are byte-for-byte identical to `git show 121e3b3^`.
- Confirmed via `git status`/`git diff --stat` that no Never-list file was touched.

**Residual risks:** The no-auth account/balance enumeration surface remains (tracked in `deferred-work.md`), gated on this being demo code never deployed to production.

