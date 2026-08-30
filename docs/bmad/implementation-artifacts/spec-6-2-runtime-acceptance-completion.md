---
title: 'Story 6.2: Runtime acceptance completion'
type: 'chore'
created: '2026-08-30'
status: 'done'
review_loop_iteration: 0
baseline_commit: '395012a0493b2cdfe5580da208263043521401df'
context:
  - '{project-root}/docs/bmad/implementation-artifacts/spec-6-2-renewable-redis-distributed-locking.md'
  - '{project-root}/docs/bmad/implementation-artifacts/epic-6-context.md'
  - '{project-root}/.claude/skills/aspire-launch/SKILL.md'
  - '{project-root}/.claude/skills/aspire-mcp/SKILL.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Story 6.2's Redis locking implementation and review are complete, but its real regular-AppHost acceptance task remained unchecked because the prior environment could not start DCP. The story spec says `done` while sprint tracking correctly remains at `review`, leaving completion inconsistent and the production composition unproven.

**Approach:** Restart the regular AppHost through Aspire in the now-working devcontainer, prove the live graph and a unique end-to-end payment/event flow use healthy Redis-backed processing plus Dapr pub/sub without a Dapr lock component, then record the evidence and mark Story 6.2 done consistently.

## Boundaries & Constraints

**Always:** Use Aspire CLI lifecycle and health waits, not `dotnet run` or sleeps. Verify one shared healthy Redis resource; healthy CoreBankAPI and PaymentsAPI resources and Dapr sidecars; absence of an active lockstore resource/component; runtime Redis lock acquisition by both processing APIs; and an end-to-end unique payment whose Payments outbox, CoreBank inbox, CoreBank messaging outbox, and Payments inbox all complete. Preserve the current Story 6.3 topology. Update the original Story 6.2 task, append runtime evidence, and move its sprint key from `review` to `done` only after every assertion passes.

**Ask First:** Any production code, topology, package, database schema, lock behavior, or Dapr pub/sub change; deleting persistent data; or weakening the original acceptance criteria.

**Never:** Rewrite Story 6.2's frozen intent, claim completion from static wiring alone, use the NoOp lock fallback as runtime proof, modify Story 6.3 status, or push changes.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Clean AppHost restart | Regular AppHost stopped, then started through Aspire | Redis, both APIs, and both Dapr sidecars become healthy; no lockstore resource appears | Any unhealthy required resource blocks completion |
| Runtime lock selection | Both APIs process background partitions | Logs show `RedisDistributedLockService` acquiring/releasing partition locks | NoOp or missing lock activity blocks completion |
| End-to-end pub/sub | Unique valid payment between regular seeded accounts | All four durable message stages reach `Completed` exactly once | Retry, failure, loss, or duplication blocks completion |
| Tracking reconciliation | Runtime assertions passed | Original task checked, evidence appended, sprint key set to `done` | No status update on partial proof |

</frozen-after-approval>

## Code Map

- `docs/bmad/implementation-artifacts/spec-6-2-renewable-redis-distributed-locking.md:81-88` -- original unchecked AppHost task and acceptance criteria; update only its mutable execution/evidence sections.
- `docs/bmad/implementation-artifacts/sprint-status.yaml:79-83` -- authoritative story tracking; only `6-2-renewable-redis-distributed-locking` moves from `review` to `done`.
- `CoreBankDemo.AppHost/AppHost.cs:33-107` -- read-only topology evidence: one Redis resource, Redis references/waits for both APIs, Dapr pub/sub retained.
- `CoreBankDemo.ServiceDefaults/RedisDistributedLockService.cs:44-113` -- runtime logger category and expected acquire/release behavior.
- `CoreBankDemo.PaymentsAPI/CoreBankDemo.http:1-13` -- reusable valid regular-AppHost account fixture; use a unique idempotency key for this proof.
- `CoreBankDemo.CoreBankAPI/Program.cs:19` and `CoreBankDemo.PaymentsAPI/Program.cs:11` -- read-only Redis client registration expected to select the real adapter.

## Tasks & Acceptance

**Execution:**
- [x] Running regular AppHost -- stop/start with Aspire and wait for Redis, both APIs, and both Dapr sidecars; inspect the graph for exactly one Redis and no lockstore.
- [x] Running APIs and PostgreSQL -- submit a unique valid payment, capture Redis lock logs, and query all four durable stages for exactly-once `Completed` state.
- [x] `docs/bmad/implementation-artifacts/spec-6-2-renewable-redis-distributed-locking.md` and `docs/bmad/implementation-artifacts/sprint-status.yaml` -- replace the stale blocked note with runtime evidence, check the final task, and reconcile tracking to `done`.

**Acceptance Criteria:**
- Given the regular AppHost is stopped, when it is started through Aspire, then one Redis resource, both APIs, and both Dapr sidecars become healthy and no lockstore resource is present.
- Given the healthy graph, when background partition processors run, then both APIs emit Redis lock acquire/release logs rather than using the NoOp fallback.
- Given a unique valid payment, when processing drains, then each durable outbox/inbox stage contains exactly one completed record and no failed or retried record for that transaction.
- Given all runtime assertions pass, when documentation is reconciled, then Story 6.2 has no unchecked task and sprint status is `done`.

## Spec Change Log

- 2026-08-30: Completed all runtime acceptance tasks against the regular AppHost and reconciled the original Story 6.2 spec and sprint tracking.

## Design Notes

Story 6.3 is concurrently changing replica topology. This completion pass validates Story 6.2 against the current graph without claiming or modifying Story 6.3; Redis lock coordination and Dapr pub/sub remain the only concerns.

## Verification

**Commands:**
- `aspire stop/start/wait --apphost CoreBankDemo.AppHost/CoreBankDemo.AppHost.csproj --non-interactive` inside the devcontainer -- expected: required resources healthy.
- `aspire describe` and active-reference search -- expected: one `redis`, no `lockstore`, pub/sub sidecars retained.
- Submit one uniquely keyed regular-account payment and query PostgreSQL durable tables -- expected: exactly one completed row at every stage, zero retries/failures.
- `aspire logs corebank-api` and `aspire logs payments-api` -- expected: Redis lock acquisition/release activity.
- `git diff --check` -- expected: clean.

**Runtime evidence (2026-08-30):**

- Restarted the regular AppHost through `aspire stop` / `aspire start` with the optional Dev Proxy disabled, then successfully waited for `redis`, `corebank-api`, `payments-api`, `corebank-api-dapr-cli`, and `payments-api-dapr-cli`.
- `aspire describe` showed exactly one healthy Redis resource, both APIs and both Dapr sidecars healthy, and no lockstore resource.
- Both API logs showed `CoreBankDemo.ServiceDefaults.RedisDistributedLockService` acquiring and releasing Inbox/Outbox partition locks.
- Payment `story-6-2-runtime-20260830T093621Z-28656` returned `202 Accepted`. Direct PostgreSQL assertions found exactly one completed, zero-retry, error-free record for the Payments outbox, CoreBank inbox, CoreBank `transaction.completed` messaging-outbox event, and Payments `transaction.completed` inbox event. The two expected `balance.updated` events also completed exactly once through both event stores, proving Dapr pub/sub remained operational.
- The original Story 6.2 final task is checked and `6-2-renewable-redis-distributed-locking` is `done` in sprint tracking; Story 6.3 tracking was not changed.

## Suggested Review Order

**Runtime acceptance evidence**

- Start with the closed original task and its concise end-to-end runtime proof.
  [`spec-6-2-renewable-redis-distributed-locking.md:81`](spec-6-2-renewable-redis-distributed-locking.md#L81)

- Review the detailed commands, identifiers, and durable-state assertions supporting completion.
  [`spec-6-2-runtime-acceptance-completion.md:81`](spec-6-2-runtime-acceptance-completion.md#L81)

**Tracking reconciliation**

- Confirm only Story 6.2 moved to done while Story 6.3 remained untouched.
  [`sprint-status.yaml:81`](sprint-status.yaml#L81)
