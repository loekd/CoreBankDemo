---
name: load-test
description: "Run a full CoreBankDemo load test, wait for drain, and assert results via the LoadTestSupport API."
---

## 1. Start the load-test AppHost

See **aspire-launch** skill:

```bash
aspire start --apphost CoreBankDemo.LoadTests/CoreBankDemo.LoadTests.csproj --no-build --non-interactive
```

Wait for `loadtest-support` to be healthy before calling its endpoints:

```bash
aspire wait loadtest-support --non-interactive
```

## 2. Reset state

Truncates all inbox/outbox tables and resets the 10 test accounts to 10,000,000 each.

```
POST /reset
```

## 3. Wait for drain

Poll until the system is idle:

```
GET /assert/drain
```

```json
{ "isDrained": true, "outboxPending": 0, "inboxPending": 0, "completed": 1000, "failed": 0 }
```

Poll every 2–5 seconds until `isDrained == true`.

## 4. Assert results

```
GET /assert/results?expectedUnique=1000
```

`expectedUnique` = number of unique payment submissions sent. Omit to skip that check.

```json
{
  "allPassed": true,
  "checks": {
    "noFailedMessages":        { "passed": true },
    "noPendingMessages":       { "passed": true },
    "noDuplicateProcessing":   { "passed": true },
    "expectedUniqueProcessed": { "passed": true },
    "allSubmittedProcessed":   { "passed": true },
    "balanceConservation":     { "passed": true },
    "balancesCorrect":         { "passed": true }
  }
}
```

Assert `allPassed == true`. Inspect individual checks and their `detail` field on failure.

## 5. Stop

See **aspire-launch** skill.
