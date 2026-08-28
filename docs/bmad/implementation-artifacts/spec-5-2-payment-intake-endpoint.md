---
title: 'Story 5.2: Payment intake endpoint'
type: 'feature'
created: '2026-08-28'
status: 'done'
review_loop_iteration: 0
baseline_commit: '86e38efbe20fab79e41ebcb220be5866492305b1'
context:
  - '{project-root}/docs/bmad/implementation-artifacts/epic-5-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Story 5.1 durably stores payments, but PaymentsAPI has no public intake route, so clients cannot submit a payment or receive the frozen acknowledgement contract.

**Approach:** Add a thin `POST /api/payments` controller over `IPaymentStorageHandler`, restore the frozen `PaymentResponse`, and configure MVC so all request-model errors are returned together in the established `{ Errors }` envelope.

## Boundaries & Constraints

**Always:** Preserve the frozen route, `Idempotency-Key` header semantics, `PaymentResponse(string PaymentId, string TransactionId, string Status, decimal Amount, string Currency, DateTimeOffset ProcessedAt)`, and `202 Accepted` location `/api/payments/{TransactionId}`. Both stored and duplicate outcomes map the handler's persisted snapshot to the response: `PaymentId` is the idempotency key, `TransactionId` is the stored transaction identity, status/amount/currency come from the row, and `ProcessedAt` is its UTC creation time. Invalid model state returns every model error in one `BadRequest(new { Errors })`; handler validation failures use the same envelope. Pass the request, first header value, and cancellation token unchanged to the handler.

**Ask First:** Changing any frozen request/response field, route, header name, status code, location, identity mapping, timestamp source, or validation envelope.

**Never:** Put persistence, idempotency, partitioning, rounding, tracing, or clock logic in the controller; expose the internal row GUID as `PaymentId`; add a payment status GET route, Swagger/OpenAPI setup, forwarding, Dapr endpoints, or event handling; change Story 5.1 storage behavior.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|---------------|---------------------------|----------------|
| New payment | Valid body; handler returns `Stored` | `202`, winner-derived frozen response, and winner transaction location | Infrastructure errors propagate |
| Duplicate | Valid body/key; handler returns `Duplicate` | `202` references and returns the persisted winner, not retry payload values | Infrastructure errors propagate |
| Invalid body | ModelState contains multiple errors | `400` with all errors; handler is not called | `{ Errors: [...] }` |
| Invalid key | Handler returns `ValidationFailed` | `400` with all handler errors | `{ Errors: [...] }` |
| Unexpected result | Missing snapshot or unknown outcome | No success-shaped response | Throw explicit invalid-state exception |

</frozen-after-approval>

## Code Map

- `CoreBankDemo.PaymentsAPI/Handlers/PaymentStorageHandler.cs:11-124` -- reuse `IPaymentStorageHandler`, outcomes, immutable persisted snapshot, and key validation unchanged.
- `CoreBankDemo.PaymentsAPI/Models/PaymentRequest.cs:1-27` -- existing frozen DataAnnotations request contract.
- `CoreBankDemo.PaymentsAPI/Models/PaymentResponse.cs` -- restore the six-field frozen acknowledgement DTO recorded in `docs/bmad/planning-artifacts/epics.md:52`.
- `CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs` -- add only binding, validation aggregation, handler delegation, and HTTP result mapping.
- `CoreBankDemo.PaymentsAPI/PaymentIntakeServiceCollectionExtensions.cs` -- production-owned, testable MVC registration seam.
- `CoreBankDemo.PaymentsAPI/Program.cs:1-21` -- register/map controllers and suppress automatic model-state responses.
- `CoreBankDemo.CoreBankAPI/Controllers/TransactionsController.cs:14-43` and `CoreBankDemo.CoreBankAPI/Program.cs:26-37` -- read-only precedent for thin controllers and reachable manual model-state handling.
- `tests/CoreBankDemo.CoreBankAPI.Tests/TransactionsControllerTests.cs` -- read-only direct-controller test pattern.

## Tasks & Acceptance

**Execution:**
- [x] `CoreBankDemo.PaymentsAPI/Models/PaymentResponse.cs` -- restore the frozen response record.
- [x] `CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs` -- implement thin endpoint mapping for every matrix outcome.
- [x] `CoreBankDemo.PaymentsAPI/PaymentIntakeServiceCollectionExtensions.cs` and `Program.cs` -- enable controllers through a testable production seam, suppress the automatic invalid-model filter, and map controller routes.
- [x] `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsControllerTests.cs` -- cover validation, exact handler inputs/cancellation, stored/duplicate winner mapping, location, and invalid states.
- [x] `tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStorageRegistrationTests.cs` -- prove deployed MVC options suppress the automatic model-state filter.

**Acceptance Criteria:**
- Given a valid payment, when `POST /api/payments` stores it, then the response is `202 Accepted` only after handler completion and contains the frozen winner-derived response and location.
- Given a duplicate key with retry payload values different from the stored row, when submitted, then `202` returns the existing row's identity, amount, currency, status, and creation time without a second store.
- Given multiple request-model errors, when intake runs, then one `400` contains all errors and storage is not invoked.
- Given `dotnet test CoreBankDemo.Rebuild.slnf`, when Story 5.2 is complete, then all rebuild tests pass and PaymentsAPI remains above its coverage threshold.

## Spec Change Log

- 2026-08-28 (final review): escaped arbitrary transaction identities in `Location`, supplied a safe message for exception-only model errors, and added production-owned route-mapping verification.

## Design Notes

The durable row is the acknowledgement source. Mapping `ProcessedAt` from `PaymentSnapshot.CreatedAt` avoids a second clock read and makes duplicate acknowledgements stable. Convert the stored UTC `DateTime` explicitly to `DateTimeOffset` without changing its instant.

## Verification

**Commands:**
- `dotnet test CoreBankDemo.Rebuild.slnf` -- expected: all projects green and PaymentsAPI >=90% line coverage.
- `git diff --check` -- expected: no whitespace errors.

## Suggested Review Order

**Intake boundary**

- Delegates validated requests and maps durable handler outcomes without business logic.
  [`PaymentsController.cs:20`](../../../CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs#L20)

- Builds stable acknowledgements from persisted snapshots and safely escapes locations.
  [`PaymentsController.cs:50`](../../../CoreBankDemo.PaymentsAPI/Controllers/PaymentsController.cs#L50)

**Production wiring**

- Centralizes testable MVC validation and endpoint mapping configuration.
  [`PaymentIntakeServiceCollectionExtensions.cs:8`](../../../CoreBankDemo.PaymentsAPI/PaymentIntakeServiceCollectionExtensions.cs#L8)

- Activates payment intake in the deployed host.
  [`Program.cs:12`](../../../CoreBankDemo.PaymentsAPI/Program.cs#L12)

**Contract and verification**

- Restores the frozen six-field acknowledgement shape.
  [`PaymentResponse.cs:7`](../../../CoreBankDemo.PaymentsAPI/Models/PaymentResponse.cs#L7)

- Covers validation, winner mapping, header semantics, and escaped locations.
  [`PaymentsControllerTests.cs:67`](../../../tests/CoreBankDemo.PaymentsAPI.Tests/PaymentsControllerTests.cs#L67)

- Proves production registration exposes the POST route and manual validation behavior.
  [`PaymentStorageRegistrationTests.cs:78`](../../../tests/CoreBankDemo.PaymentsAPI.Tests/PaymentStorageRegistrationTests.cs#L78)
