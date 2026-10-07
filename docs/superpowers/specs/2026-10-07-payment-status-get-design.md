# Payment status is readable through `GET /api/payments/{transactionId}`

> **Status:** Draft
> **Kind:** design spec
> **Original date:** 2026-10-07
> **Related:** [ADR-018](../../adr/ADR-018-instant-payment-rail.md); [ADR-020](../../adr/ADR-020-instant-rail-timeout-cancellation.md); [ADR-023](../../adr/ADR-023-corebank-sole-outcome-source.md); ADR-027 (written by this change); [constraints](../../constraints.md); [backlog](../../backlog.md)

## Intent

**Problem:** A caller who receives `202 Pending` from `POST /api/payments` has no way to read the
payment's outcome. The `202` carries `Location: /api/payments/{transactionId}`, but PaymentsAPI has
no `GET` endpoint, so that link answers `404`. Resending the original `POST` with the same
`Idempotency-Key` is the only HTTP route to the outcome today, and on the standard rail even that
reports a misleading status (see *Two meanings of "Completed"* below). On the instant rail the `202`
is the honest answer to an ambiguous timeout (ADR-020's residual `202`), so the caller must be able
to follow up.

**Approach:** Add `GET /api/payments/{transactionId}` to PaymentsAPI. It reads the payment's local
outbox row and reports the **business** outcome CoreBank committed, which PaymentsAPI already
records on that row (`OutboxMessage.ResponsePayload`, written by the delivery path and by
`TransactionEventHandler` through `IOutboxRepository.RecordCommittedOutcomeAsync`). It never calls
CoreBank. The endpoint makes the existing `Location` header resolve; it changes nothing else.

**Why local state and not an inline CoreBank call:**
- A `202` happens precisely when CoreBank is slow or unreachable. A `GET` that calls CoreBank would
  fail in the same conditions and hand the caller a second unknown.
- ADR-023 makes CoreBank the only source of outcomes. The local row holds only what CoreBank
  answered or published, so reading it is a projection of CoreBank's decision, not a second source.
- No new hop through the Dev Proxy fault path, so fault-injection demos stay about payments.

**Cost accepted:** the projection lags CoreBank by the event pipeline's latency (Dapr delivery, then
the PaymentsAPI inbox processor). In that window the `GET` reports `Pending` for a payment CoreBank
has already decided. If the event pipeline is down, the `GET` stays `Pending` until it recovers.
That answer is late, never wrong.

## Two meanings of "Completed"

The word `Completed` is used at two levels, and they must not be confused:

| Level | Where it lives | `Completed` means |
|---|---|---|
| **Technical (transport)** | `OutboxMessage.Status`, the messaging kernel's column (AD-11) | the outbox item has been **delivered**: CoreBank accepted the command into its inbox. Nothing is said about execution. |
| **Business (payment)** | the cached `TransactionSubmission.Status` in `OutboxMessage.ResponsePayload` | CoreBank **executed** the payment and committed it to the ledger. |

An outbox row reaches technical `Completed` as soon as CoreBank answers `202` for a standard
payment, which can be well before CoreBank executes it. The business outcome arrives later, through
the `transaction.completed`/`.failed`/`.cancelled` event.

**This endpoint reports the business status only.** It never reports `Completed` because the outbox
item is technically complete. The same rule already governs the instant rail's duplicate replay
(`PaymentsController.ToDuplicateResult`). The standard rail's duplicate `POST` still echoes the
technical status; that is recorded in `docs/backlog.md` and is out of scope here.

## Boundaries & Constraints

**Always:**
- Read only the PaymentsAPI database. No call to CoreBank, Dapr, or any other service.
- Answer `200` with the frozen `PaymentResponse` shape for any payment that exists, whatever its
  status. The outcome travels in the body's `Status`; the HTTP status says only that the lookup
  worked.
- Derive `Status` from the business outcome as specified in the matrix below, identically for both
  rails.
- Keep the controller thin (conventions skill): bind, call the handler, map to `200`/`404`.

**Ask First:**
- Any change to `PaymentResponse`, to the `POST /api/payments` responses, or to the `Location` header.
- Adding a database index or column (this repo uses `EnsureCreated()` only; existing databases never
  receive schema changes).

**Never:**
- Report `Completed` or `Failed` from the outbox row's technical `Status` alone.
- Answer `202`, `504`, or any status other than `200`/`404` from this endpoint.
- Write to any store from this endpoint.

## Contract

`GET /api/payments/{transactionId}`

| Case | Answer |
|---|---|
| A payment with this `TransactionId` exists | `200`, `PaymentResponse` (status per the matrix below) |
| No such payment | `404`, empty body |

`PaymentResponse` stays exactly as frozen: `PaymentId` (the idempotency key), `TransactionId`,
`Status`, `Amount`, `Currency`, `ProcessedAt`. No field is added.

**Identifier.** The route uses `TransactionId`, the value the existing `Location` header already
carries. PaymentsAPI sets `TransactionId = IdempotencyKey` when it stores a payment
(`PaymentStorageHandler`), so the two are the same value. The lookup uses the existing
`IOutboxRepository.FindByIdempotencyKeyAsync`, which is backed by the unique dedupe index;
`TransactionId` has no index. The handler carries a comment naming this equality, and a test pins
it so a future change to how `TransactionId` is generated fails loudly instead of silently breaking
the lookup.

## Status matrix

Evaluated top to bottom; the first matching row wins. "Cached status" is
`TransactionSubmission.Status` deserialized from `ResponsePayload`.

| # | Outbox row | Reported `Status` | Reported `ProcessedAt` |
|---|---|---|---|
| 1 | cached status is `Completed`, `Failed` or `Cancelled` | the cached status | the cached `ProcessedAt` |
| 2 | technical `Status` is `Cancelled` (no terminal cached status) | `Cancelled` | row `ProcessedAt`, else `CreatedAt` |
| 3 | technical `Status` is `Failed` (legacy rows from before ADR-023 only) | `Failed` | `CreatedAt` |
| 4 | anything else: `Pending`, `Processing`, technical `Completed` with no or a non-terminal cached status (for example CoreBank's `202 Pending`), or a payload that does not deserialize | `Pending` | `CreatedAt` |

Row 4 is where the technical/business distinction is enforced: a delivered-but-not-executed payment
reports `Pending`.

## Edge cases

| Input | Behaviour |
|---|---|
| `transactionId` longer than 100 characters | `404` (no row can exist; the handler answers not-found without querying, so no validation error and no reliance on how the provider sizes the parameter) |
| Case differs from the stored id | `404` (exact match, as the dedupe index matches today) |
| Id containing `/` (the `Location` header escapes it as `%2F`) | `200` for the stored payment: the `GET` follows its own `Location`. ASP.NET Core leaves `%2F` encoded in route values, so the action turns `%2F` back into `/` before the lookup. A key that literally contains the text `%2F` is the one id this cannot tell apart; accepted and recorded in ADR-027. |
| `GET /api/payments` with no id | unchanged: `405`, the route exists for `POST` only |
| Event redelivered after the outcome is cached | no effect on the answer (`RecordCommittedOutcomeAsync` never overwrites a terminal cached status) |
| Corrupt `ResponsePayload` | rows 2 to 4 apply as if no payload existed |

## Code Map

PaymentsAPI only.

- **`Handlers/PaymentStatusHandler.cs`** (new): `IPaymentStatusHandler.GetAsync(string transactionId,
  CancellationToken)` returns a `PaymentResponse?` (`null` = not found). Loads the row through
  `IOutboxRepository.FindByIdempotencyKeyAsync` and applies the status matrix. Registered next to the
  other PaymentsAPI handlers.
- **Shared outcome reader** (new, small static helper in `Handlers/` or `Outbox/`): the
  `ResponsePayload` deserialization that `PaymentsController.ResolveDeliveredResponse` performs
  today, moved so the `GET` handler and the duplicate-`POST` path read the cached outcome in one
  place. Pure move: the duplicate `POST`'s behaviour does not change, and its existing tests must pass
  unmodified.
- **`Controllers/PaymentsController.cs`**: `[HttpGet("{transactionId}")]` action calling
  `IPaymentStatusHandler`, mapping `null` to `NotFound()` and a response to `Ok(...)`; the
  constructor gains the handler.
- **`CoreBankDemo.http`**: a `GET` example after the `POST` example.
- Observability: no new spans, tags or metrics; ASP.NET Core's server instrumentation covers the
  request.

## Tests

Test-first, xUnit + AwesomeAssertions + Moq; ≥90% line coverage holds.

- **`tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStatusHandlerTests.cs`** (new): one test per matrix
  row, including technical `Completed` with a cached `Pending` → `Pending`, technical `Completed`
  with no payload → `Pending`, a cached `Cancelled` on a `Pending` row → `Cancelled`, a corrupt
  payload, and not found → `null`.
- **`PaymentsControllerTests`**: the `GET` action answers `200` with the handler's response and `404`
  for `null`; existing duplicate-`POST` tests pass unchanged after the helper move.
- **`PaymentStorageHandlerTests`** already pins `TransactionId == IdempotencyKey` for a stored payment,
  with a client-supplied key and with a generated one; no new test is needed there.
- **`tests/CoreBankDemo.Persistence.IntegrationTests`** (Postgres Testcontainer): store a payment,
  record a committed outcome through `RecordCommittedOutcomeAsync`, and read it back through
  `PaymentStatusHandler`; also a stored-but-undecided payment reads `Pending`.
- No k6, LoadTestSupport or DemoRunner change.

## Docs

- **ADR-027** (new), "Payment status is read from the local projection of CoreBank's outcome": the
  local-read decision and its lag, always `200` with the status in the body, the `TransactionId`
  route, and the technical-versus-business `Completed` distinction.
- **`docs/constraints.md`**, §2 PaymentsAPI: add the `GET` with its `200`/`404` contract.
- **`ARCHITECTURE.md`**: add the `GET` next to `POST /api/payments` in the PaymentsAPI box. The ADR
  table there stops at ADR-014 and its regeneration is Story 8.1, so ADR-027 is not added to it.
- **`docs/backlog.md`**: the standard-rail duplicate-`POST` entry (already added under this spec's
  heading).

## Acceptance

- `GET` on the `Location` of any `202` from `POST /api/payments` answers `200`.
- A standard payment that CoreBank has accepted but not executed reads `Pending`; after its
  `transaction.completed` event is processed it reads `Completed` with CoreBank's `ProcessedAt`.
- An instant payment answered `504 Cancelled` reads `Cancelled` with the same `ProcessedAt`.
- An unknown id answers `404`.
- `dotnet test CoreBankDemo.UnitTests.slnf` and `dotnet test CoreBankDemo.IntegrationTests.slnf` pass
  with coverage at or above the gate.

## Out of scope

- Changing the standard rail's duplicate-`POST` status (backlog).
- Polling the endpoint from k6 or DemoRunner.
- Calling CoreBank to refresh a stale `Pending`.
