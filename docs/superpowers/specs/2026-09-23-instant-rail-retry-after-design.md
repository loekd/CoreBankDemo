# Instant rail honours `Retry-After` and uses its forward window

> **Status:** Implemented
> **Kind:** design spec
> **Original date:** 2026-09-23
> **Related:** [ADR-024](../../adr/ADR-024-instant-rail-retry-policy.md); [ADR-018](../../adr/ADR-018-instant-payment-rail.md); [ADR-020](../../adr/ADR-020-instant-rail-timeout-cancellation.md); [ADR-023](../../adr/ADR-023-corebank-sole-outcome-source.md); [instant rail timeout cancel](2026-09-08-instant-rail-timeout-cancel-design.md); [constraints](../../constraints.md)

## Intent

**Problem:** Observed live on 2026-09-23 (trace `c212e8ec7b23fbe25db40caf7495b57a`, payment
`demo-burst-…-g001-r001-000008`). Under the armed Dev Proxy profile, attempt 1 of an instant payment
answered `429` with `Retry-After: 5` after 1.75 s. PaymentsAPI retried **4 ms later**, got a `500`, and
— having spent its two attempts — cancelled the payment through CoreBank at 4.2 s of a 7.5 s forward
window. Two things are wrong with that, and neither is the cancellation itself:

1. The server's `Retry-After` was ignored. The header is parsed by nobody: the standard resilience
   handler is deliberately removed from the `corebank-api` client (AD-11), and
   `KiotaCoreBankApiClient` keeps only the status code of a non-2xx answer. A `429` is retried exactly
   like a `500`: immediately.
2. The loop is bounded by a *count* of attempts, not by the window it was given. Two fast failures end
   it at ~3 s; the remaining 4.5 s of the budget — time in which CoreBank may well have recovered — is
   never used.

**Approach:** Make the forward loop **time-bounded with an attempt cap**, and make every wait between
attempts deliberate: the server's `Retry-After` when it gave one, a small jittered backoff otherwise.
An attempt is started only when it can run for a useful length inside the window; a wait that would
push the next attempt past the window skips straight to the cancel phase, so the caller gets its honest
`504` *sooner*, not later. The partition lock and the row's claim are held throughout, exactly as they
are today during an attempt. Retry stays in the handler, outside Kiota and outside any HTTP middleware
(ADR-024 records why).

## Boundaries & Constraints

**Always:**
- Keep the forward deadline at `start + BudgetMilliseconds − CancelTimeoutMilliseconds` and the cancel
  phase exactly as ADR-020 specifies. Nothing here changes what happens *after* the forward phase.
- Sleep between attempts while holding the partition lock and the claimed row (`Processing`). The
  row's fate is decided before anything behind it in the partition moves — the same rule that already
  applies during an attempt and during the cancel.
- Honour `Retry-After` only on `429` and `503`; ignore the header on any other status. Accept both
  forms the header allows (delta-seconds, HTTP-date); a value that does not parse, or is negative, is
  treated as absent.
- Never start an attempt that cannot run for at least `MinUsefulAttempt` (500 ms, an internal constant)
  inside the window. Never start a wait that would end less than `MinUsefulAttempt` before the deadline.
- Keep `MaxAttempts` as a hard cap on attempts. Change its default from `2` to `3`.
- Keep the message text of the retry exception the background outbox sees byte-identical, so
  `LastError` on outbox rows and every assertion that reads it are unchanged.
- Keep the metric contract's closed attribute sets closed: no new metric, no new attribute value. The
  wait is recorded as a span event on the request's activity, not as a metric.
- Propagate caller cancellation out of a sleep exactly as out of an attempt: `OperationCanceledException`
  rethrown, no measurement recorded, the row left `Processing` to be reclaimed when its claim goes stale.
- Coverage ≥ 90 % per tier; TDD; `FakeTimeProvider` for every timing assertion.

**Ask First:** Any change to the cancel phase, the residual `202`, `BudgetMilliseconds`,
`AttemptTimeoutMilliseconds` or `CancelTimeoutMilliseconds` defaults; adding a `429` to the checked-in
OpenAPI document; enabling any retry middleware (Kiota `RetryHandler`, the standard resilience handler)
on the `corebank-api` client; touching the background outbox's retry behaviour.

**Never:** Retry a business rejection (`400`, or a `2xx` with `Status: Failed`) — AD-11 and ADR-023 are
untouched. Retry inline after CoreBank replayed a `Cancelled` payload. Hold the request thread beyond
`BudgetMilliseconds`. Sleep without the partition lock. Let a `Retry-After` value extend the window.

## Retry policy

```
deadline  = start + Budget − CancelTimeout                 (unchanged: 7.5 s by default)
for attempt = 1 .. MaxAttempts:
    remaining = deadline − now
    if remaining < MinUsefulAttempt:              break     → cancel phase (ADR-020)
    run attempt with timeout = min(AttemptTimeout, remaining)
    committed outcome / business verdict / Cancelled replay → conclude as today
    retryable failure:
        if attempt == MaxAttempts:                break     → cancel phase, no sleep
        wait = RetryAfter (429/503 with a valid header)
             ?? backoff(attempt)                            250 ms · 2^(attempt−1), ±50 % jitter, cap 1 000 ms
        if now + wait + MinUsefulAttempt > deadline: break  → cancel phase, no sleep
        sleep(wait)                                         partition lock and claim held
→ attempts exhausted                                        → cancel phase (ADR-020)
```

With the defaults (`Budget 9000`, `Attempt 2500`, `MaxAttempts 3`, `Cancel 1500`) and the Dev Proxy
profile after this change (`Retry-After: 2`, latency 1.2–3 s):

| Sequence | Timeline | Outcome |
|---|---|---|
| `429` at 1.75 s | sleep 2 s → attempt 2 at 3.75 s with 3.75 s of window | can settle |
| `500` at 1.2 s, `500` at 2.7 s | backoff ~250 ms, ~500 ms → attempt 3 at ~3.2 s with 4.3 s | can settle |
| `429` at 3.0 s with `Retry-After: 5` | 3.0 + 5 + 0.5 > 7.5 → no sleep, cancel at 3.0 s | `504` two seconds earlier than today |
| three timeouts | 2.5 + 0.25 + 2.5 + 0.5 = 5.75 s → third attempt gets 1.75 s | cap reached, cancel at 7.5 s |

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| `429` with `Retry-After: 2` | attempt 1 fails at 1.75 s | sleep 2 000 ms holding lock and claim; attempt 2 at 3.75 s; span event `instant_rail.retry_wait{attempt=1, wait_ms=2000, source=retry-after, status_code=429}` | — |
| `503` with `Retry-After` | CoreBank or Dev Proxy answers `503` + header | as `429` | — |
| `Retry-After` as HTTP-date | `Retry-After: <RFC 7231 date>` | wait = date − `TimeProvider.GetUtcNow()`, clamped to ≥ 0 | unparseable → treated as absent |
| `Retry-After` negative / garbage | header present, value invalid | treated as absent → backoff | logged at Debug |
| `Retry-After` ≥ remaining window | `now + wait + 500 ms > deadline` | no sleep, no attempt; straight to the ADR-020 cancel phase | — |
| `500`, timeout, connection error | no header | backoff 250 ms · 2^(n−1), jitter ±50 %, cap 1 000 ms; span event `source=backoff` | — |
| `500` with `Retry-After` | header on a status other than 429/503 | header ignored → backoff | — |
| `400` / `2xx Failed` / `2xx Cancelled` | CoreBank's verdict | never retried (unchanged) | — |
| `remaining < 500 ms` before an attempt | late in the window | attempt not started; cancel phase | — |
| `MaxAttempts` reached | after the *n*th failure | no sleep; cancel phase (a sleep that leads to no attempt is never made) | — |
| Caller disconnects during a sleep | `cancellationToken` fires | `OperationCanceledException` rethrown; row left `Processing`; no metric | as during an attempt today |
| Options: `Attempt + Cancel > Budget` | `2500 + 1500 > 3000` | startup failure (Story 3.1 pattern) | message names the new inequality |
| Options: `Attempt × MaxAttempts + Cancel > Budget` | `2500 × 3 + 1500 = 9000` → allowed; `2500 × 4 + 1500` → allowed | no longer validated: the loop bounds it | — |
| Background outbox delivery | any retry outcome | unchanged: exception message identical; header carried but unused | — |

## Code Map

- `CoreBankDemo.PaymentsAPI/Models/InstantRailOptions.cs` — `MaxAttempts` default `3`; XML doc for the new
  inequality. `InstantPaymentRailServiceCollectionExtensions.cs` — validation becomes
  `AttemptTimeoutMilliseconds + CancelTimeoutMilliseconds ≤ BudgetMilliseconds`; the half-of-`ProcessingTimeout`
  guard stays. `appsettings.json`: `MaxAttempts: 3`.
- `CoreBankDemo.PaymentsAPI/Outbox/CoreBankApiContracts.cs` — `CoreBankResult<T>` gains `TimeSpan? RetryAfter`,
  set only by the `Retry` factory. New `CoreBankRetryException : InvalidOperationException` with
  `RetryReason`, `StatusCode`, `RetryAfter`; its `Message` is exactly the string
  `HttpForwardOutboxDeliveryStrategy.RetryOutcomeException` builds today.
- `CoreBankDemo.PaymentsAPI/Outbox/KiotaCoreBankApiClient.cs` — in the `ApiException` branch, when
  `ResponseStatusCode` is `429` or `503`, parse `Retry-After` from `ex.ResponseHeaders` via a new
  `RetryAfterHeader.TryParse(IDictionary<string, IEnumerable<string>>, TimeProvider, out TimeSpan)` (internal
  static, delta-seconds and HTTP-date). Every other branch leaves `RetryAfter` null.
- `CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs` — `RetryOutcomeException` returns
  the new typed exception carrying `submission.RetryAfter`. The background `DeliverAsync` path is unchanged
  (it catches the base type through the kernel).
- `CoreBankDemo.PaymentsAPI/Handlers/InstantPaymentForwardingHandler.cs` — `ForwardUnderPartitionLockAsync`
  implements the policy above. New internal constants `MinUsefulAttempt` (500 ms), `BackoffBase` (250 ms),
  `BackoffCap` (1 000 ms), `BackoffJitter` (0.5). A new internal `InstantRetryPolicy` (pure: inputs are
  attempt number, `RetryAfter`, `remaining`, a `Random`; output is `Sleep(TimeSpan)` or `GiveUp`) holds the
  arithmetic so it is unit-testable without the handler. The span event is added on `Activity.Current`.
- `CoreBankDemo.AppHost/devproxy/config/devproxy-errors.json` — the `429` entry's `Retry-After` `5` → `2`.
  The `generated/` copy is derived at start (ADR-019).
- `docs/adr/ADR-024-instant-rail-retry-policy.md` — the decision record. `ARCHITECTURE.md` line on
  "backoff lives one layer down" corrected (see Design Notes).
- Tests: `tests/CoreBankDemo.PaymentsAPI.Tests/KiotaCoreBankApiClientTests.cs`, `…/InstantPaymentForwardingHandlerTests.cs`,
  `…/InstantRailOptionsValidationTests.cs` (or wherever the Story 3.1 validation tests live), new
  `…/InstantRetryPolicyTests.cs`, new `…/RetryAfterHeaderTests.cs`.

## Acceptance

- Given an instant payment whose first attempt answers `429` with `Retry-After: 2`, when the second attempt
  succeeds, then the caller receives `200 Completed`, the request span carries one `instant_rail.retry_wait`
  event with `source=retry-after` and `wait_ms=2000`, and the partition lock was held for the whole wait
  (the fake lock service records no release before the conclusion).
- Given a `Retry-After` that would end less than 500 ms before the forward deadline, when the attempt
  fails, then no sleep occurs and the cancel phase begins at once; the `504` arrives earlier than the
  deadline.
- Given three attempts that each answer `500`, when the third fails, then the caller receives the ADR-020
  cancel outcome and the two sleeps between them were within `[125, 375]` ms and `[250, 750]` ms.
- Given `Payments:InstantRail` with `AttemptTimeoutMilliseconds + CancelTimeoutMilliseconds > BudgetMilliseconds`,
  when the host starts, then startup fails with a message naming that inequality; with
  `AttemptTimeoutMilliseconds × MaxAttempts + CancelTimeoutMilliseconds > BudgetMilliseconds` it starts.
- Given the background outbox processor delivering a row that answers `429`, when the retry outcome is
  recorded, then `LastError` is byte-identical to today's text.
- The k6 acceptance gate (`CoreBankDemo.LoadTests`) passes unchanged: no invariant, count formula or
  expected status is touched.

## Design Notes

**Why the window is a ceiling and the attempts are still capped.** SCT Inst gives nine seconds *in
total*; the rail must answer within it, not at it. What the window buys is the right to *wait* — for a
`Retry-After`, for a backoff — not the right to send more requests. The cap keeps the number of
commands that might be in flight at CoreBank small, which keeps the ADR-020 cancel phase's ambiguity
small. Three attempts of 2.5 s never fit in 7.5 s with any wait between them, so in practice the deadline
ends the loop before the cap does; the cap is a guard against a misconfiguration that makes attempts
very short.

**Why sleep holding the lock.** Releasing the lock during a wait would let a later row of the same
partition and priority overtake this one — the ordering rule ADR-020 keeps for the cancel phase — and
would let the background processor claim the row the moment `HoldUntil` lapses. Holding it costs the
rows behind this one some latency, but the head-of-line row already holds the lock for up to the whole
forward phase today; the worst case is unchanged, only the average rises. Under a real `429` that is
correct: nobody in that partition should be sending.

**Why not Kiota's `RetryHandler` or the standard resilience handler.** Both retry *inside one
`SendAsync`*, so a per-attempt timeout is not expressible (the caller's token bounds the whole retry
chain); neither can know that a wait should be skipped in favour of the cancel phase; Kiota's handler
retries responses only, never transport exceptions, so the loop would still exist for those; and the
same forwarder serves the background outbox, where a transport-level retry would silently change
ADR-023's "retry on the next poll tick" rule. Kiota's contribution is `ApiException.ResponseStatusCode`
and `ResponseHeaders`, which is all the handler needs. A `429` is not added to the OpenAPI document:
CoreBankAPI never sends one, only Dev Proxy does, and the document is the truthful owner of the contract.
The unmapped status arrives as the base `ApiException` with its headers, which is enough.

**A correction to ADR-023 and `ARCHITECTURE.md`.** Both say that the kernel needs no backoff because
"the resilience handler backs off and opens the circuit one layer down". That has never been true for the
`corebank-api` client: `CoreBankClientServiceCollectionExtensions` has called `RemoveAllResilienceHandlers()`
since the rail's first commit (`01eba00`), precisely so that AD-11's retry classification is not
double-applied. The background outbox therefore retries a `429` on every 200 ms poll tick with no
backoff. This spec does not change that — see the backlog — but the false statement is corrected.

**Why `Retry-After: 2` in Dev Proxy.** With `5`, a `429` after the latency plugin's 1.2–3 s can never be
followed by an attempt inside the window, so honouring the header would always mean "cancel" and the
demo could never show a backed-off retry succeeding. With `2` the default profile shows the success
path; an operator who wants to show the "server says wait longer than we have" path raises the value.

## Verification

- `dotnet test CoreBankDemo.UnitTests.slnf` green with the new tests; coverage gate met on
  `CoreBankDemo.PaymentsAPI`.
- `dotnet test CoreBankDemo.IntegrationTests.slnf` green (no schema change, so no new integration test is
  expected).
- One `aspire run` of `CoreBankDemo.AppHost` with fault arming on: a DemoRunner burst shows at least one
  instant payment whose Tempo trace has a `429`, an `instant_rail.retry_wait` event and a later `200`.
- The k6 acceptance harness (`load-test` skill) passes with the default profile.
