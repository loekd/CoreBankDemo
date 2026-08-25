---
title: 'Story 4.3: Account repository and transaction executor'
type: 'feature'
created: '2026-08-25'
status: 'done'
baseline_revision: '05ae7c35593953299372cc55f019d4fb7be82e88'
baseline_commit: '05ae7c35593953299372cc55f019d4fb7be82e88'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-4-context.md'
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** Ledger money movement (FR-11) has no home yet. `TransactionValidator` (story 4.2) only decides pass/fail from `Account` snapshots it is handed — nothing loads those snapshots with the row-level locking AD-5/AD-9 require, and nothing applies the debit/credit arithmetic or builds the frozen response payload.

**Approach:** Add a minimal `IAccountRepository` port with a logic-free, Postgres-only `SELECT ... FOR UPDATE` pass-through (individually `[ExcludeFromCodeCoverage]`, proven only by the k6/Postgres tier) plus a provider-agnostic plain-query method exercised on SQLite. Add `ITransactionExecutor`/`TransactionExecutor`: it locks both accounts in alphabetical account-number order (deadlock avoidance), runs `TransactionValidator.Validate`, and on success mutates the tracked `Account` entities' balances in memory and returns the frozen `TransactionResponse` shape (recreated verbatim from `main` — deleted at epic start, untouched until now); on failure it returns the same frozen shape plus the validator's error text, without touching balances. It does not call `SaveChangesAsync` or touch `InboxMessage`/`Status` — atomic commit and event enqueue are story 4.6's job.

## Boundaries & Constraints

**Always:** `IAccountRepository` has `Task<Account?> LockForUpdateAsync(string accountNumber, CancellationToken ct)` (raw `FromSqlInterpolated` pass-through: `SELECT * FROM "Accounts" WHERE "AccountNumber" = {accountNumber} FOR UPDATE`, no branching, individually `[ExcludeFromCodeCoverage(Justification = "...")]`) and `Task<Account?> FindByAccountNumberAsync(string accountNumber, CancellationToken ct)` (plain `dbContext.Accounts.FirstOrDefaultAsync`, provider-agnostic, covered by SQLite tests). `ITransactionExecutor.ExecuteAsync(string fromAccountNumber, string toAccountNumber, decimal amount, string transactionId, CancellationToken ct)` returns a new internal `TransactionExecutionResult(bool Success, TransactionResponse Response, string? ErrorReason, decimal? NewFromBalance, decimal? NewToBalance)` (not a wire contract — free to design). `TransactionExecutor` is constructor-injected with `IAccountRepository` and `TimeProvider` only (AD-2). Lock order: compare `fromAccountNumber`/`toAccountNumber` ordinally, lock the alphabetically-first account number first via `LockForUpdateAsync`, always call it exactly once per distinct account number (same-account transfers lock once, not twice). Always call `TransactionValidator.Validate` with the loaded snapshots before mutating anything. On success: `fromAccount.Balance -= amount`, `toAccount.Balance += amount`, both `UpdatedAt` set from `timeProvider.GetUtcNow()`; `Response = new TransactionResponse(transactionId, MessageConstants.Status.Completed, processedAt)`. On failure: no balance/`UpdatedAt` mutation; `Response = new TransactionResponse(transactionId, MessageConstants.Status.Failed, processedAt)`, `ErrorReason` = the validator's error text (verbatim, matching legacy's reuse of the transport status literals as the wire-level business-status string — the frozen `TransactionResponse.Status` field, not `InboxMessage.Status`). `TransactionResponse(string TransactionId, string Status, DateTimeOffset ProcessedAt)` is recreated in `CoreBankDemo.CoreBankAPI/Models/TransactionResponse.cs`, matching `main`'s deleted shape byte-for-byte (verified against git history at commit `121e3b3^`).

**Block If:** None — every open question (result-type shape, lock-order tie-break, response literal on failure) is resolved above with a documented rationale.

**Never:** Call `SaveChangesAsync`, touch `InboxMessage`/`MessagingOutboxMessage`/message `Status`, or enqueue any outbox event — that is story 4.6 (`InboxProcessorBase` execution handler + atomic commit); validate currency, add new business rules beyond what `TransactionValidator` already checks, or duplicate its check logic; build controllers, DI registration beyond the two new classes, or `TransactionRequest`/account endpoints — those are stories 4.4/4.5; use `ILogger<T>` (not needed — no branching worth logging at this layer); touch `Account.cs`, `CoreBankDbContext.cs`, `InboxMessage.cs`, `MessagingOutboxMessage.cs`, `DemoAccountSeeder.cs`, or `TransactionValidator.cs` (done, frozen).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Valid transfer, `from < to` alphabetically | Distinct active accounts, sufficient funds | `from` locked first, then `to`; `Success=true`; balances debited/credited exactly once; `Response.Status = Completed` | N/A |
| Valid transfer, `from > to` alphabetically | Distinct active accounts, sufficient funds | `to` locked first, then `from` (order still alphabetical by account number, not by from/to role); same balance/response outcome | N/A |
| Same-account transfer | `fromAccountNumber == toAccountNumber` | Account locked exactly once (not twice); validator's same-account failure returned; no balance change | `ErrorReason = "Cannot transfer to the same account"` |
| Any other `TransactionValidator` failure (unknown/inactive account, invalid amount, insufficient funds) | Per story 4.2's matrix | `Success=false`; `Response.Status = Failed`; no balance/`UpdatedAt` mutation on either account | `ErrorReason` = validator's exact message |
| `IAccountRepository.FindByAccountNumberAsync`, account exists | Known account number | Returns the tracked `Account` | N/A |
| `IAccountRepository.FindByAccountNumberAsync`, unknown account number | No matching row | Returns `null` | N/A |

</intent-contract>

## Code Map

- New: `CoreBankDemo.CoreBankAPI/Inbox/AccountRepository.cs` -- `IAccountRepository` + `AccountRepository` (`LockForUpdateAsync` FOR UPDATE pass-through, individually `[ExcludeFromCodeCoverage]`; `FindByAccountNumberAsync` provider-agnostic). Legacy reference for the FOR UPDATE SQL shape only: `git show 121e3b3^:CoreBankDemo.CoreBankAPI/Inbox/TransactionExecutor.cs` (`LoadAccountsAsync`, now split/simplified — no `.Local` re-fetch needed since the new method returns the tracked entity directly).
- New: `CoreBankDemo.CoreBankAPI/Inbox/TransactionExecutor.cs` -- `ITransactionExecutor` + `TransactionExecutor` + `TransactionExecutionResult` record. Calls `CoreBankDemo.CoreBankAPI.Inbox.TransactionValidator.Validate` (story 4.2, `CoreBankDemo.CoreBankAPI/Inbox/TransactionValidator.cs`, unchanged) and reuses `CoreBankDemo.Messaging.MessageConstants.Status.Completed`/`Failed` literals.
- New: `CoreBankDemo.CoreBankAPI/Models/TransactionResponse.cs` -- frozen `record TransactionResponse(string TransactionId, string Status, DateTimeOffset ProcessedAt)`, recreated verbatim from `git show 121e3b3^:CoreBankDemo.CoreBankAPI/Models/TransactionResponse.cs`.
- New: `tests/CoreBankDemo.CoreBankAPI.Tests/TransactionExecutorTests.cs` -- Moq against `IAccountRepository` (tier 1, AD-9), covering every I/O matrix row for the executor.
- New: `tests/CoreBankDemo.CoreBankAPI.Tests/AccountRepositoryTests.cs` -- `FindByAccountNumberAsync` on SQLite via existing `SqliteCoreBankApiTestBase` (`tests/CoreBankDemo.CoreBankAPI.Tests/CoreBankApiTestSupport.cs`, tier 2, AD-9); `LockForUpdateAsync` is not exercised here (Postgres-only, excluded per AD-9).
- Not touched: `Account.cs`, `CoreBankDbContext.cs`, `Inbox/InboxMessage.cs`, `Outbox/MessagingOutboxMessage.cs`, `DemoAccountSeeder.cs`, `Inbox/TransactionValidator.cs`, `Program.cs` (no DI registration needed yet — first consumer is story 4.6's `InboxProcessor`).

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.CoreBankAPI/Models/TransactionResponse.cs` -- recreate the frozen record -- required by both the executor and later stories' controllers/cached payloads
- [x] `CoreBankDemo.CoreBankAPI/Inbox/AccountRepository.cs` -- add `IAccountRepository`/`AccountRepository` -- provides the locked/unlocked load paths AD-9's tiering requires
- [x] `tests/CoreBankDemo.CoreBankAPI.Tests/AccountRepositoryTests.cs` -- SQLite tests for `FindByAccountNumberAsync` (found/not-found) -- tier 2 coverage for the provider-agnostic path
- [x] `CoreBankDemo.CoreBankAPI/Inbox/TransactionExecutor.cs` -- add `ITransactionExecutor`/`TransactionExecutor`/`TransactionExecutionResult` -- pure orchestration of locking, validation, and balance mutation
- [x] `tests/CoreBankDemo.CoreBankAPI.Tests/TransactionExecutorTests.cs` -- table-driven Moq-based tests covering every I/O matrix row -- tier 1 coverage, no real DB needed

**Acceptance Criteria:**
- Given an `IAccountRepository` port with a `FOR UPDATE` pass-through (logic-free, individually coverage-excluded per AD-9) and a provider-agnostic load path used in tests, when `TransactionExecutor.Execute` runs with mocked/SQLite accounts, then accounts lock in alphabetical order, debit and credit apply exactly once, the cached `ResponsePayload` (frozen `TransactionResponse` shape) is produced, and validation failure produces a failure payload without touching balances
- Given this story's own tests, executor logic reaches ≥90% coverage without Postgres (the excluded `LockForUpdateAsync` pass-through does not count against the gate)

## Spec Change Log

## Review Triage Log

## Design Notes

Lock-order tie-break: "alphabetical order" is by account number string (ordinal comparison), independent of which side is `from`/`to` — this matches the legacy executor's own comment ("lock accounts in consistent alphabetical order to prevent deadlocks") and story 4.2's ordinal same-account check. For a same-account transfer, only one `LockForUpdateAsync` call happens (deduping by account number) since locking the same row twice is redundant, not incorrect, but the test matrix asserts the call count to guard against an accidental double-lock regression.

The result type `TransactionExecutionResult` is intentionally not a wire contract (AD-12 only freezes HTTP/event payloads) — it exists purely to hand story 4.6's future `InboxProcessor` handler everything it needs (`NewFromBalance`/`NewToBalance` for the two `BalanceUpdated` events, `ErrorReason` for the `TransactionFailed` event and no `InboxMessage` field is touched here).

## Verification

**Commands:**
- `dotnet build CoreBankDemo.Messaging/CoreBankDemo.Messaging.csproj --no-restore` -- passed
- `dotnet test CoreBankDemo.Rebuild.slnf --no-restore` -- passed; `CoreBankDemo.CoreBankAPI.Tests` reached 98.19% line coverage

## Suggested Review Order

**Execution flow**

- Orchestrates ordered locking, validation, and frozen success/failure payload creation.
  [`TransactionExecutor.cs:16`](../../../CoreBankDemo.CoreBankAPI/Inbox/TransactionExecutor.cs#L16)

- Restores the exact response contract later stories will cache and return.
  [`TransactionResponse.cs:1`](../../../CoreBankDemo.CoreBankAPI/Models/TransactionResponse.cs#L1)

**Persistence seam**

- Splits provider-agnostic lookup from the excluded Postgres `FOR UPDATE` pass-through.
  [`AccountRepository.cs:6`](../../../CoreBankDemo.CoreBankAPI/Inbox/AccountRepository.cs#L6)

- Exposes internals to the test assembly and Moq's proxy generator only.
  [`CoreBankDemo.CoreBankAPI.csproj:28`](../../../CoreBankDemo.CoreBankAPI/CoreBankDemo.CoreBankAPI.csproj#L28)

**Verification**

- Proves both alphabetical lock-order success paths plus all validator-driven failures.
  [`TransactionExecutorTests.cs:67`](../../../tests/CoreBankDemo.CoreBankAPI.Tests/TransactionExecutorTests.cs#L67)

- Verifies the provider-agnostic repository path on SQLite, including tracked-entity behavior.
  [`AccountRepositoryTests.cs:7`](../../../tests/CoreBankDemo.CoreBankAPI.Tests/AccountRepositoryTests.cs#L7)
