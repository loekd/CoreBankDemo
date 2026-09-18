# ADR-023: CoreBank is the only source of payment outcomes; infrastructure failures retry without limit

**Date:** 2026-09-18
**Status:** Proposed
**Deciders:** Architecture team
**Supersedes in part:**
- Story 5.4's boundary "never skip the destination-account validation call before submission" — the call is removed.
- ADR-005's fault target `/api/accounts/validate` — the checked-in errors file targets `/api/transactions/process`.
- The kernel's retry limit (`MessageConstants.Defaults.MaxRetryCount`, terminal `Failed` at the limit) — removed for all four stores.
- ADR-020's cancel matrix row "`Processing` / `Failed` → `409`" — a CoreBank inbox row can no longer become `Failed`; the row stays valid for databases that still hold such rows.

Implementation spec: [`2026-09-18-corebank-sole-outcome-source-design.md`](../superpowers/specs/2026-09-18-corebank-sole-outcome-source-design.md).

## Context

On 2026-09-17 a DemoRunner burst under a Dev Proxy throttle (10 requests per 60 s) reported four
payments as `still moving` indefinitely. PaymentsAPI had been throttled on its
`POST /api/accounts/validate` call, exhausted five retries within about a second, and marked the
outbox rows `Failed`. CoreBank never received the commands and therefore published nothing. Only
CoreBank publishes outcome events, so neither the `202` caller nor the console was ever told. 76
development rows ended this way, all on the validation call.

This already contradicted `docs/constraints.md` invariants 2 and 4; the load tests run without
throttling and never exposed it.

The obvious repair — PaymentsAPI publishes a `payment.failed` event — was rejected. CoreBank
publishes outcomes through a transactional outbox; a second publisher would give a transaction's
outcome two sources that can disagree. The disagreement is not hypothetical: the same retry limit
guarded the submission call, so a Payments row could end `Failed` while CoreBank had committed and
published `Completed`.

## Decision

**Only CoreBank decides and announces how a payment ends.** PaymentsAPI either delivers the command
or keeps trying.

1. **No pre-validation.** PaymentsAPI submits the transaction directly. The validation call checked
   nothing CoreBank does not check again at execution, could not produce a correct outcome for an
   invalid account (no `TransactionSubmission` to complete with), doubled the proxied calls per
   attempt, and was where every silent failure occurred. CoreBankAPI keeps the endpoint; it is part
   of the external contract.
2. **Infrastructure failures retry without limit**, in the Payments outbox, the Payments inbox, the
   CoreBank inbox and the CoreBank event outbox. `MarkAsFailedWithRetryAsync` always returns the row
   to `Pending`. The kernel never writes `Failed`. There is no kernel backoff: the poll interval is
   the retry delay, and PaymentsAPI's HTTP calls already run through the standard resilience
   handler's exponential backoff and circuit breaker.
3. **Strict ordering under faults.** A batch stops at its first failed row and releases the rows it
   had claimed behind it (`ReleaseClaimsAsync`, `RetryCount` untouched). Previously the batch carried
   on, so a later row could complete ahead of a failed one whenever faults were on — a hole in
   invariant 5 that the fault-free load tests never showed.
4. **`400` on submission is a verdict.** PaymentsAPI does not retry it: the row ends `Completed`
   with a `Failed` payload, the same shape an execution-time rejection already has. Every other
   failure — `429`, `408`, `401`, `403`, `404`, `5xx`, timeouts, transport exceptions — is retried.
5. **CoreBank answers `400` only after recording the rejection.** A terminal inbox row and a
   `transaction.failed` outbox row are committed in one save, modelled on the cancel tombstone. If
   the save fails the answer is `503`. A request whose `TransactionId` is itself unusable cannot be
   recorded and gets a plain `400`; PaymentsAPI cannot produce one.
6. **Internal failures are not bad requests.** The lost-store-race path on `process` and
   `StoreFailed` on `cancel` answer `503` instead of `400`.

## Alternatives considered

- **PaymentsAPI publishes the failure.** Two sources of truth for one transaction; rejected.
- **Bounded retries, then cancel through CoreBank's cancel endpoint.** Keeps CoreBank authoritative
  and bounds the wait, but a short blip still ends payments, and it reintroduces give-up logic.
  Rejected in favour of never giving up.
- **Overtake a failing row** instead of blocking its partition. Better throughput, but weakens
  invariant 5 to "ordered unless retried". Rejected: the system's claim is ordering *under faults*.
- **Stored backoff** (`HoldUntil` reuse or a `NextAttemptAt` column). Unnecessary once a batch stops
  at its first failure; the resilience handler already backs off. `EnsureCreated`-only databases
  would also have had to be recreated for a new column.
- **Treat `401`/`403` as permanent.** CoreBank has no authentication and cannot produce them; a
  response like that comes from something in between, so CoreBank could never record or publish the
  rejection. Left in the retry bucket.

## Consequences

- An accepted payment can no longer end without an outcome: it is delivered, rejected by CoreBank
  (`transaction.failed`), or cancelled by CoreBank (`transaction.cancelled`).
- **A row that can never succeed blocks its partition until someone intervenes.** Previously it was
  skipped after five tries. It is visible — a warning per attempt, a climbing `retry_scheduled`
  count — but nothing gives up automatically. An operator can end a stuck payment through CoreBank's
  existing cancel endpoint.
- A `transaction.failed` event now has two causes: a rejection at execution and a rejection at the
  door. Consumers already treat the event as "rejected"; the reason text tells them apart.
- An unknown destination account on the instant rail now answers `200 Failed` instead of
  `504 Cancelled`, which closes the corresponding `docs/backlog.md` item.
- Non-HTTP retry paths (CoreBank's database, Dapr publish) retry once per partition per poll tick
  with no backoff. Accepted for a demo; an in-memory per-partition pause can be added without a
  schema change.
- `ItemOutcome.TerminalFailed` disappears from the metric's attribute set. Rows already `Failed` in
  existing databases stay terminal and untouched.
- The "Instant-rail jitter" preset is re-tuned (1200–3000 ms) because an attempt now makes one
  proxied call instead of two.
- Hand-written store fakes must implement `ReleaseClaimsAsync`.
