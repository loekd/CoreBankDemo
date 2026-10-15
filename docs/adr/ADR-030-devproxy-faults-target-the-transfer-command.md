# ADR-030: Dev Proxy faults target the transfer command only, and injected errors are statuses the breaker ignores

**Date:** 2026-10-15
**Status:** Accepted
**Deciders:** Architecture team
**Amends:** ADR-005 (the injected statuses), ADR-019 (the "narrower surface" rationale for regenerating the errors file), ADR-029 Decision 3 (which error-rate step opens the circuit on stage).

## Context

The talk needs two instant-payment bursts: one where a flaky core bank makes a handful of payments
fail *without* opening the circuit, and one where the circuit opens and recovers. Measured against
the running system on 2026-10-15, the Regular Dev Proxy profile could do neither cleanly:

1. **Latency cannot produce a cancellation.** Dev Proxy's `LatencyPlugin` delays the request and
   then forwards it even after the client has given up, so a command that overran the instant
   rail's 2.5 s attempt still executed at CoreBank a moment later. The cancel that followed was
   delayed just as much, so it either timed out (`202 Pending`, settled later by the background
   rail) or found the transfer already executed (`200`, late). Two 20-payment bursts under a pure
   latency band produced zero `504`s. For an instant payment, "deferred" is the confusing outcome.
2. **Every plugin watched `/api/*`,** so the ADR-020 cancel call was faulted along with the
   command. At 100 % errors a single instant payment ended `202` and was delivered by the
   background rail half a minute later, once the faults were removed.
3. **The breaker counted the injected errors.** The errors file answered `503`/`429`/`500`, all in
   Polly's transient set, so any error rate high enough to cancel a payment (three attempts must
   fail: 60 % for about one in five) was far above the 20 % failure ratio that opens the circuit.
4. **A burst serializes on one partition.** The console's burst uses one debtor account, so all
   its payments share one outbox partition and the instant rail delivers them one at a time
   (ADR-026). Under a 1.2–3 s band most of a 40-payment burst at four-at-once was cancelled
   locally after waiting 7.5 s for its turn, never having reached the proxy. That is correct
   behaviour, but it is not the failure the demo means to show.

## Decision

1. **Every plugin in the Regular profile watches only `POST http://127.0.0.1:5032/api/transactions/process`.**
   The root `urlsToWatch` says the same, so the console's generated errors file (ADR-019) covers
   the same surface. The cancel and the account read are never faulted. A faulted instant payment
   therefore ends `504 Cancelled` after a cancel that CoreBank confirmed (a tombstone for a command
   it never received, ADR-020), never `202`. Verified against Dev Proxy 3.2.0: a plugin's own
   `urlsToWatch` restricts that plugin (`LatencyPlugin: URL not matched` on the command while the
   error plugin fired; both skipped the account read; only the latency plugin fired on the cancel).
2. **The checked-in errors file answers `409 Conflict` and `423 Locked`.** PaymentsAPI classifies
   any non-`400` status as a retryable transport rejection (ADR-023), so the instant rail retries
   with its ADR-024 backoff and cancels after the third failure. Polly's HTTP transient predicate
   counts `5xx`, `408`, `429`, `HttpRequestException` and `TimeoutRejectedException`; a `4xx`
   outside that set is neither a success nor a failure to the breaker. The error knob can
   therefore never open the circuit at any rate.
3. **The console offers two Regular presets in place of "Instant-rail jitter".** *Flaky CoreBank*
   (errors 60 %, nothing else): a payment is cancelled only when all three attempts fail,
   0.6³ ≈ 22 %, about four of twenty, each in about a second; at 100 % a single payment walks
   through every retry and ends cancelled. *Throttled CoreBank* (10 transfers per minute, nothing
   else) is a two-step demo. First a **standard** burst from a *different* debtor (the seeded
   `NL39RABO0300065264`, partition 2): the eleventh command gets `429`, its row stays Pending, and
   the background rail's 200 ms retries open each replica's circuit (ADR-029) within a second or
   two. Then the **instant** burst from the usual debtor: every payment is refused before it
   reaches a socket and answers `504 Cancelled` with reason "CoreBank circuit open; the command
   never left PaymentsAPI", twenty of them in under four seconds. "All off" restarts the proxy, the
   next half-open probe closes the circuit, and the held standard rows settle. The order matters:
   measured the other way round, the instant payment whose own `429` tripped the breaker had its
   cancel refused by the open circuit, ended `202 Pending`, and that row then headed the debtor's
   partition for the rest of the throttle window, so the remaining instant payments each waited
   their full budget behind it and were cancelled locally, 40 payments in 56 s.
4. **Nothing changes in PaymentsAPI or CoreBankAPI.** The instant-rail budget, attempt timeout,
   attempt cap and cancel allowance stay as ADR-020/024 set them.

## Consequences

- ADR-029 Decision 3's sentence "the 25 % step opens it" no longer holds for the Regular profile:
  no error-rate step does. The throttle knob is the on-stage breaker trigger.
- The latency knob demonstrates slowness, not failure. A band that overruns the attempt timeout
  shows retries and late settlement; it will not produce cancellations, by the mechanism above.
- The "Regular profile" preset's 20–200 ms band and 1000/min limit now apply to the transfer
  command only.
- The console's preset chip limit rises from three to four.
- Per-debtor ordering (ADR-026) means a Pending row at the head of a partition holds every instant
  payment behind it for its full budget. Any demo that leaves such a row behind while the circuit
  is open is slow and shows the wrong cancel reason; trip the breaker from another partition.

## Key takeaway

> A cancel the proxy also faults is a cancel that cannot succeed. Fault the command, leave the
> withdrawal alone, and inject statuses the breaker is not meant to count when the breaker is not
> what you are showing.
