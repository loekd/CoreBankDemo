# ADR-005: Resilience testing with DevProxy and K6

**Date:** 2026-05-23  
**Status:** Accepted  
**Deciders:** Architecture team  

## Context

Resilience patterns (Inbox, Outbox, retries) are worthless if they have never been exercised under realistic failure conditions. Manual testing cannot reproduce the combination of load, latency, and random errors that surfaces race conditions and message loss. We need repeatable, automated chaos testing that runs in development without polluting production code.

## Decision

Use Microsoft DevProxy for fault injection between PaymentsAPI and CoreBankAPI, and K6 for automated load testing. Both are launched and configured by the Aspire AppHost so the test environment is self-contained.

## Implementation

- `CoreBankDemo.AppHost` conditionally adds DevProxy via `builder.AddDevProxyExecutable("devproxy")` when `Features:UseDevProxy` is enabled.
- DevProxy config (`AppHost/devproxy/config/devproxyrc.json`) injects 503, 429, and 500 errors at a 5% rate on CoreBankAPI's `/api/accounts/validate` endpoint.
- When DevProxy is active, PaymentsAPI routes HTTP through `HTTP_PROXY=http://127.0.0.1:8000` (Dapr is disabled to keep traffic routed through the proxy).
- K6 script (`k6/script.js`) sends configurable transaction volume with 10% intentional retries, then asserts exactly-once delivery and balance conservation via the LoadTestSupport API.
- The `CoreBankDemo.LoadTests` AppHost provides a disposable test infrastructure with its own databases, K6, and assertion endpoints.

## Consequences

### Positive
- Resilience patterns are validated under real concurrency with injected failures every CI run.
- Tests assert business invariants (balance conservation, no duplicates) — not just HTTP status codes.

### Negative / Trade-offs
- DevProxy adds a network hop and cannot simulate all failure modes (e.g., partial writes).
- K6 and DevProxy are additional tools developers must install locally (handled by the devcontainer).

## Key takeaway

> If you haven't tested your resilience patterns under injected failures and concurrent load, you don't know if they work.
