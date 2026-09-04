---
title: "CoreBankDemo Rebuild — Product Brief"
status: final
created: 2026-08-21
updated: 2026-08-21
---

# CoreBankDemo Rebuild — Product Brief

## What this is

CoreBankDemo is a working conference-demo system: a two-service .NET 10 / Aspire solution (PaymentsAPI → CoreBankAPI) demonstrating resilient, exactly-once payment processing with the Outbox/Inbox patterns, partitioned ordering under distributed locks, Dapr pub/sub, and end-to-end OpenTelemetry tracing. It is validated today only by a black-box k6 load test asserting five system invariants — it has **zero unit tests**.

This brief proposes the product: **the same system, rebuilt from scratch, story-driven with BMAD, with unit tests as a first-class deliverable** — itself serving as live conference-talk material demonstrating AI-driven agile development on a non-trivial distributed system.

## Why rebuild something that works

1. **The talk needs a second act.** The existing narrative ("resilience patterns layered on a payment flow") is proven. The new material is *process*: how BMAD turns a brownfield system into stories, and how an agent rebuilds it test-first without breaking the invariants.
2. **The codebase has no safety net below the load test.** Every behavior is either asserted end-to-end (minutes per run) or not at all. A ≥90%-covered unit suite gives second-scale feedback and makes each pattern's contract explicit and teachable.
3. **Accumulated cruft undermines the teaching value.** Dead feature flags, a processor that bypasses the shared base class it exists to demonstrate, and docs describing components that don't exist — a demo should model the practices it preaches.

## Users & audience

- **Primary:** Loek — repo owner, conference speaker; needs a demo that is reproducible, explainable, and green on stage.
- **Secondary:** conference audiences and repo visitors — engineers evaluating resilience patterns and/or BMAD; they read stories, tests, and ADRs as the product.

## Scope

**Same externally observable behavior as `main`.** The external contract (endpoints, ports, message topics, CloudEvents, trace propagation) and the five invariants — exactly-once, zero message loss, balance conservation, terminal-state completeness, per-key ordering — are fixed by `docs/bmad/constraints.md` (the binding guardrail contract for all downstream BMAD artifacts).

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
- **Doc drift between BMAD artifacts and system docs** — source-of-truth rule in AGENTS.md; ARCHITECTURE.md regenerated from code, last.
- **BMAD version churn** — installed version pinned (BMM v6.11.0, TEA v1.23.3); no mid-project reinstall.

## Inputs & references

- `ARCHITECTURE.md` (system truth today), `docs/adr/ADR-001…007`, `README.md`, `AGENTS.md`
- `docs/bmad/constraints.md` — binding contract (invariants, external API, conventions, test rules, A1–A8)
