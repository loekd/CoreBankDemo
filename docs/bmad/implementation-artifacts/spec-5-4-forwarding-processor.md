---
title: 'Story 5.4: Forwarding processor'
type: 'feature'
created: '2026-08-29'
status: 'done'
review_loop_iteration: 1
followup_review_recommended: false
baseline_commit: 'd6d3b4c37853b9b3b9c845ad147bef25f24449f3'
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-5-context.md'
  - '{project-root}/docs/bmad/implementation-artifacts/spec-5-3-contract-generated-kiota-corebank-client.md'
  - '{project-root}/.claude/skills/messaging-patterns/SKILL.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** PaymentsAPI stores payments idempotently (5.1, 5.2) and has a contract-driven CoreBank client (5.3), but nothing forwards a stored payment to CoreBankAPI yet — the outbox never drains.

**Approach:** Add a concrete `OutboxProcessorBase<OutboxMessage>` for PaymentsAPI, delivering through a new `IOutboxDeliveryStrategy<OutboxMessage>` that validates the destination account then submits the transaction via the existing `ICoreBankApiClient` (story 5.3), mirroring CoreBankAPI's `MessagingOutboxProcessor`/`DaprOutboxDeliveryStrategy` pattern exactly.

## Boundaries & Constraints

**Always:** Reuse `OutboxProcessorBase<TMessage>` unchanged — the strategy only delivers; the base class still owns claim/lock/complete/retry. `IOutboxDeliveryStrategy.DeliverAsync` returns normally only when `ICoreBankApiClient` reports `CoreBankClientOutcome.Success` for both the destination-account validation and the transaction submission (including a duplicate-accept replay); it throws for every other outcome — a non-2xx, malformed response, timeout, transport exception, or `IsValid = false` — so the base class's existing retry/`MarkAsFailedWithRetryAsync`/terminal-`Failed`-at-`MaxRetryCount` path handles it exactly as any other delivery failure, with no new "immediate terminal failure" path. Caller cancellation from `ICoreBankApiClient` propagates unchanged (never caught and reclassified). Lock name prefix is `payments-outbox`. No Kiota-generated type crosses out of the strategy — only `ICoreBankApiClient`'s application-owned contracts.

**Ask First:** Any change to `OutboxProcessorBase`, `IOutboxDeliveryStrategy`, or `ICoreBankApiClient` itself; treating an invalid destination account as anything other than a retry-then-eventually-`Failed` outcome; adding retry logic inside the strategy (retries stay kernel-owned).

**Never:** Reimplement polling/locking/claiming/completion — that is `OutboxProcessorBase`'s job. Add a second CoreBank transport. Skip the destination-account validation call before submitting.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|---------------|----------------------------|----------------|
| Valid destination, submission succeeds (2xx) | `ValidateAccountAsync` → Success, IsValid=true; `ProcessTransactionAsync` → Success | `DeliverAsync` returns; message marked `Completed` | N/A |
| Duplicate-accept replay | `ProcessTransactionAsync` → Success (200 cached replay) | Same as above — Completed | N/A |
| Invalid destination account | `ValidateAccountAsync` → Success, IsValid=false | `DeliverAsync` throws | Kernel retry path; `Failed` after `MaxRetryCount` |
| CoreBank rejects submission (non-2xx) | `ProcessTransactionAsync` → Retry (TransportRejection) | `DeliverAsync` throws with status preserved in the message | Kernel retry path |
| Timeout / transport exception | Either call → Retry (Timeout/TransportException) | `DeliverAsync` throws | Kernel retry path |
| Caller cancellation | Ambient token cancelled during either call | `OperationCanceledException` propagates unchanged | Left `Processing`, not retried or failed |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.CoreBankAPI/Outbox/DaprOutboxDeliveryStrategy.cs`, `MessagingOutboxProcessor.cs` -- read-only sibling pattern to mirror exactly (strategy throws on failure/returns on success; processor only overrides `LockNamePrefix` and options mapping).
- `CoreBankDemo.Messaging/OutboxProcessorBase.cs` -- read-only; owns the loop, lock, claim, completion, and retry/`MaxRetryCount` classification. Delivery strategy throwing = retry; returning = `MarkAsCompletedAsync`.
- `CoreBankDemo.PaymentsAPI/Outbox/ICoreBankApiClient.cs`, `CoreBankApiContracts.cs` -- read-only existing port (story 5.3): `ValidateAccountAsync`, `ProcessTransactionAsync`, `CoreBankResult<T>`/`CoreBankClientOutcome`.
- `CoreBankDemo.PaymentsAPI/Outbox/OutboxMessage.cs` -- read-only; already carries `FromAccount`, `ToAccount`, `Amount`, `Currency`, `TransactionId` needed to build `TransactionSubmissionRequest`.
- `CoreBankDemo.PaymentsAPI/Outbox/OutboxRepository.cs` -- read-only; already implements `IOutboxMessageStore<OutboxMessage>` via `OutboxMessageRepositoryBase`, just not yet exposed under that interface in DI.
- `CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs` (new) -- `IOutboxDeliveryStrategy<OutboxMessage>`: validate `ToAccount`, then `ProcessTransactionAsync`; throw with the `CoreBankRetryReason`/status in the exception message on any non-`Success` outcome.
- `CoreBankDemo.PaymentsAPI/Outbox/PaymentsOutboxProcessor.cs` (new) -- `OutboxProcessorBase<OutboxMessage>`, `LockNamePrefix => "payments-outbox"`, options mapped from `IOptions<OutboxProcessingOptions>` (already registered/validated at `PartitionCount == 4` in `PaymentStorageServiceCollectionExtensions`).
- `CoreBankDemo.PaymentsAPI/Program.cs` -- register `IOutboxMessageStore<OutboxMessage>` (delegate to `OutboxRepository`), `IOutboxDeliveryStrategy<OutboxMessage>` → `HttpForwardOutboxDeliveryStrategy`, `AddHostedService<PaymentsOutboxProcessor>()`.
- `tests/CoreBankDemo.CoreBankAPI.Tests/MessagingOutboxProcessorTests.cs` -- read-only sibling test-shape precedent (`StartAsync_publishes_and_completes_a_claimed_row`, `..._applies_the_kernel_retry_transition`, `Concrete_processor_overrides_only_the_lock_name_prefix`).
- `tests/CoreBankDemo.Messaging.Tests/OutboxProcessorBaseTests.cs` -- read-only; already proves per-partition fan-out/ordering at the kernel level (`Tick_fans_out_over_every_partition_under_its_own_lock_name`) — story 5.4 need only prove its own strategy's outcome classification, not re-derive kernel ordering.
- `tests/CoreBankDemo.PaymentsAPI.Tests/HttpForwardOutboxDeliveryStrategyTests.cs` (new) -- fake `ICoreBankApiClient`: valid+submit-success → completes; invalid account, non-2xx, malformed, timeout, transport exception, duplicate-accept-success → each classified correctly.
- `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsOutboxProcessorTests.cs` (new) -- mirrors `MessagingOutboxProcessorTests.cs`'s shape plus one interleaving test: two partitions' claimed batches deliver via a fake client without cross-partition reordering.

## Tasks & Acceptance

**Execution:**

- [x] `CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs` -- implement validate-then-submit delivery, throwing on any non-`Success` outcome from either call.
- [x] `CoreBankDemo.PaymentsAPI/Outbox/PaymentsOutboxProcessor.cs` -- concrete processor, `payments-outbox` lock prefix.
- [x] `CoreBankDemo.PaymentsAPI/Program.cs` -- register the store interface, strategy, and hosted service.
- [x] `tests/CoreBankDemo.PaymentsAPI.Tests/HttpForwardOutboxDeliveryStrategyTests.cs` -- cover every classification in the I/O matrix below.
- [x] `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsOutboxProcessorTests.cs` -- publish+complete, retry-transition, lock-prefix override, and the interleaving/ordering proof.

**Acceptance Criteria:**

- Given `PaymentsOutboxProcessor` on `OutboxProcessorBase` with `HttpForwardOutboxDeliveryStrategy`, when a message processes, then the destination account is validated, the transaction is submitted, a 2xx outcome (including duplicate-accept) marks it `Completed`, and anything else enters the kernel retry path, reaching `Failed` only after `MaxRetryCount`.
- Given concurrent partitions with claimed messages, when a tick runs, then delivery order within each partition is preserved and different partitions demonstrably progress independently (interleaving test).
- Given `dotnet test CoreBankDemo.Rebuild.slnf`, when story 5.4 is complete, then all rebuild tests pass and PaymentsAPI stays at or above its coverage threshold.

## Spec Change Log

- **Review finding (patch):** verification-gap review found that Program.cs's three new registrations (`IOutboxMessageStore<OutboxMessage>`, `IOutboxDeliveryStrategy<OutboxMessage>`, `AddHostedService<PaymentsOutboxProcessor>`) had no test replaying Program.cs's actual composition — `PaymentsOutboxProcessorTests` builds its own parallel `ServiceCollection`, and Program.cs is excluded from the coverage gate, so a dropped or mis-wired line would silently leave the outbox processor never forwarding payments. Added `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsOutboxWiringTests.cs`, mirroring the `RedisLockWiringTests.cs` pattern from story 6.2: replays Program.cs's real registration sequence and asserts the composed graph resolves correctly. No code changes to the delivery strategy or processor were needed.
- **Follow-up review finding (patch):** strengthened the prior wiring regression test to boot the real `Program` entry point rather than reconstructing its registrations. Also added exact cancellation-token forwarding assertions, observable lock-expiry and polling-interval option mapping, and a two-partition barrier that fails if partition processing becomes serial.

## Design Notes

The strategy is intentionally thin — all classification already happened in `KiotaCoreBankApiClient` (story 5.3); this story only decides what a non-`Success` `CoreBankResult` *means* for outbox delivery (throw, so the kernel's existing retry machinery owns the rest). No new retry, backoff, or terminal-failure logic is added here.

## Verification

**Commands:**

- `dotnet build CoreBankDemo.Rebuild.slnf` -- expected: green.
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: all unit projects green, PaymentsAPI ≥90% line coverage.
- `dotnet test --filter "FullyQualifiedName~HttpForwardOutboxDeliveryStrategyTests|FullyQualifiedName~PaymentsOutboxProcessorTests"` -- expected: every I/O matrix row covered and passing.

## Suggested Review Order

**Delivery classification (the core decision)**

- Entry point: validates the destination, then submits — throw = retry, return = complete.
  [`HttpForwardOutboxDeliveryStrategy.cs:41`](../../CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs#L41)

- A successful (2xx) validation whose body says the account is invalid still must not proceed.
  [`HttpForwardOutboxDeliveryStrategy.cs:59`](../../CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs#L59)

- Every non-`Success` outcome throws, preserving the retry reason and status for `LastError`.
  [`HttpForwardOutboxDeliveryStrategy.cs:90`](../../CoreBankDemo.PaymentsAPI/Outbox/HttpForwardOutboxDeliveryStrategy.cs#L90)

**Processor composition (kernel reuse)**

- Concrete processor only overrides the lock prefix and the options mapping, nothing else.
  [`PaymentsOutboxProcessor.cs:43`](../../CoreBankDemo.PaymentsAPI/Outbox/PaymentsOutboxProcessor.cs#L43)

- DI wiring turns the processor on; exposes `OutboxRepository` under the kernel's store port.
  [`Program.cs:24`](../../CoreBankDemo.PaymentsAPI/Program.cs#L24)

**Verification**

- Covers every I/O-matrix row against a fake client: success, duplicate-accept, invalid account, retry outcomes, cancellation.
  [`HttpForwardOutboxDeliveryStrategyTests.cs:194`](../../tests/CoreBankDemo.PaymentsAPI.Tests/HttpForwardOutboxDeliveryStrategyTests.cs#L194)

- Interleaving proof: two partitions progress independently without cross-partition reordering.
  [`PaymentsOutboxProcessorTests.cs:470`](../../tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsOutboxProcessorTests.cs#L470)

- Review-added: replays Program.cs's real registration sequence to close the DI-wiring verification gap.
  [`PaymentsOutboxWiringTests.cs:26`](../../tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsOutboxWiringTests.cs#L26)

## Review Triage Log

### 2026-08-30 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 1, medium 3, low 0)
- defer: 0
- reject: 35: (high 0, medium 0, low 35)
- addressed_findings:
  - `[high]` `[patch]` The wiring test reconstructed registrations and could not detect removal from `Program.cs`; replaced it with a real-entry-point `WebApplicationFactory<Program>` composition test.
  - `[medium]` `[patch]` Delivery tests did not observe cancellation-token forwarding; recorded and asserted the exact token at both CoreBank client calls.
  - `[medium]` `[patch]` Concrete processor option mapping was not behaviorally observed; asserted lock expiry and measured the configured interval between processor ticks.
  - `[medium]` `[patch]` The partition test proved ordering but not concurrent progress; added a cross-partition barrier that deadlocks under serial execution.

### 2026-08-30 — Follow-up review pass
- intent_gap: 0
- bad_spec: 0
- patch: 7: (high 1, medium 4, low 2)
- defer: 0
- addressed_findings:
  - `[high]` `[patch]` Asserted amount and currency as well as account and transaction identities on the forwarded request.
  - `[medium]` `[patch]` Exercised real caller cancellation through the composed processor during validation and submission, proving the row remains `Processing` without consuming a retry.
  - `[medium]` `[patch]` Proved all four configured partitions are attempted and bounded the configured polling interval on both sides.
  - `[medium]` `[patch]` Used a non-default lock expiry so hard-coded option mappings cannot pass.
  - `[medium]` `[patch]` Preserved validation HTTP status diagnostics in the asserted retry exception.
  - `[low]` `[patch]` Asserted exactly one validation/submission attempt so strategy-local retries cannot be introduced silently.
- verification:
  - PaymentsAPI unit suite: 125 passed; 100% line coverage.
  - PostgreSQL integration suite: 145 passed; 99.27% combined line coverage.
  - Full rebuild gate: 682 passed, 1 intentionally skipped Redis integration test; every enforced line-coverage threshold exceeded 90%.
- followup_review_recommended: false

## Auto Run Result

Status: done

Summary of implemented change: Hardened Story 5.4's verification so production composition, cancellation propagation, concrete option mapping, and independent partition progress are directly observable.

Files changed:
- `tests/CoreBankDemo.PaymentsAPI.Tests/HttpForwardOutboxDeliveryStrategyTests.cs` — asserts the caller token reaches validation and submission unchanged.
- `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsOutboxProcessorTests.cs` — verifies lock expiry, polling interval, and genuine cross-partition concurrency.
- `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsOutboxWiringTests.cs` — boots the real PaymentsAPI entry point and validates its forwarding graph.
- `docs/bmad/implementation-artifacts/spec-5-4-forwarding-processor.md` — records review triage, verification, and completion.

Review findings breakdown: 4 patches applied, 0 items deferred, 35 findings rejected as unrelated later-story findings or unsupported findings from the baseline-wide diff.

Follow-up review recommendation: true — patched findings: high 1, medium 3, low 0; score `3 × 3 + 0 = 9`, with a high-severity patch also independently requiring follow-up.

Verification performed:
- `dotnet build CoreBankDemo.Rebuild.slnf --no-restore` — succeeded.
- `dotnet test CoreBankDemo.Rebuild.slnf --no-build --no-restore` — 640 passed, 1 skipped; PaymentsAPI line coverage 100%.

Residual risks: The suite still reports the pre-existing `NU1903` advisory for `SQLitePCLRaw.lib.e_sqlite3` 2.1.11. Live Postgres, Redis, and cross-service behavior remain acceptance-tier concerns rather than unit-test coverage.
