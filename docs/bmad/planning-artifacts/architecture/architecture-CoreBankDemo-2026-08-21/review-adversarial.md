# Adversarial Review — ARCHITECTURE-SPINE.md (CoreBankDemo Rebuild)

**Lens:** adversarial two-team construction. For each finding, two units one level down each obey every AD (AD-1..AD-10), every Consistency Convention, and the binding skills (`conventions`, `messaging-patterns`, `observability`) to the letter — and still build something that fails at integration. Findings that the current text already forbids were discarded (checked against the spine, `constraints.md`, and all three skill files).

**Verdict:** The spine is strong on *mechanism* ownership (AD-3 single kernel, AD-6 fixed ports, AD-7 lock semantics) but leaks on *shared-data identity and shape*. Ten permitted incompatibilities found; four (F1–F4) break §1 load-test invariants silently and only surface at the k6 tier, which is the most expensive place to find them. Recommend closing F1–F5 by AD amendment before parallel epic work starts; F6–F10 are one-line Rule tightenings.

---

## Critical findings

### F1 — AD-4's single identity silently drops two of every three events on the return path

**Units:** CoreBankAPI MessagingOutbox team vs PaymentsAPI Inbox team (and CoreBankAPI vs itself).

**Scenario.** AD-4: "The idempotency key … is the dedupe identity at every hop … Idempotent stores use `StoreIfNewAsync` (unique index + violation catch)." The Persistence convention repeats: "unique index enforces idempotency." Per constraints §2, CoreBankAPI publishes **three** events per transaction — `TransactionCompleted`/`Failed` + 2× `BalanceUpdated` — all sharing one `TransactionId` (= the idempotency key). The MessagingOutbox team, obeying AD-4 literally, puts the unique index on the idempotency key and enqueues via `StoreIfNewAsync`: the second and third enqueues are "duplicates" and are silently swallowed. Identically on the far side: PaymentsAPI Inbox, deduping by idempotency key per AD-4, stores the first arriving event and drops the other two. Every AD obeyed; `BalanceUpdated` events are lost; §1.2 zero-message-loss and the balance-updated inbox assertions fail. No unit or SQLite test catches it because each team's store is self-consistently "idempotent."

**Why permitted:** AD-4 conflates three roles — partition/ordering identity, payment-path dedupe identity, and event-path row identity — into one key. The text does not grant events an identity of their own anywhere (CloudEvent *types* are constants; event *identity* is unspecified).

**Fix (amend AD-4):**
> The idempotency key is the **ordering/partition identity** at every hop (`PartitionId = FNV-1a(key) % 4`) and the **dedupe identity on the payment path** (payments outbox, CoreBank inbox), where it equals `TransactionId`. On the **event path** (CoreBank messaging outbox, payments inbox) each event additionally carries an `EventId` GUID generated once at enqueue and propagated in the CloudEvent; `EventId` is the unique-index/dedupe identity for those two stores. `StoreIfNewAsync` everywhere, keyed accordingly.

### F2 — The in-flight-duplicate window and HTTP result classification are two different contracts

**Units:** Messaging kernel team (owns the HTTP-forward `IOutboxDeliveryStrategy` per AD-3) vs CoreBankAPI Controllers team.

**Scenario.** Constraints §2: "duplicates replay cached `ResponsePayload`." AD-5: the response is cached only when ledger mutation + inbox completion commit. So there is a window — original request still `Pending`/`Processing` — where a duplicate has **no cached response to replay**. This window is not theoretical: the only source of duplicates on this hop is the outbox strategy itself re-sending after a timeout (exactly what DevProxy chaos induces), i.e. it hits the window preferentially. The CoreBankAPI team, obeying every rule, may answer that case with `409`, `202` + empty body, or `200` + a partial shape. The kernel team, obeying every rule, wrote a strategy that deserializes the body as the completed-transaction payload, or treats non-2xx as retryable-forever. Depending on the pairing: outbox rows retry to `Failed` (breaks §1.4), or complete against an empty payload, or spin. Neither team violated any AD.

**Why permitted:** No AD defines (a) the response for a not-yet-completed duplicate, or (b) how the delivery strategy classifies status codes into completed / retry / poison.

**Fix (new AD-11 — HTTP delivery contract):**
> `POST /api/transactions/process` returns `202` for all three accept cases: new row, completed duplicate (body = cached `ResponsePayload`), in-flight duplicate (body = accepted-status payload, no result fields). The HTTP delivery strategy classifies: any `2xx` → outbox row `Completed`; `5xx`/network error/timeout → retry (`RetryCount`++); `4xx` → `Failed` immediately (poison — intake validation makes this a contract bug and the drain assertion is *meant* to surface it).

### F3 — `Status.Failed`: business outcome vs transport poison

**Units:** CoreBankAPI Inbox team vs LoadTestSupport team.

**Scenario.** A load-test transaction is rejected for insufficient funds — a *normal* flow (the contract publishes `TransactionFailed` events). The CoreBankAPI team, seeing `MessageConstants.Status.Failed` with no semantics attached anywhere, marks the inbox row `Failed`: the transaction failed, after all. Consequences, all AD-compliant: the kernel's retry machinery re-attempts the row up to `MaxRetryCount` (five `TransactionFailed` events published, breaking exactly-once observably), and LoadTestSupport's §1.4 assertion "zero `Failed` after drain" fails on a run that behaved correctly. The two teams hold contradictory but individually defensible readings of the same status string.

**Why permitted:** The Conventions table lists the four statuses with no semantics; §1.4 constrains a word ("Failed") whose meaning no AD pins.

**Fix (add Rule to AD-5 or the MessageConstants convention row):**
> Statuses describe **transport processing only**. A business rejection is a *successfully processed* message: inbox row → `Completed` atomically with the `TransactionFailed` event enqueue and a `ResponsePayload` recording the failed business outcome. `Status.Failed` is reserved for retry exhaustion (`RetryCount ≥ MaxRetryCount`) and is what §1.4 asserts to be zero.

### F4 — Cross-service wire shapes have no owner, and AD-10 deletes the only reference for them

**Units:** CoreBankAPI team vs PaymentsAPI team (also LoadTestSupport/k6 as third consumer).

**Scenario.** The dependency rule forbids API-to-API references ("they interact only via HTTP/pub-sub"), so the transaction request/response DTOs and the three CloudEvent payload shapes must exist as **two independent copies**. AD-1 freezes "response semantics … in `constraints.md` §2" — but §2 names endpoints and behaviors, not one field or casing of any payload. The only concrete schema reference is `main`'s code, and AD-10 mandates "old sources deleted" at the start of each project's epic — so when the PaymentsAPI epic runs, the CoreBankAPI epoch's shapes exist only in the new code the PaymentsAPI team is forbidden to reference and has no contract doc for. `status` vs `transactionStatus`, enum-as-int vs string, `amount` decimal vs `{ value, currency }`: each copy is internally consistent, unit tests pass at 90%, and the mismatch appears only when the k6 tier runs the full loop.

**Why permitted:** No AD places wire DTOs anywhere. `ServiceDefaults` holds CloudEvent *type strings* only; the Deferred section defers "repository interface shapes" but is silent on wire shapes, so they default to per-team invention.

**Fix (extend AD-1 / Structural Seed):**
> Cross-service wire shapes — the transaction process request/response DTOs and the three CloudEvent payload records — live once in `ServiceDefaults` beside `CloudEventTypes` (both APIs and LoadTestSupport already may reference it). No project defines a private copy of a cross-service shape.

## Significant findings

### F5 — AD-9's tier 2 and AD-6's raw-SQL rule are jointly unsatisfiable for repositories, and teams will resolve it in incompatible directions

**Units:** Messaging kernel team vs test-gate/API test owners.

**Scenario.** AD-6: raw SQL (`SELECT … FOR UPDATE`) exists *only inside repository implementations*. AD-9 tier 2: "repository/store behavior tested on EF Core SQLite in-memory." Constraints §4: repositories are inside the 90% line gate. But a repository method containing `FOR UPDATE` cannot execute on SQLite at all, and `StoreIfNewAsync`'s "violation catch" — kernel dedupe *logic* — hinges on `PostgresException` SqlState 23505, which AD-9 assigns "solely" to the k6 tier while the coverage gate still counts its lines. One team resolves this with provider-branched SQL (drift between test and prod paths); another slaps `[ExcludeFromCodeCoverage]` on whole repositories (sanctioned only for "hosting wiring"); another catches bare `DbUpdateException` and miscounts other constraint violations as duplicates. All three claim AD-9 compliance; their test suites assert different things about the same store.

**Fix (tighten AD-9):**
> Tier 2 covers everything EF translates on SQLite. Raw-SQL members are isolated in dedicated adapter methods — no branching, one SQL string, no logic — carrying a dedicated exclusion attribute and individually listed in the test-strategy ADR. Unique-violation detection goes through a provider-agnostic `IsUniqueViolation(DbUpdateException)` helper (Postgres mapping is one line, excluded; the catch path itself is SQLite-exercisable and gated).

### F6 — Stale-claim recovery is owned by nobody and forbidden to everybody but the kernel

**Units:** Messaging kernel team vs CoreBankAPI team (chaos scenarios).

**Scenario.** AppHost chaos kills a worker mid-batch; rows sit in `Processing`. §1.4 demands zero `Processing` after drain, so *someone* must revert stale rows. AD-3's exhaustive kernel-owned list — "polling, partition fan-out, locking, batching, claiming, retry, trace restoration" — does not include stale-claim recovery, so a kernel team building exactly that list ships without it; the CoreBankAPI team that notices cannot add a sweep because AD-3 forbids processors re-implementing claiming. Both teams are rule-compliant; the drain assertion fails under kill-chaos and the fix requires renegotiating the kernel contract mid-rebuild.

**Fix (extend AD-3's list):**
> …retry, **stale-claim recovery** (rows `Processing` longer than `ProcessingTimeout` revert to `Pending`, performed under the partition lock), or trace restoration.

### F7 — Seed data has two owners: startup `EnsureCreated` seeding vs LoadTestSupport `reset_database`

**Units:** CoreBankAPI team vs LoadTestSupport team.

**Scenario.** AD-1 freezes "seeded accounts"; the Persistence convention allows only `EnsureCreated()`, which seeds once at first creation. `reset_database` (constraints §2) must restore the 10 × €10,000,000 baseline mid-run — `EnsureCreated` will not re-run, so LoadTestSupport necessarily writes its own reseed with its own copy of account numbers/balances. Two independently maintained seed sets, both "frozen" per AD-1, no rule tying them together; one digit of drift and §1.3 balance conservation asserts against the wrong baseline. The Deferred note ("E6 conforms to whatever E1–E4 produced") sequences the *schema*, not the seed constants.

**Fix (Rule under Persistence convention):**
> Seed data (account numbers and opening balances) is defined once, as constants next to the CoreBankAPI DbContext; both startup seeding and LoadTestSupport `reset_database` invoke the same seeder.

## Minor findings

### F8 — Idempotency key type is unpinned; a string header meets a `Guid TransactionId`

**Units:** PaymentsAPI intake vs CoreBankAPI model.
AD-4 says the client `Idempotency-Key` header **or** a generated GUID; nothing constrains client-supplied values to GUID shape, while "equals `TransactionId`" invites CoreBankAPI to type it `Guid`. A key like `order-123` is accepted with `202` at intake, then 400s forever on forward → `Failed` → §1.2/§1.4 violated. k6 sends GUIDs, so this survives every gate and dies in the live demo when someone curls a friendly key.
**Fix (AD-4 addition):** the key is a GUID; a non-GUID header is rejected `400` (all-errors shape) at intake. (Verify `main` accepted arbitrary strings first — if so, this is an AD-1 behavior change needing a one-line ADR.)

### F9 — `PartitionCount = 4 everywhere` fixes the value but not its home

**Units:** Messaging kernel vs either API's options class.
A team that binds `PartitionCount` as a DataAnnotations-validated option *defaulting* to 4 obeys AD-4's letter and AD-7's "every option is read by code" — and has recreated the exact A3 failure mode (appsettings overrides it to 2 in one service, silently, with no gate). Ruling A3 re-asserts the value without removing the drift channel.
**Fix (AD-4 addition):** `PartitionCount` is a compile-time constant (`MessageConstants.Defaults.PartitionCount = 4`); no configuration key for partition count exists in any service.

### F10 — Free-form `LockNamePrefix` permits cross-store lock collisions

**Units:** CoreBankAPI Inbox vs CoreBankAPI MessagingOutbox (any two processors).
The `messaging-patterns` skill mandates overriding `LockNamePrefix` but nothing requires uniqueness. Two processors choosing `"corebank"` share per-partition lock resources: the inbox worker holding partition 3's lock starves the outbox's partition 3 for the lock lifetime. Rule-compliant on both sides; symptom is drain-timeout flakiness under load, misattributed to throughput.
**Fix (Rule under AD-7):** lock resource names are `{service}:{store}:{partitionId}`; the kernel composes them from a required store-name constructor argument rather than a free-form prefix. (Update the skill's `LockNamePrefix` wording to match.)

---

## Discarded candidates (for completeness of the adversarial pass)

- **Trace-context carrier on the Dapr hop** — considered a format clash (Dapr envelope auto-injection vs explicit payload fields), but AD-8 already mandates persistence on message rows and the `observability` skill mandates persist-and-restore from rows; since the messaging-outbox row carries the context and F4's fix pins the event payload shape (which should include the traceparent fields), the residual ambiguity collapses into F4. Noted here so F4's fix explicitly includes `TraceParent`/`TraceState` fields in the event payload records.
- **Retry backoff shape** — unspecified, but any two choices interoperate; no incompatibility, only tuning.
- **PaymentsAPI inbox side-effects** (what consuming `TransactionCompleted` *does*) — underspecified but single-owner; no second unit can clash with it.

## Proposed amendment summary

| # | Action |
| --- | --- |
| F1 | Amend AD-4: split ordering identity (idempotency key, everywhere) from dedupe identity (key on payment path; per-event `EventId` on event path) |
| F2 | New AD-11: HTTP delivery contract — 202 for all accept cases incl. in-flight duplicates; strategy maps 2xx→Completed, 5xx/timeout→retry, 4xx→poison |
| F3 | New Rule: statuses are transport-only; business rejection = `Completed` + failure payload + `TransactionFailed` event; `Failed` = retries exhausted |
| F4 | Extend AD-1/seed: cross-service DTOs + event payload records (incl. trace fields) live once in ServiceDefaults |
| F5 | Tighten AD-9: logic-free raw-SQL adapter methods, enumerated exclusions, provider-agnostic unique-violation helper |
| F6 | Extend AD-3 list with stale-claim recovery under the partition lock |
| F7 | Single seeder shared by startup and `reset_database` |
| F8 | Pin key type to GUID, reject others at intake (verify vs `main`) |
| F9 | `PartitionCount` is a constant, never a config key |
| F10 | Kernel-composed unique lock names `{service}:{store}:{partition}` |
