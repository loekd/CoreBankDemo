# ADR-024: The instant rail's retry loop is time-bounded and honours `Retry-After`

**Date:** 2026-09-23
**Status:** Accepted
**Deciders:** Architecture team
**Supersedes in part:** ADR-018's option rule "`AttemptTimeoutMilliseconds × MaxAttempts` must not exceed
`BudgetMilliseconds`" and ADR-020's restatement of it with the cancel allowance; the edge-case row
"CoreBank returns 5xx → existing retry/circuit-breaker policy applies" in the instant rail spec; and,
for `MaxAttempts` only, the "Always" constraint "do not touch `BudgetMilliseconds`/`MaxAttempts`" in
[`docs/superpowers/specs/2026-09-03-add-instant-rail-load-coverage-design.md`](../superpowers/specs/2026-09-03-add-instant-rail-load-coverage-design.md)
— that constraint was about the k6 load-coverage story's own scope, not a ceiling on future changes.
**Corrects:** ADR-023's and `ARCHITECTURE.md`'s statement that backoff for CoreBank calls lives in the HTTP
resilience pipeline.

## Context

An instant payment observed live on 2026-09-23 (trace `c212e8ec7b23fbe25db40caf7495b57a`, payment `…-g001-r001-000008`) was
cancelled 4.2 s into a 7.5 s forward window. Attempt 1 had answered `429` with `Retry-After: 5`; attempt 2
was sent 4 ms later and answered `500`; with `MaxAttempts = 2` spent, the ADR-020 cancel phase ran and the
caller got its `504 Cancelled`. The cancellation was correct. The path to it was not:

- **Nobody reads `Retry-After`.** The `corebank-api` `HttpClient` has had `RemoveAllResilienceHandlers()`
  since the rail's first commit, so that AD-11's retry classification in the outbox layer is not applied
  twice. That removed the only component that would have parsed the header. `KiotaCoreBankApiClient`
  maps a `429` to `Retry(TransportRejection)` with the status code alone; the loop retries at once.
  ADR-023 and `ARCHITECTURE.md` nonetheless state that "hammering is bounded one layer down: the
  resilience handler backs off and opens the circuit". For CoreBank calls that has never been true.
- **The loop is count-bounded, not window-bounded.** ADR-018 sized `MaxAttempts` so that the worst
  case — every attempt timing out — fits inside the budget. When attempts fail *fast*, the loop ends
  early and the rest of the window goes unused, even though the window exists precisely to absorb a
  transient fault.

## Decision

The instant rail's forward loop becomes **time-bounded with an attempt cap**, and every pause between
attempts is deliberate. The cancel phase (ADR-020) is untouched.

- **Deadline first, cap second.** The loop runs while `now + MinUsefulAttempt ≤ deadline`
  (`deadline = start + Budget − CancelTimeout`) and `attempt ≤ MaxAttempts`. `MinUsefulAttempt` is
  500 ms: an attempt that cannot run that long is a wasted command that only widens the ambiguity the
  cancel phase must then resolve. `MaxAttempts` stays as a hard cap (default raised from 2 to 3) and its
  validation becomes `AttemptTimeout + CancelTimeout ≤ Budget` — one attempt must fit; the loop bounds
  the rest.
- **The server's hint wins.** A `429` or `503` carrying a valid `Retry-After` (delta-seconds or
  HTTP-date) sets the wait before the next attempt. Any other status ignores the header. The value is
  carried from `KiotaCoreBankApiClient` (`CoreBankResult.RetryAfter`, read from
  `ApiException.ResponseHeaders`) through a typed `CoreBankRetryException` whose message is byte-identical
  to today's, so the background outbox's `LastError` does not change.
- **Otherwise, back off.** A `500`, a timeout or a transport exception waits `250 ms · 2^(attempt−1)`
  with ±50 % jitter, capped at 1 s.
- **A wait that cannot be followed by a useful attempt is not made.** If `now + wait + MinUsefulAttempt`
  exceeds the deadline, or the cap has just been reached, the loop goes straight to the cancel phase. A
  `Retry-After` longer than the remaining window therefore produces the honest `504` *sooner*, not a
  request thread sleeping to no purpose.
- **The partition lock and the claim are held while sleeping.** ADR-020's ordering rule — a later row of
  the same partition and priority cannot overtake one whose fate is being decided — applies to a wait
  exactly as it applies to an attempt and to the cancel.
- **Retry stays in the handler.** Neither Kiota's `RetryHandler` nor the standard resilience handler is
  enabled on the `corebank-api` client. Both retry inside one `SendAsync`, so a per-attempt timeout cannot
  be expressed and "skip the wait, go cancel" cannot be decided there; Kiota's handler never retries
  transport exceptions, so the loop would remain for those; and the same forwarder serves the background
  outbox, whose retry rule (ADR-023) must not change as a side effect. Kiota's contribution is the status
  code and the headers on the exception, which is all that is needed. No `429` is added to the checked-in
  OpenAPI document: CoreBankAPI never sends one; the base `ApiException` for an unmapped status carries
  the headers.
- **Observability.** Each sleep is an `ActivityEvent` `instant_rail.retry_wait` on the request span with
  `attempt`, `wait_ms`, `source=retry-after|backoff` and `status_code`, so a waterfall shows the gap was
  chosen. No metric or attribute value is added.
- **Dev Proxy.** The armed `429` fault's `Retry-After` drops from `5` to `2` seconds so that the default
  profile can show a backed-off retry that settles inside the window; a larger value demonstrates the
  skip-to-cancel path.

## Consequences

- The standard rail and the background outbox are behaviourally unchanged. The background outbox still
  retries a `429` on every poll tick without backoff; mapping `Retry-After` onto `HoldUntil` there is a
  separate decision, tracked in `docs/backlog.md`.
- `InstantRailOptions.MaxAttempts` changes default and loses its product term in validation. A
  configuration that was valid under ADR-018/020 remains valid; some configurations that were rejected
  (large `MaxAttempts`) are now accepted, because the loop, not the validator, bounds them.
- Under a fault profile that answers fast, an instant request now holds its partition lock longer on
  average. The worst case grows too: with the old default (`MaxAttempts: 2`) the worst-case hold was
  `2 × 2500 + 1500 = 6.5 s`; with the shipped default (`MaxAttempts: 3`) and a window-bounded loop it is
  the full `9 s` budget, unattainable before only through the retired `AttemptTimeoutMilliseconds ×
  MaxAttempts` product term. The ceiling the validator permits is unchanged at `9 s` — the same budget
  ADR-018 sized the rail to. Rows queued behind it in a burst see that as extra latency and,
  at the window's edge, as more *local* cancellations ("budget exhausted before the command left
  PaymentsAPI"). Accepted: under a real `429` that partition should not be sending.
- `CoreBankResult<T>` and the forwarder's exception type gain members; both additive. Fakes that construct
  `CoreBankResult.Retry(...)` are unaffected (`RetryAfter` defaults to null).
- ADR-023's and `ARCHITECTURE.md`'s claim that backoff lives in the resilience pipeline is withdrawn by
  this record; the kernel's "next poll tick" rule stands on its own.
