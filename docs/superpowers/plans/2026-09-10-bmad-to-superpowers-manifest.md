# BMAD migration manifest

Generated 2026-09-10 by the plan's manifest script. One row per tracked file under `docs/bmad/`.

| Action | Files |
|---|---|
| promote | 1 |
| convert | 64 |
| fold | 5 |
| drop | 32 |
| **total** | **102** |

| Old path | Action | New path | Reason |
|---|---|---|---|
| `docs/bmad/constraints.md` | promote | `docs/constraints.md` | living contract |
| `docs/bmad/implementation-artifacts/spec-1-1-test-package-versions-and-coverage-gate.md` | convert | `docs/superpowers/specs/2026-08-21-story-1-1-test-package-versions-and-coverage-gate-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-1-2-test-projects-and-rebuild-solution-filter.md` | convert | `docs/superpowers/specs/2026-08-21-story-1-2-test-projects-and-rebuild-solution-filter-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-1-3-gate-proof.md` | convert | `docs/superpowers/specs/2026-08-21-story-1-3-gate-proof-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-2-1-identity-constants-and-message-contracts.md` | convert | `docs/superpowers/specs/2026-08-21-story-2-1-identity-constants-and-message-contracts-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-2-2-idempotent-store.md` | convert | `docs/superpowers/specs/2026-08-21-story-2-2-idempotent-store-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-2-3-claiming-retry-and-poison-state-machine.md` | convert | `docs/superpowers/specs/2026-08-21-story-2-3-claiming-retry-and-poison-state-machine-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-2-4-outboxprocessorbase-and-delivery-strategy-port.md` | convert | `docs/superpowers/specs/2026-08-21-story-2-4-outboxprocessorbase-and-delivery-strategy-port-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-2-5-inboxprocessorbase-and-handler-dispatch.md` | convert | `docs/superpowers/specs/2026-08-22-story-2-5-inboxprocessorbase-and-handler-dispatch-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-2-6-kernel-failure-path-hardening.md` | convert | `docs/superpowers/specs/2026-08-22-story-2-6-kernel-failure-path-hardening-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-3-1-validated-processing-options.md` | convert | `docs/superpowers/specs/2026-08-22-story-3-1-validated-processing-options-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-3-2-distributed-lock-port-and-dapr-implementation.md` | convert | `docs/superpowers/specs/2026-08-22-story-3-2-distributed-lock-port-and-dapr-implementation-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-3-3-cloudevent-types-and-publisher-port.md` | convert | `docs/superpowers/specs/2026-08-24-story-3-3-cloudevent-types-and-publisher-port-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-3-4-service-wiring-defaults.md` | convert | `docs/superpowers/specs/2026-08-24-story-3-4-service-wiring-defaults-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-4-1-domain-model-dbcontext-and-seeding.md` | convert | `docs/superpowers/specs/2026-08-24-story-4-1-domain-model-dbcontext-and-seeding-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-4-2-transaction-validation.md` | convert | `docs/superpowers/specs/2026-08-25-story-4-2-transaction-validation-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-4-3-account-repository-and-transaction-executor.md` | convert | `docs/superpowers/specs/2026-08-25-story-4-3-account-repository-and-transaction-executor-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-4-4-idempotent-transaction-intake.md` | convert | `docs/superpowers/specs/2026-08-25-story-4-4-idempotent-transaction-intake-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-4-5-account-endpoints.md` | convert | `docs/superpowers/specs/2026-08-27-story-4-5-account-endpoints-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-4-6-atomic-inbox-execution-with-event-enqueue.md` | convert | `docs/superpowers/specs/2026-08-27-story-4-6-atomic-inbox-execution-with-event-enqueue-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-4-7-event-publishing-processor.md` | convert | `docs/superpowers/specs/2026-08-28-story-4-7-event-publishing-processor-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-5-1-payment-store-and-idempotency-key-handling.md` | convert | `docs/superpowers/specs/2026-08-28-story-5-1-payment-store-and-idempotency-key-handling-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-5-2-payment-intake-endpoint.md` | convert | `docs/superpowers/specs/2026-08-28-story-5-2-payment-intake-endpoint-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-5-3-contract-generated-kiota-corebank-client.md` | convert | `docs/superpowers/specs/2026-08-29-story-5-3-contract-generated-kiota-corebank-client-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-5-4-forwarding-processor.md` | convert | `docs/superpowers/specs/2026-08-29-story-5-4-forwarding-processor-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-5-5-event-subscription-intake.md` | convert | `docs/superpowers/specs/2026-08-29-story-5-5-event-subscription-intake-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-5-6-event-handling-processor.md` | convert | `docs/superpowers/specs/2026-08-29-story-5-6-event-handling-processor-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-6-2-renewable-redis-distributed-locking.md` | convert | `docs/superpowers/specs/2026-08-29-story-6-2-renewable-redis-distributed-locking-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-6-2-runtime-acceptance-completion.md` | convert | `docs/superpowers/specs/2026-08-30-story-6-2-runtime-acceptance-completion-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-6-3-replicated-local-api-topology.md` | convert | `docs/superpowers/specs/2026-08-29-story-6-3-replicated-local-api-topology-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-6-5-opentelemetry-business-metrics.md` | convert | `docs/superpowers/specs/2026-08-29-story-6-5-opentelemetry-business-metrics-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-6-6-remove-sqlite-with-postgresql-testcontainers.md` | convert | `docs/superpowers/specs/2026-08-29-story-6-6-remove-sqlite-with-postgresql-testcontainers-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-6-7-eliminate-dapr-service-invocation.md` | convert | `docs/superpowers/specs/2026-08-29-story-6-7-eliminate-dapr-service-invocation-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-7-1-assertion-api-realignment.md` | convert | `docs/superpowers/specs/2026-08-31-story-7-1-assertion-api-realignment-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-7-2-mcp-server-tools.md` | convert | `docs/superpowers/specs/2026-08-31-story-7-2-mcp-server-tools-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-7-3-k6-run-and-first-full-acceptance-gate.md` | convert | `docs/superpowers/specs/2026-08-31-story-7-3-k6-run-and-first-full-acceptance-gate-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-7-4-reusable-terminal-demo-operator-console.md` | convert | `docs/superpowers/specs/2026-08-29-story-7-4-reusable-terminal-demo-operator-console-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-add-instant-payment-rail.md` | convert | `docs/superpowers/specs/2026-09-02-add-instant-payment-rail-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-add-instant-rail-load-coverage.md` | convert | `docs/superpowers/specs/2026-09-03-add-instant-rail-load-coverage-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-add-rest-client-devcontainer-extension.md` | convert | `docs/superpowers/specs/2026-08-30-add-rest-client-devcontainer-extension-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-demorunner-fault-injection-workspace.md` | convert | `docs/superpowers/specs/2026-09-05-demorunner-fault-injection-workspace-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-demorunner-operations-stage-focus.md` | convert | `docs/superpowers/specs/2026-09-09-demorunner-operations-stage-focus-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-demorunner-outcome-feedback-loop.md` | convert | `docs/superpowers/specs/2026-09-05-demorunner-outcome-feedback-loop-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-demorunner-resource-lifecycle-recovery.md` | convert | `docs/superpowers/specs/2026-09-10-demorunner-resource-lifecycle-recovery-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-evidence-payload-bodies.md` | convert | `docs/superpowers/specs/2026-09-10-evidence-payload-bodies-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-fix-dapr-replica-integration.md` | convert | `docs/superpowers/specs/2026-08-30-fix-dapr-replica-integration-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-fix-payments-http-demo-account-mismatch.md` | convert | `docs/superpowers/specs/2026-08-30-fix-payments-http-demo-account-mismatch-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-instant-rail-cancelled-event.md` | convert | `docs/superpowers/specs/2026-09-08-instant-rail-cancelled-event-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-instant-rail-timeout-cancel.md` | convert | `docs/superpowers/specs/2026-09-08-instant-rail-timeout-cancel-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-refine-kiota-and-local-replicas-backlog.md` | convert | `docs/superpowers/specs/2026-08-28-refine-kiota-and-local-replicas-backlog-design.md` | story spec |
| `docs/bmad/implementation-artifacts/spec-restore-redis-development-password-default.md` | convert | `docs/superpowers/specs/2026-08-30-restore-redis-development-password-default-design.md` | story spec |
| `docs/bmad/planning-artifacts/architecture/architecture-CoreBankDemo-2026-08-21/ARCHITECTURE-SPINE.md` | convert | `docs/superpowers/specs/2026-08-21-architecture-spine-design.md` | planning document |
| `docs/bmad/planning-artifacts/briefs/brief-CoreBankDemo-2026-08-21/addendum.md` | convert | (folded into the sibling brief.md conversion as a final section) | addendum |
| `docs/bmad/planning-artifacts/briefs/brief-CoreBankDemo-2026-08-21/brief.md` | convert | `docs/superpowers/specs/2026-08-21-corebank-rebuild-brief-design.md` | planning document |
| `docs/bmad/planning-artifacts/briefs/brief-demorunner-console-2026-09-03/brief.md` | convert | `docs/superpowers/specs/2026-09-03-demorunner-console-brief-design.md` | planning document |
| `docs/bmad/planning-artifacts/epics.md` | convert | `docs/superpowers/specs/2026-08-21-epics-and-stories-design.md` | planning document |
| `docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-08-21/prd.md` | convert | `docs/superpowers/specs/2026-08-21-corebank-rebuild-prd-design.md` | planning document |
| `docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10-resource-lifecycle/addendum.md` | convert | (folded into the sibling prd.md conversion as a final section) | addendum |
| `docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10-resource-lifecycle/prd.md` | convert | `docs/superpowers/specs/2026-09-10-resource-lifecycle-prd-design.md` | planning document |
| `docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10/addendum.md` | convert | (folded into the sibling prd.md conversion as a final section) | addendum |
| `docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10/prd.md` | convert | `docs/superpowers/specs/2026-09-10-evidence-payload-bodies-prd-design.md` | planning document |
| `docs/bmad/planning-artifacts/sprint-change-proposal-2026-08-30.md` | convert | `docs/superpowers/specs/2026-08-30-sprint-change-proposal-design.md` | planning document |
| `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/DESIGN.md` | convert | `docs/superpowers/specs/2026-09-03-demorunner-ux-design.md` | planning document |
| `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/EXPERIENCE.md` | convert | `docs/superpowers/specs/2026-09-03-demorunner-ux-experience-design.md` | planning document |
| `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/wireframes/operations-stage-focus.md` | convert | `docs/superpowers/specs/2026-09-03-demorunner-operations-stage-focus-wireframe-design.md` | planning document |
| `docs/bmad/implementation-artifacts/deferred-work.md` | fold | `docs/backlog.md` | all items condensed into docs/backlog.md |
| `docs/bmad/implementation-artifacts/epic-1-retrospective.md` | fold | `docs/backlog.md` | open action items go to docs/backlog.md; the rest is process record |
| `docs/bmad/implementation-artifacts/epic-2-retrospective.md` | fold | `docs/backlog.md` | open action items go to docs/backlog.md; the rest is process record |
| `docs/bmad/implementation-artifacts/epic-3-retrospective.md` | fold | `docs/backlog.md` | open action items go to docs/backlog.md; the rest is process record |
| `docs/bmad/implementation-artifacts/sprint-status.yaml` | fold | `docs/backlog.md` | open stories and action items go to docs/backlog.md |
| `docs/bmad/implementation-artifacts/7-3-acceptance-evidence.md` | drop |  | process artifact |
| `docs/bmad/implementation-artifacts/7-3-continuation-handoff.md` | drop |  | process artifact |
| `docs/bmad/implementation-artifacts/bmad-build-auto-result-5-5-event-subscription-intake.md` | drop |  | process artifact |
| `docs/bmad/implementation-artifacts/bmad-build-auto-result-6-3-replicated-local-api-topology.md` | drop |  | process artifact |
| `docs/bmad/implementation-artifacts/epic-1-context.md` | drop |  | compiled build context |
| `docs/bmad/implementation-artifacts/epic-2-context.md` | drop |  | compiled build context |
| `docs/bmad/implementation-artifacts/epic-3-context.md` | drop |  | compiled build context |
| `docs/bmad/implementation-artifacts/epic-4-context.md` | drop |  | compiled build context |
| `docs/bmad/implementation-artifacts/epic-5-context.md` | drop |  | compiled build context |
| `docs/bmad/implementation-artifacts/epic-6-context.md` | drop |  | compiled build context |
| `docs/bmad/implementation-artifacts/epic-7-context.md` | drop |  | compiled build context |
| `docs/bmad/planning-artifacts/architecture/architecture-CoreBankDemo-2026-08-21/.memlog.md` | drop |  | memlog |
| `docs/bmad/planning-artifacts/architecture/architecture-CoreBankDemo-2026-08-21/review-adversarial.md` | drop |  | review artifact |
| `docs/bmad/planning-artifacts/architecture/architecture-CoreBankDemo-2026-08-21/review-version-verification.md` | drop |  | review artifact |
| `docs/bmad/planning-artifacts/briefs/brief-CoreBankDemo-2026-08-21/.memlog.md` | drop |  | memlog |
| `docs/bmad/planning-artifacts/implementation-readiness.md` | drop |  | process artifact |
| `docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-08-21/.memlog.md` | drop |  | memlog |
| `docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10-resource-lifecycle/.memlog.md` | drop |  | memlog |
| `docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10-resource-lifecycle/review-adversarial.md` | drop |  | review artifact |
| `docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10-resource-lifecycle/review-code-accuracy.md` | drop |  | review artifact |
| `docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10-resource-lifecycle/review-rubric.md` | drop |  | review artifact |
| `docs/bmad/planning-artifacts/prds/prd-CoreBankDemo-2026-09-10/.memlog.md` | drop |  | memlog |
| `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/.memlog.md` | drop |  | memlog |
| `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/.working/remediation-brief.md` | drop |  | working file |
| `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/.working/update-brief.md` | drop |  | working file |
| `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/imports/operator-console-inspiration.png` | drop |  | inspiration image |
| `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/review-accessibility.md` | drop |  | review artifact |
| `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/review-rubric.md` | drop |  | review artifact |
| `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/review-stage-safety.md` | drop |  | review artifact |
| `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/review-structure.md` | drop |  | review artifact |
| `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/validation-report.html` | drop |  | review artifact |
| `docs/bmad/planning-artifacts/ux-designs/ux-CoreBankDemo-2026-09-03/validation-report.md` | drop |  | review artifact |
