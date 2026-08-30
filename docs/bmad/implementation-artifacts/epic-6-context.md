# Epic 6 Context: E5 — AppHost & Orchestration

<!-- Generated from planning artifacts. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Rebuild the Aspire orchestration graph as a reliable one-command, production-shaped local demo: two replicas of each API behind stable ingress, shared PostgreSQL and renewable Redis partition locks, Dapr retained for pub/sub, observable fault injection, and race-safe load-test startup. This epic also restores the full solution build, completes the move to PostgreSQL-backed persistence tests, and removes the obsolete Dapr service-invocation path so the demo proves its ordering, idempotency, tracing, and resilience guarantees across competing processes.

## Stories

- Story 6.1: Aspire application graph
- Story 6.2: Renewable Redis distributed locking
- Story 6.3: Replicated local API topology
- Story 6.4: Chaos opt-in and demo smoke
- Story 6.5: OpenTelemetry business metrics
- Story 6.6: Remove SQLite with PostgreSQL Testcontainers
- Story 6.7: Eliminate Dapr service invocation

## Requirements & Constraints

- `aspire run` must boot healthy PostgreSQL databases for both APIs, pgAdmin, Redis and RedisInsight, Jaeger, Dapr pub/sub and subscription components, one healthy Dapr adapter per logical API service, and both APIs. There is no Dapr lock component.
- Both regular and load-test AppHosts run two PaymentsAPI and two CoreBankAPI replicas by default. Clients use stable Aspire-proxied PaymentsAPI ports 5294 and 5295 respectively; PaymentsAPI reaches CoreBankAPI through its logical Aspire endpoint, never a replica address.
- Replicas share their service database, logical Dapr app identity, pub/sub, and Redis lock store. Dapr runs one pub/sub adapter per logical API service: both CoreBank replicas publish through the logical CoreBank adapter, and the logical Payments adapter delivers through the stable Payments proxy. Concurrent empty-database startup must be safe.
- Partition count is fixed at four. A shared distributed lock must prevent concurrent processing or reordering within a store partition while allowing different partitions to progress concurrently.
- The external HTTP shapes, Dapr pubsub name `pubsub`, topic `transaction-events`, CloudEvent types, seeded accounts, and existing `.http` demo behavior remain unchanged.
- DevProxy chaos is opt-in. Retry, circuit-breaker, and timeout behavior must remain visible in telemetry, and one payment must retain one distributed trace across HTTP, stores, and pub/sub.
- Business and messaging metrics use the existing OpenTelemetry export pipeline and bounded attributes only. Transaction IDs, idempotency keys, account numbers, trace IDs, exception text, and other user-controlled values are forbidden as metric dimensions.
- Pure logic tests remain Docker-free. Persistence behavior is tested against pinned PostgreSQL Testcontainers, never SQLite, EF Core InMemory, or another substitute. The full rebuild gate runs both .NET test tiers with at least 90% line coverage.
- Dapr remains only for CloudEvent publication and subscription. PaymentsAPI-to-CoreBankAPI request/response traffic must use the generated Kiota client behind the application-owned port.
- At epic completion, `CoreBankDemo.Rebuild.slnf` represents the full buildable solution and `dotnet build CoreBankDemo.sln` is green.

## Technical Decisions

- Distributed locking uses `DistributedLock.Redis` over the Aspire-managed Redis connection. Acquisition is non-blocking, leases renew automatically, and caller cancellation or lock-loss cancellation stops cooperative work. The existing lock-service interface and non-throwing failure contract remain stable; renewal cadence is not configurable.
- Lock names are store-specific (`<prefix>-partition-<id>`), so unrelated Inbox and Outbox stores never contend for the same partition lock. Replicated acceptance tests must prove single ownership and durable ordering for a partition, including equal timestamps, while showing concurrent work across different partitions and replicas.
- Aspire proxying supplies stable ingress without introducing a gateway. Service replicas share logical dependencies and one Dapr pub/sub adapter per logical service; adapter count is outside the application-lock proof and is not an infrastructure high-availability claim.
- In the load-test graph, APIs complete schema initialization while hosted processors wait behind a load-test-only start gate. After API and LoadTestSupport health, a one-shot initializer resets databases and releases all processor gates before k6 starts. The regular AppHost leaves processing enabled.
- Persistence verification has three tiers: Docker-free unit tests, PostgreSQL Testcontainers integration tests using pinned `postgres:18.3`, and distributed Aspire/k6 acceptance. Integration tests cover real Npgsql uniqueness, row locking, transactions, ordering, concurrency, and data-type behavior.
- The sole banking request/response integration is the checked-in OpenAPI contract, generated Kiota transport client, application-owned adapter, Aspire service discovery, and standard resilience pipeline. Live configuration, tests, scripts, and guidance must reject obsolete Dapr invocation flags, routes, APIs, or fallback clients.
- OpenTelemetry measurements are recorded only when the represented outcome is known. Business rejection is a completed business outcome, not an Inbox or transport failure; retries remain visible as attempts. Metric contracts and bounded tags are verified with `MeterListener`.
- The AppHost epic is where the rebuild filter and full solution converge. Provider-specific persistence and replicated orchestration proofs belong in integration or acceptance tiers rather than mocked unit tests.

## Cross-Story Dependencies

- Story 6.1 establishes the healthy Aspire graph and full-solution baseline; Story 6.2 replaces its locking dependency with shared renewable Redis; Story 6.3 uses that adapter to prove replicated exclusivity and ordering.
- Story 6.4 validates the completed graph, stable ingress, DevProxy behavior, and trace continuity. Story 6.5 instruments the intake, processor, store, and transport paths established by the earlier service epics.
- Story 6.6 can proceed independently once the PostgreSQL test decision is accepted, but must preserve both the fast unit loop and the full rebuild gate; its integration infrastructure is consumed by later load-harness work.
- Story 6.7 depends on the existing Kiota adapter and must remove only request/response invocation remnants while preserving Dapr pub/sub.
- Live load acceptance and LoadTestSupport visibility complete in the following epic; this epic must provide the replicated, gated, PostgreSQL-backed topology that work depends on.
