# ADR-010: Four partitions are a system-wide runtime invariant

**Date:** 2026-08-29
**Status:** Accepted
**Deciders:** Architecture team
**Supersedes:** Configurations that allowed runtime partition counts other than four

## Context

The brownfield configuration disagreed with ADR-004 and the demo narrative: at least one processor used two partitions while documentation assumed four. Different partition counts change key-to-partition assignment and can cause replicas or services to reason about ordering and lock ownership differently. A default of four is insufficient if configuration can silently override it.

## Decision

Every production Inbox, Outbox, and messaging-outbox processor runs with exactly four partitions. Production application registration requires an explicit, parseable `PartitionCount: 4` in every active processor section; startup fails when it is missing or has any other value.

Option records may retain a default of `4`, and `PartitionHelper` plus kernel option records may accept other positive counts for focused unit tests and reusable-library verification. That flexibility does not extend to production application configuration. All replicas of a service consume the same explicitly validated value.

Lock names remain store-specific and partition-scoped: `payments-outbox`, `payments-inbox`, `corebank-inbox`, and `messaging-outbox`, followed by `-partition-{id}`. Stores never share a lock prefix.

## Implementation

- `CoreBankDemo.ServiceDefaults/Configuration/ProcessingOptionsBase.cs` supplies the common option shape and default.
- Each API's registration layer adds an exact-value startup validator; `CoreBankDemo.PaymentsAPI/PaymentStorageServiceCollectionExtensions.cs` is the current reference implementation.
- `CoreBankDemo.CoreBankAPI/appsettings.json` and `CoreBankDemo.PaymentsAPI/appsettings.json` must contain `PartitionCount: 4` for every active processor section.
- `CoreBankDemo.Messaging/PartitionHelper.cs` remains parameterized so hash behavior can be tested independently.
- Option-binding tests may prove that the record default is `4`; production registration tests prove that omission and explicit non-four values fail before hosted processors start.

## Consequences

### Positive
- Every replica maps a given idempotency key to the same partition.
- Demo behavior, tests, configuration, and documentation share one scale model.
- Configuration drift fails at startup instead of surfacing as an ordering defect.

### Negative / Trade-offs
- Changing the partition count becomes an architectural/data-coordination change requiring a new ADR.
- The demo cannot tune partition concurrency without rebuilding and revalidating the topology.

## Key takeaway

> Four is not merely a default; it is the validated production partition count everywhere.
