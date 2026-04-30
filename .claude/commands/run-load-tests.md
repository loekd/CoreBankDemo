# Run CoreBank Load Test

Use the **load-test** skill to execute a full end-to-end load test.

## Before you start

Read the load-test skill in full before executing any step. Do not skip ahead.

## Critical rules — read before touching anything

- **Never run k6 manually.** It starts automatically with the AppHost. If you don't see k6 output, wait — do not intervene.
- **Never call `/reset` unless the user explicitly asked for a database reset.** It is destructive and cannot be undone.
- **The MCP endpoint is `http://localhost:5181/` (root path).** Not `/mcp`, not `/tools`. Root.
- **Every MCP session requires initialization.** If `$SESSION_ID` is empty after the init step, stop immediately and report it. Do not proceed with empty session ID — all subsequent calls will silently fail.
- **You may only use `aspire` CLI and `curl`.** No other bash commands.
- **Default transaction count is 100.** If the test completes with 100 transactions, that's correct per `CoreBankDemo.LoadTests/appsettings.json`. To run more (e.g., 1000), pass `TransactionCount=1000` as a command-line parameter to aspire start.

## Verify session ID before continuing

After the initialize call, before doing anything else, verify:

```bash
echo "Session ID: $SESSION_ID"
```

If the output is `Session ID:` with nothing after the colon — stop. Do not continue. Report:
> "MCP session initialization failed: no session ID was returned. The service may not be ready yet."

Then wait 5 seconds and retry the initialize call once. If it fails again, stop and report the raw response.

## Expected response format

MCP responses use SSE format. A successful response looks like:

```
event: message
data: {"result":{"content":[{"type":"text","text":"..."}]},"id":3,"jsonrpc":"2.0"}
```

An empty response body, a 404, or a non-SSE response means the service is not ready or the endpoint is wrong. Do not interpret silence as success.

## Waiting for healthy status

After starting the AppHost, wait for `loadtest-support` to report healthy before initializing the MCP session. If `aspire wait` times out, report it — do not proceed.

## After poll_until_drained

The `poll_until_drained` tool streams progress notifications as SSE events during polling. Always pass `minimumExpectedCompleted` (typically 100 for default config, 1000 for larger runs) to prevent false drain detection while k6 is still submitting payments. 

**To display progress to the user:**
- Use `tee` to capture full response while parsing progress messages
- Use grep/sed to extract progress message strings from SSE events
- Display each progress line with formatting (e.g., "  ▶ Poll 1: 42/1000 processed...")
- Save full response to temp file for later extraction of final `isDrained` status

See the load-test skill SKILL.md for the complete helper function pattern.

Only call `get_assertion_results` if `isDrained` is `true` in the drain response. If `isDrained` is `false` or the call timed out, report:
> "Drain did not complete within the timeout. Do not assert — results would be incomplete."

## On assertion failure

If `allPassed` is not `true`, inspect inbox/outbox before drawing conclusions. Report:
- Which checks failed
- Counts from relevant inbox/outbox tools
- Your interpretation of what went wrong

Do not just report the raw JSON. Explain what it means.

## After assertions — analyze traces

Once `get_assertion_results` has returned (pass or fail), invoke the **corebank-trace-analysis** skill. Pass the test start and end timestamps so it can scope its Jaeger queries correctly. Do this before stopping the AppHost.

## When you are done

Report a summary with:
1. Whether the test passed or failed
2. How many unique payments were processed
3. Any failed checks and their likely cause
4. Key findings from trace analysis (errors, slowest spans, bottleneck)
5. Whether the AppHost was stopped cleanly