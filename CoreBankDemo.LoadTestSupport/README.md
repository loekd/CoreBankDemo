# CoreBank LoadTestSupport

This project contains support infrastructure for load testing and diagnostics. **It does not contain any production code** and should only be used in test environments.

## Purpose

LoadTestSupport provides read-only access to both CoreBank and Payments databases for monitoring, assertions, and test orchestration. It exposes endpoints that allow load testing tools to:

- Reset database state to a clean baseline
- Monitor inbox/outbox message processing
- Verify exactly-once delivery guarantees
- Assert correctness of account balances and transaction processing

## Usage with k6

The k6 load tests (`k6/script.js`) use LoadTestSupport endpoints to orchestrate end-to-end test scenarios:

1. **Setup phase**: Calls `/reset` to truncate inbox/outbox tables and reset all load test accounts to their initial balance
2. **Load phase**: Generates payment traffic against the Payments API (not LoadTestSupport)
3. **Teardown phase**: Polls `/assert/drain` until all messages are processed, then calls `/assert/results` (optionally with `?expectedUnique=<n>`) to verify exactly-once semantics, balance conservation, and correctness

## Available Endpoints

- `/reset` - Reset database to clean state
- `/assert/drain` - Poll until inbox/outbox are fully drained
- `/assert/results` - Run full assertion suite (supports optional `expectedUnique` query parameter)
- `/corebank/inbox` - View CoreBank inbox messages
- `/corebank/outbox` - View CoreBank outbox messages
- `/payments/inbox` - View Payments inbox messages
- `/payments/outbox` - View Payments outbox messages
