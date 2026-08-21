# Addendum — CoreBankDemo Rebuild Brief

Depth that belongs in downstream documents (PRD, architecture, epics), preserved here so it is not lost.

## Proposed epic order (input to bmad-create-epics-and-stories)

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

Strict dependency order E0 → E1 → … → E7. ~30 stories total. Stories sized ≤ one class-cluster (agent context guardrail).

## Rejected alternatives

- **Parallel `v2/` folder rebuild** — rejected: path/namespace churn, repo temporarily doubles; git history on `main` already preserves the reference implementation.
- **Fresh repository** — rejected: loses ADRs, project skills, and the repo's identity as *the* demo.
- **TUnit as test framework** — rejected in favor of xUnit: audience familiarity beats novelty for talk material.
- **Rebuilding LoadTestSupport through the story mill first** — rejected: it is the acceptance harness; it conforms to the rebuilt system (E6), not the other way around.

## Verification tiers (input to PRD non-functionals)

1. **Story tier:** `dotnet build` + `dotnet test` on `CoreBankDemo.Rebuild.slnf`, coverlet threshold ≥90% line.
2. **Epic tier:** E3/E4 TEA coverage-gap review; E5 `aspire-launch` boot + `.http` smoke + trace check via `aspire-mcp`.
3. **Milestone tier (after E6, E7):** full `/run-load-tests`: reset_database → k6 → poll_until_drained → get_assertion_results (five invariants) → trace analysis.
