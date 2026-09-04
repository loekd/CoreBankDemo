---
title: 'DemoRunner: from cue player to operator console'
type: 'ux-intake'
created: '2026-09-03'
status: 'draft'
purpose: 'Intake for bmad-ux (DESIGN.md + EXPERIENCE.md). This is raw material, not a specification — the spines are Sally''s to elicit and author.'
---

# Intake brief — DemoRunner operator console

## How to use this

This is a **brain dump for Discovery**, not a design. It records what exists, what changes, and how the tool is really used, so elicitation can start from facts instead of from zero. Where a decision is genuinely mine to make it is listed under *Open questions* rather than answered here.

## 1. The shift

`CoreBankDemo.DemoRunner` today is a **scripted cue player**. The evidence, in its own code:

- `Application/Scenarios/TalkScenarioDefinition.cs` — a `TalkCueDefinition` carries `SlideAnchor`, `SpeakerNote`, `PreArmActions`, `Actions`, `InvestigateActions`.
- `Application/StateMachine/` — `CueRuntimeState`, `SessionState`, a 458-line `SessionController` advancing a linear cue track.
- `Scenarios/mission-critical-talk-v7.json` — the only scenario, pinned to a deck version that has since been superseded twice.
- `CliOptions` — `--show`, `--rehearse`, `--resume`, `--scenario`, all shaped around performing a script.

**That is the thing to move away from.** I do not want a PowerPoint extension that walks a fixed cue list beside a fixed deck. Every time the deck changes, the tool goes stale, and it did.

**What I want instead is an operator console**: a control surface that exposes the system's capabilities, which I drive live in whatever order the room needs. If a question comes from the floor, I want to answer it by doing the thing, not by discovering that the cue for it does not exist.

The underlying machinery is largely right and should survive — the ports (`IProcessAdapter`, `IHealthMonitor`, `IHttpActionExecutor`, `ILoadWorkflowRunner`, `IBrowserLauncher`, `IJournal`, `IProofPackStore`), the allow-listed `ActionKind` set, `EndpointResolver`, `HealthMonitor`, the doctor. **What goes is the cue track, the slide anchors, the speaker notes and the linear session.**

## 2. Form factor and hard constraints

- **Terminal UI on my laptop**, mouse-enabled, pinned to Terminal.Gui — fixed by `ADR-015`, which also forbids the console from referencing any banking project, opening a Postgres/Redis/Dapr/container socket, or reaching anything but known local HTTP endpoints and a narrowly scoped Aspire child process.
- It runs **beside** the presentation, on the presenter machine. It is not the slides.
- It must keep working when the network is hostile, because that is the point of the demo.
- One person, one session, no multi-user concerns. No auth.

## 3. Stakes and operating conditions

Live conference sessions, 55 minutes each, **12–13 November 2026**, two different talks off the same repository. Failure is visible to a room of a few hundred people and is not recoverable by a retry.

Conditions that shape every design decision:

- I am talking while operating. Attention on the tool must be **seconds, not tens of seconds**.
- Sometimes the tool is on the projected screen; sometimes only on my display. It has to be legible at projector distance either way.
- I am often mid-sentence when I need the next action. Hunting through a tree is a failure.
- Some actions are destructive to the demo state (stopping a resource, running a load test). Getting one by accident is worse than needing an extra keystroke.
- After an action I need to know *what happened* fast enough to narrate it truthfully.

## 4. Capabilities it must expose

Grouped as capabilities, not as a script. Order and grouping are Sally's to design.

**Environment**
- Start / attach / stop the **regular AppHost**
- Start / attach / stop the **load-test AppHost**
- Show which topology is currently running, and refuse to run something that needs the other one
- Health of every resource, continuously (this part works well today and should be preserved)
- Open known URLs: Aspire dashboard, Jaeger, the metrics view

**Operations**
- Send a **standard (SCT)** payment
- Send an **instant (SCT Inst)** payment — newly implemented rail
- With or without an explicit **idempotency key**; resend with the *same* key to demonstrate replay
- Send a **burst** of payments (a surge) — count configurable
- Choose accounts, amount, and rail without editing a file
- Query the result of a payment / transaction

**Observation**
- Inspect outbox and inbox contents
- Check balances / conservation
- Run the accepted load workflow and show its assertion results
- Show what the last action actually returned — status code, latency, body

**Chaos**
- Stop and restart individual resources in Aspire (Core Bank API above all)
- Turn Dev Proxy fault injection on and off
- Scale replicas, or at least show the current replica count

## 5. The two journeys

Sally asks for named protagonists. The protagonist is me, on stage, twice.

### Journey A — "Five things every developer should know", 55 minutes

Loek, second speaking slot of the day, laptop on a lectern, room of 250. He has run `--doctor` in the green room and everything was healthy.

1. Before the session: brings up the **regular AppHost**, glances at the health panel, sees all green, leaves the tool on a second display.
2. Section 1: sends **one standard payment**. Narrates the 202. Sends **the same payment again with the same idempotency key** and shows the replayed result — the audience sees dedupe rather than hearing about it.
3. Sends an **instant payment** and shows it settle inside the budget. This is the new rail and the first time the two rails are visibly different.
4. Section 2: **stops the Core Bank API** from the tool. Sends an instant payment: `202 Pending`. Narrates retry and the breaker. **Restarts Core Bank** and watches the queue drain on its own.
5. Section 3 — *the climax*: with that outage still fresh, opens the **metrics view** and shows the queue-duration histogram stretching and collapsing, while every health check stayed green the whole time. The point of the talk lands on a graph, not on a claim.
6. Section 5: sends a **burst of 200 payments** to show surge buffering, then shows the inbox draining.
7. Bonus: turns on **Dev Proxy fault injection**, sends payments, shows the system absorbing it.

What ruins this journey: having to leave the tool to use a `.http` file; a cue list that doesn't have "send it again with the same key"; not being able to restart what he stopped.

### Journey B — "Breaking your back-end before production does", 55 minutes

Same laptop, different day, more hands-on room.

1. Brings up the **regular AppHost**; one payment; green; one span in the trace.
2. **Scales to three replicas** and fires concurrent payments at one account to surface the ordering problem.
3. Turns on **Dev Proxy**, shows the same payment failing, then surviving.
4. **Stops Core Bank**, watches the outbox hold and drain.
5. Switches to the **load-test AppHost** and runs the accepted load workflow; shows the assertion results — exactly-once, no loss, balance conserved, ordering, drained.
6. Switches to a branch with a **planted lock-ordering bug**, runs the same workflow, and shows deadlocks and a failed invariant.
7. *The climax*: switches to the **second planted bug** — lock granularity — runs the same workflow again and shows **everything passing** while throughput is a quarter of what it was. Then opens metrics to show the only signal that ever existed.

What ruins this journey: a slow or ambiguous switch between the two AppHosts; assertion output he cannot read from three metres away; not being able to re-run the same workflow twice in a row cleanly.

## 6. Concerns to scan

- **Legibility under projection** — font size, contrast, how much fits without scrolling.
- **Latency honesty** — an action that takes nine seconds must look like it is working, not like it hung.
- **Irreversibility** — stopping a resource and running a load test both change the world; the affordance should differ from a read-only action.
- **Recoverability** — every stop needs an obvious start; every mutation needs an obvious way to check what it did.
- **Truthfulness on failure** — when something fails on stage I want the real error, not a friendly summary. I will read it aloud.
- **Redaction** — `JournalRedaction` exists; nothing sensitive should ever be on a projected screen.
- **Statelessness between talks** — I run this twice in two days; it must not carry hidden state from the first session.
- No i18n, no dark/light theming beyond legibility, no offline concerns, no notifications.

## 7. Open questions — for elicitation, not for me to pre-answer here

- Keyboard-first, mouse-first, or genuinely both? (Terminal.Gui supports mouse; whether I *should* reach for it mid-sentence is a real question.)
- One dense screen, or a small number of panes I switch between? What is the right unit — capability, or system component?
- Should the tool ever be on the projected screen deliberately, or is it always presenter-only?
- How much of "what just happened" belongs on screen permanently versus on demand?
- What does "check the results" mean concretely at each moment — balances, store contents, assertion output, or all three?
- Preset payment shapes versus typed parameters: how much do I want to type live?
- What is the confirmation model for destructive actions, given that a stray keypress on stage is expensive?
- Is there a "reset to a known state" action, and how brutal is it allowed to be?

## 8. Non-goals

- No cue track, slide anchors, speaker notes, or scenario JSON bound to a deck version.
- No banking UI. This is an operator tool; `ADR-015` draws that boundary and it stands.
- No new external dependency, no database or broker access, no shell escape.
- Not a replacement for the Aspire dashboard, Jaeger, or the metrics view — it should *open* those, not reimplement them.

## 9. Related context

- `docs/adr/ADR-015-presentation-safe-terminal-demo-console.md` — the binding constraints on this project.
- `docs/bmad/implementation-artifacts/spec-7-4-reusable-terminal-demo-operator-console.md` — the current implementation contract.
- `docs/bmad/implementation-artifacts/spec-add-instant-payment-rail.md` — the instant rail the console must now drive.
- Talk decks: `MissionCriticalTalk_v9.pptx` (Journey A) and `BreakingBackendBeforeProd.pptx` (Journey B).
