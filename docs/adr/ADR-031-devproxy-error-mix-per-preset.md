# ADR-031: The Regular Dev Proxy profile is an observability mix, and each preset names its own errors file

**Date:** 2026-10-22
**Status:** Accepted
**Deciders:** Architecture team
**Amends:** ADR-030 (Decision 1: the error plugin now also watches the cancel; Decision 2: the checked-in `devproxy-errors.json` is no longer breaker-blind; Decision 3: "Regular profile" is no longer the quiet 5 % profile), ADR-019 (the generated errors file is a verbatim copy of a checked-in file, not a regeneration over the watched surface).

## Context

The observability talk needs one burst that shows every outcome the system can produce, so that
the dashboards, traces and logs have something to disagree about: most payments settle, some only
after a visible retry, a few are cancelled before they execute (`504 Cancelled`), and a few are
deferred to the background rail (`202 Pending`) and settle later. Measured against the running system on 2026-10-22, the Regular
profile as ADR-030 left it could not produce that burst:

1. **Nothing failed.** At 5 % and three attempts, a payment is lost only when all three attempts
   are answered with an error: 0.05³, about one in 8 000. A 20-payment instant burst under the
   "Regular profile" chip produced one retry and twenty `200 Completed` (Loki, run `r005`).
2. **Nothing could end `Failed`.** The errors file answered `409`/`423` only, and PaymentsAPI
   treats every non-`400` status as a retryable transport rejection (ADR-023). Only a `400` is a
   verdict.
3. **Nothing could end `202`.** ADR-030 scoped every plugin to the transfer command so the cancel
   always gets through. That was the right call for the breaker demo, and it removed the deferred
   outcome from the Regular profile entirely.
4. **The Flaky preset is too destructive for this burst.** At 60 % the same run cancelled nine of
   twenty (run `r006`), and it is meant to.

Before ADR-030 (2026-10-15) the profile injected `503`/`429`/`500` on every `/api/*` call, which
faulted the cancel and the account read too and let the error knob open the circuit. The talk's
observability burst was built on that behaviour; ADR-030 traded it for a clean breaker demo without
offering a replacement. The user's ask: a preset that produces all four outcomes, about three
non-success payments per burst of twenty, with the `500`s back.

Seven candidate mixes were measured on the live system (20 instant payments from one debtor, four
at once, the console's defaults). What the measurements showed, beyond the numbers:

- **Latency cannot produce a `202`.** A band that overruns the 2.5 s attempt timeout produces
  `504`s, not `202`s: the cancel reaches CoreBank before the delayed original does, stores a
  tombstone, and the original is replayed as `Cancelled`. Thirteen timeouts, zero deferrals.
- **A faulted cancel produces a `202`, and it cascades.** The deferred row heads its partition
  until the background rail delivers it, and every instant payment queued behind it waits. Under a
  200–800 ms band the retry walks of the payments ahead plus that wait pushed the ones behind past
  their forward budget: the same mix went from three non-success per burst to six, most of them
  "budget exhausted before the command left PaymentsAPI". A 100–400 ms band keeps the queue inside
  the budget.
- **Every `500` is a breaker failure, and so is every `500` on a cancel.** At one `500` in ten
  injected answers the circuit opened in four of six bursts (`FailureRatio` 0.2 over a
  `MinimumThroughput` of 10 per replica, ADR-029). At one in nineteen on the transfer command and
  a `423` on the cancel it opened in about one burst in ten. A `423` on the cancel still produces
  the `202`: `CancelThroughCoreBankAsync` treats any failed cancel as "outcome unknown".
- **An injected `400` is a ghost verdict.** PaymentsAPI reads a `400` as CoreBank's verdict
  (ADR-023) and completes the row `Failed`, but CoreBank never received the command, recorded
  nothing and published no `transaction.failed` event. The console counted three such payments as
  failed while the dashboards, which read CoreBank's processed counter and the Payments outbox's
  cancellations, showed one. A mix with a `400` therefore makes the console and Grafana disagree
  by construction; a first cut with two `400`s in nineteen picks was dropped for that reason.
- **The preset's rate must be a ladder step.** The console snaps to 0/5/10/25/40/60/80/100, so the
  mix is tuned at 40 %, not 45 %.

## Decision

1. **The checked-in Regular profile is the observability mix.** `devproxyrc.json` sets the error
   knob to 40 % and the band to 100–400 ms; `devproxy-errors.json` answers the transfer command from
   nineteen weighted picks — nine `409`, nine `423`, one `500` — and answers the cancel with a
   `423`. No `400`: see the ghost-verdict finding above. Measured over six bursts of twenty:
   4, 0, 6, 0, 1, 4 non-success payments (average 2.5; eleven `504 Cancelled`, four
   `202 Pending`, no breaker trip in those six), with `504 Cancelled` and `202 Pending` both occurring and every deferred
   payment settling through the background rail within two seconds of its `202`. This is what the
   "Regular profile" chip stages and what a plain `aspire run` injects.
2. **The error plugin watches the cancel; only an errors file can reach it.** The Regular
   profile's `GenericRandomErrorPlugin` lists both `POST …/api/transactions/process` and
   `POST …/api/transactions/cancel`; the latency and rate-limit plugins still watch the transfer
   command alone (ADR-030's reason stands: latency on the cancel overruns the cancel allowance).
   Verified against Dev Proxy 3.2.0: a watched URL with no entry in the errors file passes through
   untouched. Which URLs are faulted is therefore decided by the errors file, per mix.
3. **Each preset names its errors file through `ErrorMix`.** `FaultLevels` gains a fifth
   component, `Mix`, with two values: `Mixed` (`devproxy-errors.json`, above) and `Retryable`
   (`devproxy-errors-retryable.json`: `409`/`423` on the transfer command only — ADR-030's file
   under its own name). "Regular profile" is `Mixed`; "Flaky CoreBank" is `Retryable` and keeps
   every guarantee ADR-030 gave it: the breaker never counts an injected answer, the cancel is
   never faulted, a failed payment ends `504`, never `202`. The mix is part of the level: the same
   rate from the other file is a different level, reads as `Custom`, and is stamped on evidence as
   such. A knob moved after a preset keeps that preset's mix. A quiet error knob has no mix
   (`Normalized()` resets it), so two quiet configs never differ only by one.
4. **The generated errors file is the chosen mix's checked-in file, copied verbatim.** The
   console no longer regenerates entries over the watched surface (ADR-019's rationale was
   retired by ADR-030 and is now contradicted by Decision 2). The readback recognises a mix by
   comparing the written entries with each checked-in file; a session file that matches none
   reads as the default mix. A profile that ships no errors file for the mix (LoadTests) keeps
   the built-in `503`/`429`/`500` fallback over its watched surface.
5. **Nothing changes in PaymentsAPI or CoreBankAPI.** The instant-rail budget, attempt timeout,
   attempt cap, cancel allowance and breaker thresholds stay as ADR-020/024/029 set them. The
   mix is tuned to them; retuning the breaker would move the trip rate stated in Decision 1.

## Consequences

- A plain `aspire run` of the Regular AppHost now injects 40 % faults out of the box. "All off"
  in the console, or `--Features:UseDevProxy=false`, is the quiet development setting.
- The "Regular profile" burst is statistical: a burst of twenty can come out clean or lose six,
  and roughly one burst in ten opens a replica's circuit on its `500` (none did in the final six). That is the point of the
  preset; the Flaky and Throttled presets remain the deterministic ones.
- The error knob's reading now names its mix ("40% mixed", "60% retryable"); the Faults
  workspace's value column grows by one word.
- ADR-030 Decisions 1–3 now describe the *Retryable* file and the *Flaky CoreBank* preset rather
  than the checked-in profile. The talk's two-burst breaker flow is unchanged.
- Adding a mix is one checked-in file, one enum value, one `ProfileRegistry` case and one preset.

## Key takeaway

> One rate, two errors files. The rate says how often the proxy answers for CoreBank; the file says
> what it answers with and which call it answers — and only the file can reach the cancel.
