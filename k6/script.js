import http from 'k6/http';
import { check, sleep } from 'k6';
import exec from 'k6/execution';

// ---------------------------------------------------------------------------
// Configuration — override via --env flags or Aspire parameter injection
// ---------------------------------------------------------------------------
const PAYMENTS_URL      = __ENV.PAYMENTS_API_URL      || 'http://localhost:5294';
const SUPPORT_URL       = __ENV.LOAD_TEST_SUPPORT_URL  || 'http://localhost:5180';
const TRANSACTION_COUNT = parseInt(__ENV.TRANSACTION_COUNT || '1000', 10);
const VU_COUNT          = parseInt(__ENV.VU_COUNT          || '10', 10);
const DRAIN_TIMEOUT_MS  = parseInt(__ENV.DRAIN_TIMEOUT_MS  || `${5 * 60 * 1000}`, 10);
const TEARDOWN_TIMEOUT_SECONDS = Math.ceil((DRAIN_TIMEOUT_MS + 60_000) / 1000);

// Fixed load-test accounts seeded by k6/seed/01_corebank_seed.sql
const ACCOUNTS = [
    'NL01LOAD0000000001',
    'NL02LOAD0000000002',
    'NL03LOAD0000000003',
    'NL04LOAD0000000004',
    'NL05LOAD0000000005',
    'NL06LOAD0000000006',
    'NL07LOAD0000000007',
    'NL08LOAD0000000008',
    'NL09LOAD0000000009',
    'NL10LOAD0000000010',
];

// ---------------------------------------------------------------------------
// Shared state: pre-generate all idempotency keys so we can track them.
// Each VU gets its own slice of work. ~10% are intentional retries.
// ---------------------------------------------------------------------------
const RETRY_RATIO    = 0.10;
const RETRY_COUNT    = Math.floor(TRANSACTION_COUNT * RETRY_RATIO);
const UNIQUE_COUNT   = TRANSACTION_COUNT; // we want this many unique transactions processed

// IMPORTANT: __ITER is per-VU. Use scenario.iterationInTest for a deterministic
// global sequence shared across all VUs.

export const options = {
    vus: VU_COUNT,
    iterations: TRANSACTION_COUNT + RETRY_COUNT, // unique + deliberate retries
    setupTimeout: '2m',
    teardownTimeout: `${TEARDOWN_TIMEOUT_SECONDS}s`,
    thresholds: {
        // All requests must complete without unexpected HTTP errors
        'http_req_failed': ['rate<0.01'],
        // 95% of payment submissions under 2s
        'http_req_duration{type:payment}': ['p(95)<2000'],
    },
};

// ---------------------------------------------------------------------------
// Setup — called once before load. Resets database and verifies APIs.
// ---------------------------------------------------------------------------
export function setup() {
    console.log(`Starting load test: ${UNIQUE_COUNT} unique transactions, ${VU_COUNT} VUs, ${RETRY_COUNT} intentional retries`);
    console.log(`Payments API: ${PAYMENTS_URL}`);
    console.log(`LoadTestSupport: ${SUPPORT_URL}`);

    // Confirm LoadTestSupport is reachable
    const healthRes = http.get(`${SUPPORT_URL}/health`, { timeout: '10s' });
    const supportHealthy = check(healthRes, {
        'loadtest-support is healthy': (r) => r.status === 200,
        'loadtest-support: not 404': (r) => r.status !== 404,
    });
    if (!supportHealthy) {
        console.error(`LoadTestSupport health check failed: status=${healthRes.status}, body=${healthRes.body ? healthRes.body.substring(0, 200) : 'empty'}`);
        throw new Error(`LoadTestSupport is not reachable at ${SUPPORT_URL}/health - check that the service is running on the correct port`);
    }

    // Confirm Payments API is reachable
    const paymentsHealthRes = http.get(`${PAYMENTS_URL}/health`, { timeout: '10s' });
    const paymentsHealthy = check(paymentsHealthRes, {
        'payments-api is healthy': (r) => r.status === 200,
        'payments-api: not 404': (r) => r.status !== 404,
    });
    if (!paymentsHealthy) {
        console.error(`Payments API health check failed: status=${paymentsHealthRes.status}, body=${paymentsHealthRes.body ? paymentsHealthRes.body.substring(0, 200) : 'empty'}`);
        throw new Error(`Payments API is not reachable at ${PAYMENTS_URL}/health - check that the service is running on the correct port`);
    }

    // Reset database to clean state
    console.log('Resetting database to clean state...');
    const resetRes = http.post(`${SUPPORT_URL}/reset`, null, { timeout: '30s' });
    const resetSuccess = check(resetRes, {
        'database reset successful': (r) => r.status === 200,
        'reset: not 404 (endpoint exists)': (r) => r.status !== 404,
        'reset: not 500 (no server error)': (r) => r.status !== 500,
    });
    if (!resetSuccess) {
        console.error(`Database reset failed: status=${resetRes.status}, body=${resetRes.body ? resetRes.body.substring(0, 200) : 'empty'}`);
        throw new Error(`Database reset failed - cannot proceed with load test`);
    }

    if (resetRes.status === 200) {
        const resetData = JSON.parse(resetRes.body);
        console.log(`Reset complete: ${resetData.accountsReset} accounts, total balance: €${resetData.totalBalance.toLocaleString()}`);
    }

    return { startTime: Date.now() };
}

// ---------------------------------------------------------------------------
// Default function — the load test body. Each VU runs this repeatedly.
// ---------------------------------------------------------------------------
export default function () {
    const iterationIndex = exec.scenario.iterationInTest;
    const isRetry = iterationIndex >= UNIQUE_COUNT;
    const keyIndex = isRetry
        ? (iterationIndex - UNIQUE_COUNT)
        : iterationIndex;
    const messageId = `load-test-${keyIndex.toString().padStart(10, '0')}`;

    // Keep account selection tied to key index so retries replay the same shape.
    const fromIdx = keyIndex % ACCOUNTS.length;
    const toIdx   = (fromIdx + 1) % ACCOUNTS.length;

    const payload = JSON.stringify({
        fromAccount: ACCOUNTS[fromIdx],
        toAccount:   ACCOUNTS[toIdx],
        amount:      1.00,
        currency:    'EUR',
    });

    const params = {
        headers: {
            'Content-Type': 'application/json',
            'Idempotency-Key': messageId
        },
        tags: { type: 'payment' },
    };

    const res = http.post(`${PAYMENTS_URL}/api/payments`, payload, params);
    //console.log(`Added payment, status=${res.status}, body: ${res.body}`);
    
    // Detailed checks for better visibility, especially for chaos testing
    if (isRetry) {
        check(res, {
            'retry accepted (202) or conflict (409/202)': (r) => r.status === 202,
            'retry: not 404 (endpoint exists)': (r) => r.status !== 404,
            'retry: not 500 (no server error)': (r) => r.status !== 500,
            'retry: not 503 (service available)': (r) => r.status !== 503,
        }) || console.error(`Retry payment failed: status=${res.status}, body=${res.body.substring(0, 200)}, messageId=${messageId}`);
    } else {
        check(res, {
            'payment accepted (202)': (r) => r.status === 202,
            'payment: not 400 (valid request)': (r) => r.status !== 400,
            'payment: not 404 (endpoint exists)': (r) => r.status !== 404,
            'payment: not 500 (no server error)': (r) => r.status !== 500,
            'payment: not 503 (service available)': (r) => r.status !== 503,
        }) || console.error(`Payment failed: status=${res.status}, body=${res.body.substring(0, 200)}, messageId=${messageId}, from=${ACCOUNTS[fromIdx]}, to=${ACCOUNTS[toIdx]}`);
    }
    
    sleep(0.1);
    //
    const paymentsOutboxRes = http.get(`${SUPPORT_URL}/payments/outbox`);
    check(paymentsOutboxRes, {
        'outbox (200)': (r) => r.status === 200
    }) || console.error(`Outbox error, body=${paymentsOutboxRes.body.substring(0, 200)}`);
    //console.log(`Checked the outbox, status: ${paymentsOutboxRes.status}, body: ${paymentsOutboxRes.body}`);
}

// ---------------------------------------------------------------------------
// Teardown — called once after load. Waits for inbox drain, then asserts.
// ---------------------------------------------------------------------------
export function teardown(data) {
    console.log(`Load phase complete after ${((Date.now() - data.startTime) / 1000).toFixed(1)}s. Waiting for inbox to drain...`);

    // First, verify that messages were actually created in the outbox
    const paymentsOutboxCheck = http.get(`${SUPPORT_URL}/payments/outbox`);
    if (paymentsOutboxCheck.status === 200) {
        const messages = JSON.parse(paymentsOutboxCheck.body);
        console.log(`Payments outbox has ${messages.length} messages (showing up to 50 most recent)`);
        if (messages.length === 0) {
            console.error('ERROR: No outbox messages found! Payments API is not creating outbox entries.');
            console.error('This means the API endpoint is returning 202 but not actually queuing transactions.');
            console.error('Check that the Payments API is running on the correct port and k6 is hitting the right endpoint.');
            return;
        }
    } else {
        console.error(`Failed to check payments outbox: status=${paymentsOutboxCheck.status}`);
    }

    // Poll /assert/drain until all messages are processed (max 5 minutes)
    const maxWaitMs  = DRAIN_TIMEOUT_MS;
    const pollMs     = 500; // Poll every 500ms
    const deadline   = Date.now() + maxWaitMs;
    let drained      = false;

    let pollCount = 0;
    while (Date.now() < deadline) {
        const drainRes = http.get(`${SUPPORT_URL}/assert/drain`);
        if (drainRes.status === 200) {
            const body = JSON.parse(drainRes.body);
            pollCount++;

            // Log every poll for first 10, then every 5th poll
            if (pollCount <= 10 || pollCount % 5 === 0) {
                console.log(`  [Poll ${pollCount}] Drain status — outboxPending: ${body.outboxPending}, inboxPending: ${body.inboxPending}, completed: ${body.completed}, failed: ${body.failed}`);
            }

            if (body.isDrained) {
                console.log(
                    `  Fully Drained after ${pollCount} polls (${((Date.now() - data.startTime) / 1000).toFixed(1)}s total). ` +
                    `Final status — outboxPending: ${body.outboxPending}, inboxPending: ${body.inboxPending}, completed: ${body.completed}, failed: ${body.failed}`
                );
                drained = true;
                break;
            }
        } else {
            console.error(`Drain check failed: status=${drainRes.status}, body=${drainRes.body.substring(0, 200)}`);
            // Continue polling even on errors (chaos testing might cause transient failures)
        }
        sleep(pollMs / 1000);
    }

    // If not drained, check inbox/outbox details for debugging
    if (!drained) {
        console.error('TIMEOUT: Messages did not drain. Checking inbox/outbox details...');

        const paymentsOutbox = http.get(`${SUPPORT_URL}/payments/outbox`);
        if (paymentsOutbox.status === 200) {
            const messages = JSON.parse(paymentsOutbox.body);
            console.error(`  Payments Outbox: ${messages.length} recent messages`);
            if (messages.length > 0) {
                console.error(`    Sample: ${JSON.stringify(messages[0])}`);
            }
        }

        const coreBankInbox = http.get(`${SUPPORT_URL}/corebank/inbox`);
        if (coreBankInbox.status === 200) {
            const messages = JSON.parse(coreBankInbox.body);
            console.error(`  CoreBank Inbox: ${messages.length} recent messages`);
            if (messages.length > 0) {
                console.error(`    Sample: ${JSON.stringify(messages[0])}`);
            }
        }
    }

    check(null, {
        'inbox drained within timeout': () => drained,
    });

    if (!drained) {
        console.error(`Inbox did not drain within ${(maxWaitMs / 1000).toFixed(0)}s — aborting assertions`);
        return;
    }

    // Run the full assertion suite
    const assertRes = http.get(`${SUPPORT_URL}/assert/results?expectedUnique=${UNIQUE_COUNT}`);
    const assertOk = check(assertRes, {
        'assert endpoint returned 200': (r) => r.status === 200,
        'assert: not 404 (endpoint exists)': (r) => r.status !== 404,
        'assert: not 500 (no server error)': (r) => r.status !== 500,
    });

    if (assertRes.status !== 200) {
        console.error(`Assert endpoint failed: status=${assertRes.status}, body=${assertRes.body.substring(0, 500)}`);
        return;
    }

    const result = JSON.parse(assertRes.body);
    console.log('\n========== ASSERTION RESULTS ==========');
    console.log(JSON.stringify(result, null, 2));
    console.log('========================================\n');

    check(result, {
        'all checks passed':              (r) => r.allPassed === true,
        'no failed inbox messages':       (r) => r.checks.noFailedMessages.passed === true,
        'no pending inbox messages':      (r) => r.checks.noPendingMessages.passed === true,
        'no duplicate processing':        (r) => r.checks.noDuplicateProcessing.passed === true,
        'expected unique count processed': (r) => r.checks.expectedUniqueProcessed.passed === true,
        'all submitted transactions processed': (r) => r.checks.allSubmittedProcessed.passed === true,
        'balance conservation':           (r) => r.checks.balanceConservation.passed === true,
        'balances correct':               (r) => r.checks.balancesCorrect.passed === true,
    });

    if (!result.allPassed) {
        console.error('ONE OR MORE ASSERTIONS FAILED — see details above');

        // Log balance discrepancies if present
        if (result.checks.balancesCorrect && !result.checks.balancesCorrect.passed) {
            console.error('\n========== BALANCE DISCREPANCIES ==========');
            console.error(JSON.stringify(result.checks.balancesCorrect.discrepancies, null, 2));
            console.error('==========================================\n');
        }
    } else {
        console.log('✓ All exactly-once guarantees and balance correctness verified successfully');
    }
}


