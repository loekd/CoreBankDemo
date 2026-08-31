# Run CoreBank Load Test

Use the **load-test** skill to execute the full acceptance workflow.

## Critical rules

- Start only `CoreBankDemo.LoadTests`; never start the regular AppHost for this workflow.
- k6 starts automatically after the reset initializer succeeds. Never run k6 manually.
- The reset initializer owns the destructive reset and fails before k6 on an invalid response.
- The MCP endpoint is `http://localhost:5181/` and requires initialization.
- Treat the k6 state gate and the final trace/order verdict as separate required results.
- Any startup, k6, drain, REST/MCP, Tier-2 ordering, or trace failure makes the final verdict fail.

## Run sequence

1. Run `dotnet test CoreBankDemo.Rebuild.slnf` and require both tiers and coverage gates to pass.
2. Start `CoreBankDemo.LoadTests` with the aspire-launch skill.
3. Wait for the automatic initializer and k6 resources to finish; require k6 exit code 0.
4. Capture the exact run start/end timestamps.
5. Call REST and MCP assertion endpoints for the same run and compare their JSON field-for-field.
6. Require four-store completed cardinality `N/N/3N/3N`, zero failed/non-terminal rows, exact canonical accounts, dedupe, and balances.
7. Run the replicated Inbox and Outbox Tier-2 ordering tests.
8. Use the corebank-trace-analysis skill for the exact run window. Require complete `traceparent`/`tracestate`, both replica identities, and no same-store/partition span overlap.
9. Record all evidence and classify every failure as a code defect or harness mismatch.
10. Stop the LoadTests AppHost with the aspire-launch skill.

The run is accepted only when both the k6 state gate and the trace/order verdict are green.
