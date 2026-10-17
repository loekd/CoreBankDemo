---
name: feature-impact
description: |
  Measure what a feature branch does to CoreBankDemo's correctness and throughput compared with main, as a background run that ends in one report.

  **When to use:**
  - When the user asks to measure, compare, or demo the impact of the checked-out branch against main (`/feature-impact`, "run the feature impact", "compare my branch with main under load"), on stage or not.
  - When a load-test comparison must run unattended while the user keeps presenting or working.

  **When NOT to use:**
  - Do NOT use for a single acceptance run of one branch — use `load-test`.
  - Do NOT use to explain why a run was slower — use `corebank-regression-analysis` afterwards, on the user's request.
  - Do NOT use while any AppHost is running; the run refuses to start and never stops something it did not start.
---

# Feature impact

One command, one background run, one report. `scripts/measure.py` is the whole runbook:
unit tier on the feature checkout, then the LoadTests acceptance run on a temporary
worktree of `origin/main`, then the same run on the feature checkout, then a side-by-side
report (assertions, completed/failed/cancelled, average and peak transactions per second,
lock-impact dashboard link per run). Each load step is the `aspire-launch` and `load-test`
procedure as code, with a deadline on every wait. The script stops at the first failure,
cleans up what it started, and writes the failing step into the report. It never writes
inside the repository.

**The agent's job is launch and relay. Nothing else.** Violating the letter of that rule is
violating the spirit of it: the user chose a scripted run precisely so that no diagnosis,
retry, or fix happens while they are on stage.

## Procedure

1. **Preflight, read-only.** Run `git status --porcelain` and `git branch --show-current`.
   Dirty tree or `main` checked out: report it and stop; the user fixes that, not you.
2. **Confirm the branch.** Ask with AskUserQuestion: "Measure `<branch>` (@`<sha>`) against
   `origin/main`? Profile: 1000 transactions / 10 VUs." Offer the profile override as the
   other option. Do not launch before an explicit yes.
3. **Launch in the background**, from the repository root, with the Bash tool's
   `run_in_background`:

   ```bash
   python3 -I .claude/skills/feature-impact/scripts/measure.py --transactions 1000 --vus 10
   ```

   Tell the user in one line that it is running and roughly how long it takes: about
   three minutes when the branches are already built, longer on a cold build. Grafana
   restarts twice, because each run recreates the persistent LGTM container; the data
   survives. Then go back to whatever the user needs.
4. **Relay when it exits.** The completion notification carries the script's output, which
   ends with the report. Show the report as it is (the table and the verdict line, or the
   ABORTED section). Do not summarize it into a different shape, do not add a diagnosis,
   do not soften a failure. Also name the `report.md` path, which the output prints.

## What you never do after launch

| Temptation | Why it is wrong here |
|---|---|
| "A timeout doesn't say which dependency failed; let me check `aspire ps`, resources, or console logs" | That is troubleshooting mode. The report names the failing step; the user decides what happens next. |
| "I started that AppHost, so I may stop and restart it" | The script already stopped it. A second launch while the user is presenting is a surprise they did not ask for. |
| "Run only the feature branch, or compare with an older run in Prometheus" | A different measurement than the one requested. Report the abort and stop. |
| "The script looks wrong, I'll patch it and rerun" | Never change code or the script during a measurement. Note the suspicion in one sentence; fixing it is a separate, later task. |
| "Let me poll `aspire ps` or the log file meanwhile" | The notification arrives on its own. Polling adds noise to the stage terminal. |
| "It is taking long, I'll stop it" | The script has deadlines on every step. Wait for the notification. |

## Red flags, stop and relay instead

- You are about to run anything other than `git status`, `git branch`, or the launch command.
- You are reading Aspire resources, logs, traces, or the LoadTestSupport endpoints yourself.
- You are composing an explanation of *why* a step failed.
- You are offering a partial or substitute comparison.

## Reading the report

- **Average throughput** is processed transactions divided by the processing window
  (first to last increase of `corebankdemo_transaction_processed_total`, joined to the run
  by `run_id`). **Peak** is the highest 15 s rate, the same number as the dashboard tile.
- Both runs use the profile you launched with; everything else (such as `Demo:LockStrategy`)
  comes from each branch's own configuration, which is the point of the comparison.
- A green unit tier and `allPassed` on both runs means the feature changed only speed. The
  verdict line gives the deltas.
