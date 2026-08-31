# Story 7.3 Continuation Handoff

## Resume Point

- Story spec: `docs/bmad/implementation-artifacts/spec-7-3-k6-run-and-first-full-acceptance-gate.md`
- Spec status: `in-progress`
- Sprint key: `7-3-k6-run-and-first-full-acceptance-gate`
- Sprint status: `in-progress`
- Baseline commit: `303ac0f9e8dce0b13adfc7d859d22f877f49c3d1`
- Workflow snapshot used: `_bmad/render/bmad-build/corebankdemo-4d866ab4279c/1ecb19d9aca3d728e704/`
- Current workflow position: Step 3 implementation, blocked during distributed acceptance verification. Do not advance to Step 4 review until every task and acceptance criterion is complete.

To resume through BMAD, invoke `bmad-build` with the existing Story 7.3 spec path. Its `in-progress` status should route directly back to implementation.

## Completed Work

The implementation agent completed the code and lower-tier test work described by the approved spec:

- `CoreBankDemo.LoadTestSupport/Services/LoadTestAssertionService.cs`
  - Added summaries for all four message stores.
  - Made failed/non-terminal checks four-store complete.
  - Added N/N/3N/3N stage-cardinality and exact canonical-account checks.
  - Preserved existing REST fields and REST/MCP output parity.
- `k6/script.js`
  - Added a fail-closed checks threshold.
  - Hardened setup, drain, malformed JSON, endpoint failure, and assertion paths so they record failed checks rather than returning apparent success.
- `CoreBankDemo.LoadTestInitializer/ResetResponseValidator.cs` and `Program.cs`
  - Added semantic validation of reset account count, total balance, and initial balance before k6 can start.
- `docs/adr/ADR-017-dapr-w3c-trace-context.md`
  - Supersedes the traceparent-only publisher contract.
- `IEventPublisher`, `DaprEventPublisher`, and `DaprOutboxDeliveryStrategy`
  - Propagate persisted `TraceState` as `cloudevent.tracestate` alongside `traceparent`.
- `InboxProcessorBase` and `OutboxProcessorBase`
  - Added stable store/message tags to processing spans for live ordering/exclusivity analysis.
- Tests
  - Added unit and PostgreSQL integration coverage for assertion behavior.
  - Added `tests/CoreBankDemo.LoadTestInitializer.Tests/`.
  - Added `K6ScriptContractTests.cs`.
  - Added replicated CoreBank Inbox ordering/exclusivity coverage to complement the existing replicated Outbox test.
- Documentation
  - Updated `.claude/skills/load-test/SKILL.md`, `.claude/commands/run-load-tests.md`, and `CoreBankDemo.LoadTests/README.md` to use only the disposable LoadTests AppHost.

The first five execution tasks in the spec are checked. The final acceptance-evidence task remains unchecked.

## Verification Already Reported

The implementation agent reported:

- `dotnet test CoreBankDemo.Rebuild.slnf`: 848 passed in total.
- Unit tier: 663 passed; one pre-existing real-Redis test skipped.
- Persistence tier: 185 passed, zero failed, zero skipped.
- All measured projects passed the 90% line-coverage gate.
- Persistence aggregate line coverage: 98.55%.
- `node --check k6/script.js`: passed.
- `git diff --check`: passed.
- Focused rerun after restoring two test files:
  - `K6ScriptContractTests`: 9 passed.
  - `ReplicatedCoreBankInboxProcessorTests`: 1 passed against PostgreSQL and Redis Testcontainers.

Treat these as prior evidence, not a substitute for rerunning the gate after reviewing the current worktree.

## Why Work Stopped

The required distributed acceptance run could not be completed.

1. The implementation subagent had an Aspire CLI, but its sandbox blocked Aspire DCP's loopback IPv6 control endpoint with a default-deny network policy before any resource was created.
2. This session then retried:

   ```text
   aspire start --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --non-interactive
   ```

   It failed with `aspire: command not found`.
3. The user attempted to install Aspire with `curl -sSL https://aspire.dev/install.sh | bash`; the network tunnel returned HTTP 502.

No PostgreSQL, Redis, API, initializer, k6, or Jaeger resource started in either usable attempt. No banking invariant failed; this is an environment/tooling blocker.

The partial record is in `docs/bmad/implementation-artifacts/7-3-acceptance-evidence.md`. Its final verdict is correctly `not accepted`.

## Remaining Required Work

### 1. Review and rerun lower tiers

Before distributed execution:

1. Inspect the complete diff for correctness and unintended contract changes.
2. Run `dotnet test CoreBankDemo.Rebuild.slnf`.
3. Run `git diff --check`.
4. Confirm all matrix rows have executed tests, especially hidden failures in each store and malformed/failed k6 endpoint paths.

Do not mark the final spec task complete from source-contract tests alone.

### 2. Run the single disposable topology

Use the `aspire-launch`, `load-test`, `aspire-mcp`, and `corebank-trace-analysis` skills. Start only:

```text
CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj
```

Do not start `CoreBankDemo.AppHost`; the LoadTests graph already owns disposable PostgreSQL, Redis, Jaeger, two API replicas, LoadTestSupport, the one-shot reset initializer, and automatic k6 execution. Starting both graphs causes fixed-port conflicts.

Record exact UTC start/end timestamps and the effective transaction/VU configuration. The acceptance workload is 100 unique payments plus 10 deliberate duplicate submissions.

### 3. Collect state-gate evidence

Verify and retain:

- Reset initializer completed successfully before k6 started.
- k6 resource exited 0.
- `/assert/drain` reports all four stores drained.
- `/assert/results?expectedUnique=100` reports `allPassed: true`.
- REST and MCP assertion JSON are field-for-field identical for the same run.
- Completed row counts are:
  - Payments Outbox: 100
  - CoreBank Inbox: 100
  - CoreBank Messaging Outbox: 300
  - Payments Inbox: 300
- Every store has zero `Pending`, `Processing`, and `Failed` rows.
- The exact 10 canonical LOAD accounts pass conservation and replay checks.

Do not call reset a second time in the same AppHost generation; initialization/reset release is intentionally one-shot.

### 4. Collect trace and ordering evidence

Analyze the exact run window, not a default time range:

- Call the trace backend's service-list operation first.
- Check errors and slow traces separately for PaymentsAPI and CoreBankAPI.
- Select representative payment traces and verify parentage across:
  - k6/HTTP intake into PaymentsAPI
  - Payments Outbox processing to CoreBank over HTTP
  - CoreBank Inbox processing and domain-event enqueue
  - CoreBank Outbox publication over Dapr
  - Payments event intake and Inbox processing
- Confirm both `traceparent` and `tracestate` survive both transport hops.
- Confirm both API replicas participated.
- Group tagged processor spans by `messaging.store` and `PartitionId`; verify no same-store/partition intervals overlap.
- Combine this live evidence with the passing replicated PostgreSQL/Redis Inbox and Outbox tests. This is the user-approved proof for the fifth invariant; k6 state alone is insufficient.

If Jaeger is unavailable, follow the trace-analysis skill's fail-fast rule. The story remains unaccepted rather than weakening trace acceptance.

### 5. Finish artifacts and workflow

After a fully green run:

1. Replace the blocked-run sections in `7-3-acceptance-evidence.md` with the real configuration, timestamps, resource outcomes, final REST/MCP JSON, row counts, representative trace IDs, ordering analysis, and failure classifications encountered while reaching green.
2. Check the final execution task in the Story 7.3 spec.
3. Verify every acceptance criterion and every I/O matrix row.
4. Continue the rendered BMAD workflow by reading and following `step-04-review.md` from the workflow snapshot. Do not manually improvise review/status transitions.
5. Do not commit or push unless the user explicitly requests it.

## Known Risks to Review

- The new k6 tests inspect script contracts; the live run must prove actual container exit behavior and cannot be replaced by string assertions.
- Ensure additive assertion fields did not alter pre-existing JSON names consumed by k6, MCP clients, or DemoRunner.
- Confirm four-store cardinality semantics match the all-success workload: one Payments/CoreBank command row and three CoreBank/Payments event rows per unique transaction.
- Confirm exact-account filtering cannot include arbitrary account numbers merely containing `LOAD`.
- Confirm ADR-017, interface signature tests, Dapr metadata, and live CloudEvent handling agree on `tracestate` spelling and propagation semantics.
- Story 7.4 remains out of scope and dependency-gated. Do not bind or complete DemoRunner as part of 7.3.

## Filesystem Casing Hazard

This workspace exposes case-only path aliases for test directories. For example, the canonical and lowercase spellings reported the same inode. `git status` may display both spellings for the two new test files:

- `tests/CoreBankDemo.LoadTestSupport.Tests/K6ScriptContractTests.cs`
- `tests/CoreBankDemo.Persistence.IntegrationTests/CoreBankApi/ReplicatedCoreBankInboxProcessorTests.cs`

Do not delete a lowercase-looking duplicate without checking inode/path behavior first; doing so previously removed the canonical file too. Keep and stage only the repository's canonical casing if a future commit is requested.
