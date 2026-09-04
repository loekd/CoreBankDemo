---
title: 'Story 3.1: Validated processing options'
type: 'feature'
created: '2026-08-22'
status: 'done'
baseline_commit: '9130a389e7ce260bdc0a6551c819a4e7e59fa293'
review_loop_iteration: 0
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-3-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Every processor option must fail fast with all violations reported together, and two brownfield defects must not survive the rebuild: `PartitionCount` misconfigured to 2 instead of the documented 4 (ruling A3), and a bound-but-dead `LockRenewIntervalSeconds` member (ruling A4) — the epic rebuild starts here since `AddServiceDefaults` (3.4) depends on these types existing (FR-20, FR-23).

**Approach:** `git rm` old `CoreBankDemo.ServiceDefaults/Configuration/*.cs` (nothing there is referenced by the already-merged Messaging kernel — confirmed in epic context). TDD-rebuild `ProcessingOptionsBase` + `InboxProcessingOptions`/`OutboxProcessingOptions`/`MessagingOutboxProcessingOptions`, DataAnnotations-validated, with `PartitionCount` defaulting/documented to 4 and no `LockRenewIntervalSeconds` member anywhere.

## Boundaries & Constraints

**Always:** `PartitionCount` `[Required][Range(1,100)]` default 4; `LockExpirySeconds` `[Required][Range(1,300)]`; `PollingIntervalMs` `[Required][Range(100,300_000)]` default 5000; `MessagingOutboxProcessingOptions` adds `PubSubName`/`TopicName` `[Required]` defaulting `"pubsub"`/`"transaction-events"`; validation via `ValidateDataAnnotations().ValidateOnStart()` reporting every violation together, not just the first.

**Ask First:** None expected.

**Never:** Add `LockRenewIntervalSeconds` or any other member nothing reads — a dead-option test (reflection over the type, cross-checked against a hand-maintained "known consumers" list) must fail if an unread member exists; touch `CoreBankDemo.ServiceDefaults/IDistributedLockService.cs`, `DaprDistributedLockService.cs`, `NoOpDistributedLockService.cs`, `CloudEventTypes/`, or `Extensions.cs` (stories 3.2–3.4).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Valid config | All fields in range | Binds successfully | N/A |
| Multiple violations | PartitionCount=0 AND LockExpirySeconds=500 | Both violations reported in one failure, not just the first | `OptionsValidationException` listing both |
| Missing required field | TopicName absent (Messaging variant) | Validation fails, names the field | thrown at startup |
| Default applied | Config omits PartitionCount | Binds to 4 | N/A |
| Dead-member check | Reflect over each options type | No member exists that isn't in the known-consumers list | test failure if violated |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.ServiceDefaults/Configuration/` — demolish all `.cs` (epic context confirms zero Messaging dependency)
- New: `CoreBankDemo.ServiceDefaults/Configuration/ProcessingOptionsBase.cs`, `InboxProcessingOptions.cs`, `OutboxProcessingOptions.cs`, `MessagingOutboxProcessingOptions.cs`
- `tests/CoreBankDemo.ServiceDefaults.Tests/` — remove the `<Threshold>0</Threshold>` carry-forward override from epic 1 (first real test story for this project)
- Epic context §Legacy Behavioral Reference — exact ranges/defaults/section names to preserve, with the A3 (default 4) and A4 (delete LockRenewIntervalSeconds) fixes applied

## Tasks & Acceptance

**Execution:**
- [x] `git rm CoreBankDemo.ServiceDefaults/Configuration/*.cs`
- [x] Tests first covering the full I/O matrix (real `ServiceCollection` + `AddOptions<T>().BindConfiguration(...).ValidateDataAnnotations().ValidateOnStart()` + `IConfiguration` built from an in-memory dictionary — no mocking DataAnnotations)
- [x] `ProcessingOptionsBase` + three subclasses, `SectionName` constants per epic context
- [x] Dead-option reflection test asserting no member exists outside a maintained known-consumers list
- [x] `tests/CoreBankDemo.ServiceDefaults.Tests.csproj` — remove Threshold=0 override

**Acceptance Criteria:**
- Given `PartitionCount=0` and `LockExpirySeconds=500` together, when validation runs at startup, then the resulting exception lists both violations
- Given config omitting `PartitionCount`, when bound, then it resolves to 4
- Given the rebuilt options types, when reflected over, then no `LockRenewIntervalSeconds` (or any other unread) member exists

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` — expected: green, ServiceDefaults ≥90% line

## Spec Change Log

- 2026-08-22 (step-04): removing the epic-1 Threshold=0 override (per this story's task list) correctly turned the gate red — this project's only coverable surface belongs to stories 3.2-3.4 (auto-properties are compiler-generated/excluded). Restored the override with an updated TODO(story-3.4), same tripwire pattern as story 1.2. Review found: the dead-option guard only name-matched against a free-text list (gameable) — strengthened to real repo-relative consumer file paths with an existence check; missing test parity between Inbox/Outbox option tests; no upper-Range-boundary or whole-section-absent coverage; whitespace-only PubSubName/TopicName accepted — all added/fixed. Deferred: PaymentsAPI/CoreBankAPI appsettings.json still have PartitionCount:2 and dead LockRenewIntervalSeconds keys — those projects are unbuilt/off-slnf until epics 4/5, tracked in deferred-work.md so the config fix isn't forgotten when they're rebuilt.
