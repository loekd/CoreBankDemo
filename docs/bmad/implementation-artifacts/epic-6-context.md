# Epic 6 Context: E5 — AppHost & Orchestration

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Rebuild and replicate the Aspire orchestration graph so the full solution builds and runs as a
realistic distributed demo rather than a single-instance stand-in. This epic moves partition
locking off the Dapr lock component onto renewable Redis leases, runs two replicas of each API by
default (proving partition exclusivity and ordering hold across competing processes, not just
within one process), and gives the LoadTests AppHost a race-safe reset/gating sequence. It is also
the point at which `CoreBankDemo.Rebuild.slnf` becomes equal to the full solution and
`dotnet build CoreBankDemo.sln` must go green. It also removes SQLite as a test-only second
database engine and introduces an independently runnable PostgreSQL Testcontainers persistence
tier alongside the fast Docker-free unit-test loop.

## Stories

- Story 6.1: Aspire application graph
- Story 6.2: Renewable Redis distributed locking
- Story 6.3: Replicated local API topology
- Story 6.4: Chaos opt-in and demo smoke
- Story 6.5: OpenTelemetry business metrics
- Story 6.6: Remove SQLite with PostgreSQL Testcontainers

## Requirements & Constraints

- One command (`aspire run`) must boot a healthy system: Postgres (`paymentsdb`, `corebankdb`,
  pgAdmin), Redis (+ RedisInsight), Jaeger, Dapr components (pubsub, subscription — no lock
  component), and both APIs with sidecars.
- Partition count is fixed at 4 everywhere; no dead feature flags survive.
- The documented external contract is frozen: PaymentsAPI ingress ports 5294 (regular AppHost) and
  5295 (load-test AppHost), plus 5032/5181, Dapr pubsub `pubsub` / topic `transaction-events`,
  CloudEvent types, seeded accounts, and response semantics. Any behavior change requires a new
  ADR.
- Competing API instances must share distributed partition locks so no partition is processed
  concurrently or out of order by two replicas, while different partitions remain free to progress
  in parallel — verified under concurrent load with fault injection (invariants hold end to end).
- One payment must still resolve to exactly one trace across the HTTP hop, message stores, and the
  Dapr hop (Jaeger-verifiable), including when DevProxy fault injection triggers Polly retries.
- Business metrics must use the existing OpenTelemetry/OTLP pipeline and bounded attributes only.
  IDs, account numbers, trace context, exception text, and other user-controlled values must never
  be metric dimensions.
- The LoadTests AppHost provides a configurable k6 run (transaction count / VUs, ~10% deliberate
  duplicate keys) against the same default two-by-two topology, using disposable infrastructure.
- Existing `.http` demo flows (`demo-requests.http`, `payment-idempotency-tests.http`) must keep
  behaving exactly as on `main` (202 Accepted, duplicate replay, current outbox/inbox visibility).
- Unit tests must remain runnable without Docker. Persistence integration tests may require the
  devcontainer's Docker runtime and cold-start latency, but must be independently targetable and
  must use PostgreSQL rather than a behaviorally different relational substitute.

## Technical Decisions

- **Locking (ADR-011, supersedes ADR-004's Dapr lock adapter and fixed-expiry behavior; ADR-004's
  partitioning-by-hash decision itself remains accepted):** `IDistributedLockService` keeps its
  existing signature and non-throwing `bool` contract. Its adapter now uses `DistributedLock.Redis`
  over the `IConnectionMultiplexer` Aspire injects for the shared `redis` resource. Acquisition is
  non-blocking (a busy partition is skipped immediately); while the handle is healthy the lease
  renews automatically; caller cancellation or `HandleLostToken` cancels cooperative work promptly.
  There is no `LockRenewIntervalSeconds` option — renewal cadence is adapter/library-owned, not
  application configuration. Dapr remains the pub/sub adapter only; the Dapr `lockstore` component
  and its sidecar wiring are removed. Frozen, already-completed Story 3.2 stays historical and is
  not rewritten to match.
- **Replicated topology (ADR-014):** both the regular and LoadTests AppHosts run two PaymentsAPI
  replicas and two CoreBankAPI replicas by default. Aspire's proxy is the only ingress — clients
  never bind to a replica address and no gateway is introduced. Replicas of one service share its
  Postgres database, Redis lock store, Dapr pubsub, and logical Dapr app id; only sidecar/runtime
  ports and process identity are replica-unique. PaymentsAPI resolves CoreBankAPI through Aspire's
  logical `corebank-api` endpoint, never a replica address. Schema initialization must tolerate
  concurrent empty-database startup by multiple replicas of the same service.
- **Load-test reset/gating (ADR-014):** in the LoadTests AppHost, both APIs run their normal schema
  initialization, but their hosted Inbox/Outbox processors wait behind a load-test-only,
  non-public processing-start gate. After API and LoadTestSupport health checks pass, a one-shot
  reset initializer resets the databases and releases every processor gate before k6 starts. k6
  waits on that initializer and may verify clean state, but owns load/drain/assertions only — it is
  not responsible for startup ordering. In the regular AppHost the gate's default state is open.
- **Scale-out lock invariant (AD-3):** the lock store is shared by every replica; at most one
  instance may own a given store partition at a time, preserving durable enqueue order (including
  ties in ordering timestamps) while different partitions progress concurrently. Lock names follow
  `<prefix>-partition-<id>` with per-store prefixes (`payments-outbox`, `payments-inbox`,
  `corebank-inbox`, `messaging-outbox`) — never shared between stores.
- **Rebuild gate (AD-10):** story/epic gates run against `CoreBankDemo.Rebuild.slnf` throughout the
  rebuild; the full `.sln` is only required to build green once this AppHost epic completes.
- **Test tiering (AD-9):** Postgres-, Redis-, and replicated-topology semantics (same-partition
  exclusion, durable ordering, concurrent progress on different partitions, lock-loss cancellation
  and renewal) belong to the k6/Aspire acceptance tier using real Postgres and the real renewable
  Redis lock adapter — not mocked in unit tests.
- **Pending persistence-test amendment (Story 6.6 / ADR-016):** ADR-016 must supersede the
  SQLite-specific portions of ADR-012 and AD-9 before implementation. The intended tiers are fast,
  Docker-free unit tests; PostgreSQL Testcontainers persistence integration tests; and full
  Aspire/k6 distributed acceptance. The amendment must define independently runnable targets,
  preserve the combined >=90% line-coverage gate without blanket exclusions, pin the PostgreSQL
  image to the AppHost major version, and remove SQLite-specific packages and production helpers.
- **Business metrics (ADR-003):** one shared meter contract distinguishes business outcomes,
  transport attempts, and durable message-store transitions. Measurements happen only after the
  represented outcome is known; retries count as transport/processing attempts, while business
  rejection remains a completed Inbox outcome rather than an infrastructure failure. Instrument
  behavior and bounded tags are proven with `MeterListener`; dashboards and alerts are not part of
  this epic.
- **Relevant stack pins:** Aspire 13.4.0; `Aspire.StackExchange.Redis` and `DistributedLock.Redis`
  both at 13.4.0 / 1.1.1; Dapr.AspNetCore / Dapr.Client 1.17.9 (pub/sub only going forward).
- Story 2.6 already owns lock-expiry takeover/failure-path proof; Story 6.3's replicated-topology
  tests do not duplicate that.

## Cross-Story Dependencies

- Story 6.2 (renewable Redis locking) must land before Story 6.3 (replicated topology) can prove
  cross-replica exclusivity using the real lock adapter — 6.3's acceptance tier depends on 6.2's
  Redis lock adapter, not the old Dapr one.
- Story 6.1 establishes the baseline Aspire graph (single-instance-equivalent, ports, Dapr
  components, healthy build) that Stories 6.2 and 6.3 then modify (removing the Dapr lockstore,
  adding replication).
- Story 6.4's demo/chaos smoke test exercises the graph produced by 6.1–6.3. Story 6.5 instruments
  the already-proven business flows and depends on Payments stories 5.4–5.6 so both transport
  directions and all four durable stores exist before their metric hooks are added.
- Story 6.6 may proceed independently of the orchestration stories once ADR-016 is accepted, but
  it must reconcile concurrent test changes from Stories 5.4 and 6.3 and leave both the unit-only
  and full rebuild gates green. Story 7.1 consumes its PostgreSQL integration-test infrastructure.
- Story 7.4 may later orchestrate only these already-proven flows; it must not paper over or replace
  Story 6.4's validation or treat Story 6.5's counters as a replacement for durable assertions.
- Outbox/inbox visibility via LoadTestSupport endpoints (mentioned in Story 6.4) is not available
  until Epic 6 (E6) lands per the epics file wording — until then, verification falls back to
  direct DB inspection.
