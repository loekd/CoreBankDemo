---
title: 'Story 4.2: Transaction validation'
type: 'feature'
created: '2026-08-25'
status: 'done'
baseline_commit: '121e3b3ab88449af6803c079fbb8da0325a39052'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-4-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Business rejection (unknown account, inactive account, insufficient funds) must be deterministic, fully unit-tested pure logic (AD-2), not buried in a processor or controller (FR-12). Legacy `TransactionValidator.ValidateTransaction` covers unknown/inactive accounts and insufficient funds, but is missing two rules epics.md's frozen AC explicitly requires for this story: same-account transfer rejection and invalid-amount rejection — genuine brownfield gaps, not preserved-on-purpose legacy behavior.

**Approach:** Rebuild `TransactionValidator` as a pure, dependency-free `static class` with a single `static ValidationResult Validate(...)` method — it has no ports, no `TimeProvider`, no `ILogger<T>` to inject (nothing legacy's instantiated-via-DI version needed either; DI registration was ceremony this story drops since AD-2's "constructor-injected ports" guidance applies to classes that *have* dependencies). Decouple the input from `InboxMessage` (story 4.1's kernel-message-store DTO) — the validator takes primitive parameters (`fromAccountNumber`, `toAccountNumber`, `amount`) plus nullable `Account` snapshots, keeping it usable from any future caller without a forced dependency on the inbox message shape. Preserve the `ValidationResult(bool IsValid, string? Error)` record shape and `Success()`/`Failure(string)` factories exactly as legacy had them.

## Boundaries & Constraints

**Always:** `TransactionValidator` is a `static class`; `Validate` takes `(string fromAccountNumber, string toAccountNumber, decimal amount, Account? fromAccount, Account? toAccount)` and returns `ValidationResult`. Checks run in this fixed order, stopping at the first failure (fail-fast, one reason per call — never aggregated):
1. Same-account transfer: `fromAccountNumber == toAccountNumber` (ordinal) → `Failure("Cannot transfer to the same account")`
2. Invalid amount: `amount <= 0` → `Failure($"Invalid amount: {amount}. Amount must be greater than zero")`
3. Unknown/inactive source account: `fromAccount is null || !fromAccount.IsActive` → `Failure($"Source account {fromAccountNumber} not found or inactive")`
4. Unknown/inactive destination account: `toAccount is null || !toAccount.IsActive` → `Failure($"Destination account {toAccountNumber} not found or inactive")`
5. Insufficient funds: `fromAccount.Balance < amount` → `Failure($"Insufficient funds. Available: {fromAccount.Balance}, Required: {amount}")`
6. Otherwise → `Success()`

`ValidationResult` lives in the same file as `TransactionValidator` (matches legacy's co-location); `Success()`/`Failure(string)` factory methods preserved exactly.

**Ask First:** None — every design choice (static/pure vs. DI-instantiated, decoupling from `InboxMessage`, check ordering, the two new rules' exact wording) is resolved inline above with a documented rationale.

**Never:** Reach into `CoreBankDbContext`, any repository, or perform I/O of any kind — this is pure logic, testable with zero mocks beyond constructing `Account` snapshots directly (AD-2); validate currency match or any rule beyond the five scenarios named in epics.md's frozen AC (unknown/inactive accounts, insufficient funds, same-account transfer, invalid amounts) — do not invent additional business rules; touch `Account.cs`, `InboxMessage.cs`, `MessagingOutboxMessage.cs`, `CoreBankDbContext.cs`, or `DemoAccountSeeder.cs` (story 4.1, done); build `IAccountRepository`, `TransactionExecutor`, any controller, or DI registration for `TransactionValidator` (it's static — nothing to register) — those are stories 4.3+.

## I/O & Edge-Case Matrix

| Scenario | Input | Expected Output |
|----------|-------|------------------|
| Valid transfer | Distinct accounts, both active, sufficient funds, amount > 0 | `Success()` |
| Same-account transfer | `fromAccountNumber == toAccountNumber` | `Failure("Cannot transfer to the same account")` — checked even if both accounts are otherwise valid |
| Invalid amount, zero | `amount == 0m` | `Failure("Invalid amount: 0. Amount must be greater than zero")` |
| Invalid amount, negative | `amount == -5m` | `Failure("Invalid amount: -5. Amount must be greater than zero")` |
| Unknown source account | `fromAccount == null` | `Failure("Source account {fromAccountNumber} not found or inactive")` |
| Inactive source account | `fromAccount.IsActive == false` | `Failure("Source account {fromAccountNumber} not found or inactive")` |
| Unknown destination account | `toAccount == null` (source valid) | `Failure("Destination account {toAccountNumber} not found or inactive")` |
| Inactive destination account | `toAccount.IsActive == false` (source valid) | `Failure("Destination account {toAccountNumber} not found or inactive")` |
| Insufficient funds | `fromAccount.Balance < amount` (both accounts valid) | `Failure("Insufficient funds. Available: {Balance}, Required: {amount}")` |
| Exactly-sufficient funds | `fromAccount.Balance == amount` | `Success()` — boundary is inclusive, not an insufficient-funds failure |
| Check ordering: same-account wins over invalid amount | `fromAccountNumber == toAccountNumber` AND `amount <= 0` | `Failure("Cannot transfer to the same account")` — same-account is checked first |
| Check ordering: invalid amount wins over unknown account | `amount <= 0` AND `fromAccount == null` | `Failure("Invalid amount...")` — amount checked before account existence |
| Check ordering: source-unknown wins over destination-unknown | both `fromAccount` and `toAccount` are `null` | `Failure("Source account...")` — source checked before destination |

</frozen-after-approval>

## Code Map

- New: `CoreBankDemo.CoreBankAPI/Inbox/TransactionValidator.cs` — `static class TransactionValidator` + `record ValidationResult`
- New: `tests/CoreBankDemo.CoreBankAPI.Tests/TransactionValidatorTests.cs` — table-driven (`[Theory]`/`[InlineData]` or similar), no mocks beyond constructing `Account` snapshots directly
- Not touched: `Account.cs`, `Inbox/InboxMessage.cs`, `Outbox/MessagingOutboxMessage.cs`, `CoreBankDbContext.cs`, `DemoAccountSeeder.cs`, `Program.cs` (story 4.1, done)

## Tasks & Acceptance

**Execution:**
- [x] Tests first: table-driven coverage of every row in the I/O & Edge-Case Matrix, including the three check-ordering rows
- [x] `TransactionValidator.cs` (static class + `ValidationResult` record)

**Acceptance Criteria:**
- Given combinations of unknown/inactive accounts, insufficient funds, same-account transfer, and invalid amounts, when `TransactionValidator.Validate` runs, then each yields its specific failure reason; valid input yields success
- Given the frozen check order, when multiple violations are present simultaneously, then only the highest-priority violation's reason is returned (proven by the three ordering rows in the matrix)
- Given this story's own tests, `CoreBankDemo.CoreBankAPI.Tests` continues to clear the real ≥90% line coverage gate

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — expected: green; `CoreBankDemo.CoreBankAPI.Tests` clears the real ≥90% line threshold
- `dotnet build CoreBankDemo.Messaging/CoreBankDemo.Messaging.csproj` — expected: green, no source changes needed
