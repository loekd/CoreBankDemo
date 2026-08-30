---
title: 'Fix PaymentsAPI HTTP demo account mismatch'
type: 'bugfix'
created: '2026-08-30'
status: 'done'
review_loop_iteration: 0
baseline_commit: '6cbce32b2e3a734e4143ba9ee8d2deca9b9f9bc3'
context:
  - '{project-root}/docs/bmad/constraints.md'
  - '{project-root}/.claude/skills/messaging-patterns/SKILL.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The PaymentsAPI REST Client example submits a payment between load-test-only accounts while targeting the regular AppHost. Payment intake succeeds with `202 Accepted`, but CoreBankAPI correctly reports the destination as invalid; the outbox retries five times and becomes terminally `Failed`. This makes a healthy real outbox pipeline appear broken immediately after running the repository-provided request.

**Approach:** Make the PaymentsAPI `.http` example self-consistent with the regular AppHost by using its seeded demo accounts and a fresh, clearly named fixed idempotency key. Preserve the repeated-request deduplication demonstration while avoiding the already persisted failed row created by the old key.

## Boundaries & Constraints

**Always:** Keep the request pointed at the regular PaymentsAPI endpoint on port 5294. Use account numbers seeded by `DemoAccountSeeder`. Keep a fixed idempotency key so rerunning the same request continues to demonstrate exactly-once intake. Verify the corrected request through the running real AppHost and confirm the persisted outbox row reaches `Completed` without retries.

**Ask First:** Any change to account seeding, outbox retry semantics, validation behavior, API contracts, or AppHost topology.

**Never:** Seed load-test accounts in the regular AppHost, weaken destination-account validation, treat `IsValid=false` as successful delivery, delete or mutate the existing failed outbox row, or alter processor/kernel behavior to compensate for invalid demo input.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Regular AppHost request | Seeded source and destination accounts with the new fixed key | PaymentsAPI returns `202`; outbox reaches `Completed`; CoreBank balances change once | No retry or terminal failure |
| Repeated request | Same body and fixed key submitted again | Existing payment is returned and no second CoreBank transaction is created | Existing idempotency behavior is preserved |
| Legacy failed request | Old `test-idempotency-key-12345` row remains in the database | Corrected example does not collide with it | No destructive cleanup |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.PaymentsAPI/CoreBankDemo.http:1-13` -- faulty manual request: targets regular PaymentsAPI but uses `NL01LOAD...`/`NL02LOAD...` and the key already persisted as failed.
- `CoreBankDemo.CoreBankAPI/DemoAccountSeeder.cs:31-61` -- authoritative regular-AppHost accounts; use `NL91ABNA0417164300` and `NL20INGB0001234567`.
- `CoreBankDemo.LoadTestSupport/Program.cs:32-69` -- proves `NL01LOAD...` through `NL10LOAD...` are seeded only when LoadTestSupport runs.
- `CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs:38-73` -- read-only evidence that `IsValid=false` intentionally becomes a delivery failure.
- `CoreBankDemo.PaymentsAPI/Handlers/PaymentStorageHandler.cs:40-95` -- read-only evidence that duplicate keys return the persisted winner, so retaining the old key would retain the failed result.

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.PaymentsAPI/CoreBankDemo.http` -- replace load-test-only accounts with regular seeded accounts and replace the contaminated fixed key with a descriptive fresh key.
- [x] Running regular AppHost -- execute the corrected request, query the persisted outbox record, and repeat the request to prove completion plus idempotency.

**Acceptance Criteria:**
- Given the regular AppHost and its standard three seeded CoreBank accounts, when the corrected `.http` payment request is submitted, then its outbox row reaches `Completed` with `RetryCount = 0` and no `LastError`.
- Given that completed request, when the same request and idempotency key are submitted again, then no additional outbox row or CoreBank transaction is created.
- Given the existing terminally failed row for `test-idempotency-key-12345`, when the corrected example is used, then it remains untouched and cannot affect the new request.

## Spec Change Log

- 2026-08-30: Updated the REST Client payment to use regular seeded accounts and `regular-apphost-demo-payment-v1`; verified completion, deduplication, balance effects, and isolation from the legacy failed row against the running regular AppHost.

## Design Notes

The live AppHost established the fault boundary: a fresh payment using `NL91ABNA0417164300` to `NL20INGB0001234567` completed with zero retries, while the `.http` request produced a row whose `LastError` was `Destination account 'NL02LOAD0000000002' failed validation (IsValid=false).` The correct fix is therefore the executable example, not the tested outbox implementation.

## Verification

**Commands:**
- Submit the updated request to `http://127.0.0.1:5294/api/payments` from inside the devcontainer -- expected: `202 Accepted`.
- Query `paymentsdb."OutboxMessages"` by the new idempotency key -- expected: one `Completed` row, zero retries, null `LastError`.
- Submit the request again and query both outbox and CoreBank transaction records -- expected: row counts remain one.

**Result (2026-08-30):**
- The corrected request returned `202 Accepted`; `regular-apphost-demo-payment-v1` reached `Completed` with `RetryCount = 0` and null `LastError`.
- Repeated submissions returned the original completed payment. Payments outbox and CoreBank inbox counts remained one, and balances remained `4999.00` / `10001.00` after the first transfer.
- The legacy key remained a separate single `Failed` row with `RetryCount = 5` and the original invalid-destination error.

## Suggested Review Order

- The self-contained demo request now targets seeded accounts with an uncontaminated deduplication key.
  [`CoreBankDemo.http:1`](../../../CoreBankDemo.PaymentsAPI/CoreBankDemo.http#L1)
