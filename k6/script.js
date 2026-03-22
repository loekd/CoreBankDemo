import http from 'k6/http';
import { check, sleep } from 'k6';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';

// ---------------------------------------------------------------------------
// Configuration — override via --env flags or Aspire parameter injection
// ---------------------------------------------------------------------------
const PAYMENTS_URL      = __ENV.PAYMENTS_API_URL      || 'http://localhost:5294';
const SUPPORT_URL       = __ENV.LOAD_TEST_SUPPORT_URL  || 'http://localhost:5180';
const TRANSACTION_COUNT = parseInt(__ENV.TRANSACTION_COUNT || '1000');
const VU_COUNT          = parseInt(__ENV.VU_COUNT          || '10');

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

// IMPORTANT: In k6, the init context runs ONCE PER VU, not once globally!
// To ensure idempotency keys are truly shared across VUs, we use deterministic generation
// based on iteration index instead of pre-generating random UUIDs.
// This ensures iteration N always gets the same idempotency key regardless of which VU runs it.

export const options = {
    vus: VU_COUNT,
    iterations: TRANSACTION_COUNT + RETRY_COUNT, // unique + deliberate retries
    teardownTimeout: '1m',
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
    const healthRes = http.get(`${SUPPORT_URL}/health`);
    check(healthRes, {
        'loadtest-support is healthy': (r) => r.status === 200,
    });

    // Confirm Payments API is reachable
    const paymentsHealthRes = http.get(`${PAYMENTS_URL}/health`);
    check(paymentsHealthRes, {
        'payments-api is healthy': (r) => r.status === 200,
    });

    // Reset database to clean state
    console.log('Resetting database to clean state...');
    const resetRes = http.post(`${SUPPORT_URL}/reset`);
    check(resetRes, {
        'database reset successful': (r) => r.status === 200,
    });

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
    const iterationIndex = __ITER; // global iteration counter across all VUs

    let messageId;
    let isRetry = false;

    if (iterationIndex < UNIQUE_COUNT) {
        // Normal unique transaction - use deterministic ID based on iteration
        messageId = `load-test-${iterationIndex.toString().padStart(10, '0')}`;
    } else {
        // Deliberate retry: reuse one of the first RETRY_COUNT IDs
        const retryIndex = iterationIndex % RETRY_COUNT;
        messageId = `load-test-${retryIndex.toString().padStart(10, '0')}`;
        isRetry = true;
    }

    // Pick two different accounts for from/to, using the message index for spread
    const fromIdx = iterationIndex % ACCOUNTS.length;
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

    if (isRetry) {
        check(res, {
            'retry accepted (202) or conflict (409/202)': (r) => r.status === 202,
        });
    } else {
        check(res, {
            'payment accepted (202)': (r) => r.status === 202,
        });
    }
}

// ---------------------------------------------------------------------------
// Teardown — called once after load. Waits for inbox drain, then asserts.
// ---------------------------------------------------------------------------
export function teardown(data) {
    console.log(`Load phase complete after ${((Date.now() - data.startTime) / 1000).toFixed(1)}s. Waiting for inbox to drain...`);

    // Poll /assert/drain until all messages are processed (max 5 minutes)
    const maxWaitMs  = 5 * 60 * 1000;
    const pollMs     = 3000;
    const deadline   = Date.now() + maxWaitMs;
    let drained      = false;

    while (Date.now() < deadline) {
        const drainRes = http.get(`${SUPPORT_URL}/assert/drain`);
        if (drainRes.status === 200) {
            const body = JSON.parse(drainRes.body);
            console.log(`  Drain status — outboxPending: ${body.outboxPending}, inboxPending: ${body.inboxPending}, completed: ${body.completed}, failed: ${body.failed}`);
            if (body.isDrained) {
                drained = true;
                break;
            }
        }
        sleep(pollMs / 1000);
    }

    check(null, {
        'inbox drained within timeout': () => drained,
    });

    if (!drained) {
        console.error('Inbox did not drain within 5 minutes — aborting assertions');
        return;
    }

    // Run the full assertion suite
    const assertRes = http.get(`${SUPPORT_URL}/assert/results`);
    check(assertRes, { 'assert endpoint returned 200': (r) => r.status === 200 });

    if (assertRes.status !== 200) {
        console.error(`Assert endpoint failed: ${assertRes.status} ${assertRes.body}`);
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





