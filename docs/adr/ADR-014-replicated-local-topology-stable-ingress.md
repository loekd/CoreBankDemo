# ADR-014: Replicated local topology behind stable Aspire ingress

**Date:** 2026-08-29
**Status:** Accepted — amended 2026-08-30
**Deciders:** Architecture team
**Supersedes:** Single-instance local topology assumptions
**Amendment:** The original per-replica Dapr-sidecar requirement was replaced with one pub/sub adapter per logical API service after the pinned Aspire integration proved replica-unaware.

## Context

A single local instance can demonstrate Inbox and Outbox flows but cannot prove that distributed partition locks preserve exclusivity and ordering across competing processes. Aspire may assign replica-specific endpoints dynamically, while demo clients and k6 require the documented stable PaymentsAPI ports. Adding a gateway solely for the demo would introduce another component and obscure Aspire's built-in proxy behavior.

## Decision

The regular and load-test AppHosts run two PaymentsAPI replicas and two CoreBankAPI replicas by default. Aspire's proxy provides one stable PaymentsAPI ingress at port 5294 for the regular demo and port 5295 for load tests. PaymentsAPI resolves the logical `corebank-api` service endpoint; no client binds to a replica address and no gateway is introduced.

Replicas of each service share their PostgreSQL database, Redis lock store, and logical Dapr app identity. Dapr provides one pub/sub adapter per logical API service. Here, an adapter means the single Dapr runtime/CLI process created by `WithDaprSidecar` for the logical Aspire project resource: it retains that service's logical app id and targets Aspire's stable service proxy rather than a replica address. Both CoreBankAPI replicas publish through the logical CoreBank adapter, and the logical Payments adapter delivers subscriptions through Aspire's stable PaymentsAPI proxy. The adapters are not infrastructure replicas and do not participate in the distributed-lock proof. Schema initialization must be safe when application replicas start concurrently.

The LoadTests AppHost owns disposable infrastructure. Both APIs start far enough to run their existing schema initialization, but their hosted Inbox/Outbox processors wait on a load-test-only processing-start gate before the first poll. After the APIs and LoadTestSupport are healthy, an explicit one-shot reset initializer resets the databases and releases each API's processing gate. k6 waits for that initializer and may verify the clean state, but it is not the ordering mechanism. Acceptance evidence identifies the processing replica and proves same-partition exclusion and durable ordering, including equal ordering timestamps, while different partitions progress concurrently.

## Implementation

- `CoreBankDemo.AppHost/AppHost.cs` defines the regular replicated graph and stable external PaymentsAPI endpoint.
- `CoreBankDemo.LoadTests/AppHost.cs` defines the disposable replicated load-test graph, configures API hosted processors to start gated, runs a one-shot reset-and-release initializer after API/LoadTestSupport health, makes k6 wait for that initializer, and preserves stable port 5295 ingress.
- `CoreBankDemo.CoreBankAPI/Program.cs` and `CoreBankDemo.PaymentsAPI/Program.cs` must tolerate concurrent empty-database startup.
- Story 6.3, `replicated-local-api-topology`, owns application resource replication, health dependencies, shared logical Dapr adapter wiring, and processor-instance evidence.
- The API hosts expose a load-test-only, non-public processing-start gate whose default state in the regular AppHost is open; focused tests prove no processor tick occurs before release.
- `CoreBankDemo.LoadTestSupport` exposes reset and assertion operations; the AppHost initializer owns reset-before-processor ordering, while `k6/script.js` owns load, drain, invariant assertions, and clean-state verification without addressing individual replicas.
- Tier-3 tests use the renewable Redis adapter established by ADR-011.

## Consequences

### Positive
- The normal local demo visibly exercises real cross-process lock competition.
- Clients keep stable documented URLs despite replica churn.
- Different partitions can demonstrate parallel progress without weakening per-partition ordering.

### Negative / Trade-offs
- Local startup consumes more CPU, memory, and runtime resources for the additional application processes.
- Database initialization and test reset require explicit coordination.
- Per-process circuit-breaker state remains independent across PaymentsAPI replicas.
- Dapr pub/sub availability is represented by one adapter per logical service, so this topology demonstrates application concurrency and resilience rather than infrastructure high availability.

## Key takeaway

> Run competing application replicas behind stable Aspire proxies, with one Dapr pub/sub adapter per logical API service.
