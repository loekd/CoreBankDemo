# CoreBankDemo Rebuild — Product Brief

> **Status:** Reference
> **Kind:** brief
> **Original date:** 2026-08-21
> **Migrated from:** `docs/bmad/planning-artifacts/briefs/brief-CoreBankDemo-2026-08-21/brief.md` on 2026-09-10
> **Related:** PR #2; [ADR-001](../../adr/ADR-001-idempotent-inbox.md)

## What this is

CoreBankDemo is a working conference-demo system: a two-service .NET 10 / Aspire solution (PaymentsAPI → CoreBankAPI) demonstrating resilient, exactly-once payment processing with the Outbox/Inbox patterns, partitioned ordering under distributed locks, Dapr pub/sub, and end-to-end OpenTelemetry tracing. It is validated today only by a black-box k6 load test asserting five system invariants — it has **zero unit tests**.

This brief proposes the product: **the same system, rebuilt from scratch, story-driven with the workflow, with unit tests as a first-class deliverable** — itself serving as live conference-talk material demonstrating AI-driven agile development on a non-trivial distributed system.

## Why rebuild something that works

1. **The talk needs a second act.** The existing narrative ("resilience patterns layered on a payment flow") is proven. The new material is *process*: how the workflow turns a brownfield system into stories, and how an agent rebuilds it test-first without breaking the invariants.
2. **The codebase has no safety net below the load test.** Every behavior is either asserted end-to-end (minutes per run) or not at all. A ≥90%-covered unit suite gives second-scale feedback and makes each pattern's contract explicit and teachable.
3. **Accumulated cruft undermines the teaching value.** Dead feature flags, a processor that bypasses the shared base class it exists to demonstrate, and docs describing components that don't exist — a demo should model the practices it preaches.

## Users & audience

- **Primary:** Loek — repo owner, conference speaker; needs a demo that is reproducible, explainable, and green on stage.
- **Secondary:** conference audiences and repo visitors — engineers evaluating resilience patterns and/or the workflow; they read stories, tests, and ADRs as the product.

## Scope

**Same externally observable behavior as `main`.** The external contract (endpoints, ports, message topics, CloudEvents, trace propagation) and the five invariants — exactly-once, zero message loss, balance conservation, terminal-state completeness, per-key ordering — are fixed by `../../constraints.md` (the binding guardrail contract for all downstream documents).

**In scope:** rebuild of Messaging, ServiceDefaults, CoreBankAPI, PaymentsAPI, AppHost; realignment of LoadTestSupport + k6 to the rebuilt schemas; regenerated architecture docs and new ADRs for the eight deliberate cruft rulings (A1–A8 in constraints.md); a unit-test suite (xUnit + AwesomeAssertions + Moq) with a coverlet-enforced ≥90% line-coverage gate on logic projects.

**Out of scope:** production deployment, new features, authentication, EF migrations, alternative brokers or databases.

## Approach (fixed decisions)

- In-place rebuild on `feature/bmad`; `main` keeps the last working demo.
- Bottom-up dependency order (test infra → Messaging → ServiceDefaults → CoreBankAPI/PaymentsAPI → AppHost → load-test realignment → docs), gated per story by `dotnet test` on `CoreBankDemo.Rebuild.slnf`. Stories may overlap when their recorded prerequisites and stable contracts are available; overlap never permits a story to claim live integration or completion before its dependency gate passes.
- The k6 + LoadTestSupport harness remains the acceptance tier; if code and load tests conflict, load tests adapt unless a §1 invariant is genuinely violated.
- TDD per story; TEA (Test Architect) workflows for test design and epic-end coverage review.

## Success criteria

1. All five invariants pass a full load-test run (reset → k6 with 10% duplicate keys → drain → assertions) on the rebuilt system.
2. ≥90% line coverage on logic projects, enforced locally by plain `dotnet test` — no CI dependency.
3. Every line of rebuilt production code traces to a story; every story to an epic; every epic to this brief.
4. Regenerated ARCHITECTURE.md describes only code that exists; A1–A8 each ruled in an ADR.
5. The Aspire demo boots one-command and the existing `.http` demo flows work unchanged.

## Risks

- **Mid-rebuild red solution** — mitigated by the solution-filter strangler and `main` as fallback.
- **Coverage gaming** (90% met with shallow tests) — backstopped by TEA epic-end reviews and the invariant-based acceptance tier.
- **Doc drift between documents and system docs** — source-of-truth rule in AGENTS.md; ARCHITECTURE.md regenerated from code, last.
- **Workflow version churn** — installed version pinned (BMM v6.11.0, TEA v1.23.3); no mid-project reinstall.

## Inputs & references

- `ARCHITECTURE.md` (system truth today), `docs/adr/ADR-001…007`, `README.md`, `AGENTS.md`
- `../../constraints.md` — binding contract (invariants, external API, conventions, test rules, A1–A8)


## Addendum

## Addendum — CoreBankDemo Rebuild Brief

Depth that belongs in downstream documents (PRD, architecture, epics), preserved here so it is not lost.

## Proposed epic order (input to the epics and stories workflow)

| Epic | Content | Stories (est.) |
|---|---|---|
| E0 | Test infra & scaffolding: test packages in Directory.Packages.props, 4 test projects, `tests/Directory.Build.props` coverage gate, `CoreBankDemo.Rebuild.slnf` | 2–3 |
| E1 | Messaging library: PartitionHelper, MessageConstants/models, repository bases (`StoreIfNewAsync`), InboxProcessorBase, OutboxProcessorBase with pluggable publish, retry/poison paths | 5–7 |
| E2 | ServiceDefaults: options + DataAnnotations validation, distributed lock behind interface, CloudEventTypes, OTel/Polly wiring | 3–4 |
| E3 | CoreBankAPI: domain + DbContext, TransactionValidator, TransactionExecutor, dedupe intake → Inbox, InboxProcessor on new base, OutboxPublisher (3 events, same tx), MessagingOutboxProcessor on base | 6–8 |
| E4 | PaymentsAPI: intake → Outbox → 202, idempotency-key handling, OutboxProcessor → ICoreBankApiClient, Dapr event inbox, status model | 5–6 |
| E5 | AppHost: Aspire graph, config alignment (PartitionCount=4, dead flags removed) | 2–3 |
| E6 | LoadTestSupport + k6 realignment to new schemas; keep 10% duplicate-key ratio | 3–4 |
| E7 | Docs: regenerate ARCHITECTURE.md from code; ADRs for A1–A4 + test strategy; update skills if surfaces changed | 2 |

Dependency spine: E0 → E1 → E2 establishes the shared test, messaging, and service foundation. E3 and E4 may overlap once their shared prerequisites exist. E5 work may overlap when the affected service seams are stable. E6 implementation may proceed behind stable ports and fakes, but live load integration and acceptance cannot complete until the required E3–E5 stories are done. E7 completes after the implementation and acceptance evidence it documents. Advanced statuses are preserved; a story remains in progress while any recorded completion dependency is unmet. Stories remain sized to an agent-safe class cluster unless a human-approved story explicitly records a broader boundary.

## Rejected alternatives

- **Parallel `v2/` folder rebuild** — rejected: path/namespace churn, repo temporarily doubles; git history on `main` already preserves the reference implementation.
- **Fresh repository** — rejected: loses ADRs, project skills, and the repo's identity as *the* demo.
- **TUnit as test framework** — rejected in favor of xUnit: audience familiarity beats novelty for talk material.
- **Rebuilding LoadTestSupport through the story mill first** — rejected: it is the acceptance harness; it conforms to the rebuilt system (E6), not the other way around.

## Verification tiers (input to PRD non-functionals)

1. **Story tier:** `dotnet build` + `dotnet test` on `CoreBankDemo.Rebuild.slnf`, coverlet threshold ≥90% line.
2. **Epic tier:** E3/E4 TEA coverage-gap review; E5 `aspire-launch` boot + `.http` smoke + trace check via `aspire-mcp`.
3. **Milestone tier (after E6, E7):** full `/run-load-tests`: reset_database → k6 → poll_until_drained → get_assertion_results (five invariants) → trace analysis.
