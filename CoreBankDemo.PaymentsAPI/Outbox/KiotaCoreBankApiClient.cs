using CoreBankDemo.Messaging;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Kiota.Abstractions;
using Polly.Timeout;
using GeneratedClient = CoreBankDemo.PaymentsAPI.GeneratedClients.CoreBank.CoreBankApiKiotaClient;
using GeneratedModels = CoreBankDemo.PaymentsAPI.GeneratedClients.CoreBank.Models;

namespace CoreBankDemo.PaymentsAPI.Outbox;

/// <summary>
/// Adapts the Kiota client generated (build-time, from CoreBankAPI's
/// checked-in <c>corebank-api.json</c>) into <see cref="ICoreBankApiClient"/>
/// (story 5.3). The sole boundary where a Kiota-generated model is ever
/// touched — every public method here accepts and returns only
/// application-owned types (<see cref="CoreBankApiContracts"/>).
///
/// <para>
/// Delivery-outcome classification follows AD-11 exactly: any 2xx response
/// that deserializes into a well-formed body is
/// <see cref="CoreBankClientOutcome.Success"/>; every non-2xx response (the
/// generated client throws <see cref="ApiException"/> for those), empty or
/// malformed required success data, a timeout, or any other exception is
/// <see cref="CoreBankClientOutcome.Retry"/> — with two documented exceptions,
/// each CoreBank's own answer rather than a transport fault: the
/// cancellation's <c>409</c>, classified as
/// <see cref="CoreBankClientOutcome.Conflict"/>, and the submission's
/// <c>400</c>, classified as <see cref="CoreBankClientOutcome.Rejected"/>
/// (ADR-023) — this adapter never retries
/// anything itself, it only classifies the outcome for a future delivery
/// strategy (story 5.4) to act on. Each retry outcome preserves *why* it
/// happened via <see cref="CoreBankRetryReason"/>, and a non-2xx response
/// additionally preserves the actual HTTP status CoreBankAPI returned
/// (<see cref="CoreBankResult{T}.StatusCode"/>) — never the response body or
/// a Kiota-generated error type.
/// </para>
///
/// <para>
/// Caller-requested cancellation is the one outcome that is never
/// classified: when <paramref name="cancellationToken"/> (per method) is
/// itself the reason an <see cref="OperationCanceledException"/> was raised,
/// it propagates unchanged instead of becoming a retry outcome — cooperative
/// cancellation must stay cooperative.
/// </para>
/// </summary>
internal sealed class KiotaCoreBankApiClient(GeneratedClient client) : ICoreBankApiClient
{
    private const int BadRequest = 400;

    public Task<CoreBankResult<AccountDetails>> GetAccountDetailsAsync(
        string accountNumber, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);

        return ExecuteAsync(
            async ct =>
            {
                var response = await client.Api.Accounts[accountNumber]
                    .GetAsync(ConfigureTraceContext, ct)
                    .ConfigureAwait(false);

                if (response?.AccountNumber is null
                    || response.AccountHolderName is null
                    || response.Balance is null
                    || response.Currency is null
                    || response.IsActive is null
                    || response.CreatedAt is null
                    || IsBlank(response.AccountNumber)
                    || IsBlank(response.AccountHolderName)
                    || IsBlank(response.Currency)
                    || IsMismatchedIdentifier(accountNumber, response.AccountNumber))
                {
                    return null;
                }

                return new AccountDetails(
                    response.AccountNumber,
                    response.AccountHolderName,
                    response.Balance.Value,
                    response.Currency,
                    response.IsActive.Value,
                    response.CreatedAt.Value,
                    response.UpdatedAt);
            },
            cancellationToken);
    }

    public Task<CoreBankResult<TransactionSubmission>> ProcessTransactionAsync(
        TransactionSubmissionRequest request, CancellationToken cancellationToken, bool executeInline = false)
    {
        // A null request is a programmer error, not a transport outcome --
        // it must throw immediately rather than fall into ExecuteAsync's
        // generic catch and be silently reported as a Retry.
        ArgumentNullException.ThrowIfNull(request);

        return ExecuteAsync(
            async ct =>
            {
                var body = new GeneratedModels.TransactionRequest
                {
                    FromAccount = request.FromAccount,
                    ToAccount = request.ToAccount,
                    Amount = request.Amount,
                    Currency = request.Currency,
                    TransactionId = request.TransactionId
                };
                var response = await client.Api.Transactions.Process
                    .PostAsync(
                        body,
                        configuration =>
                        {
                            ConfigureTraceContext(configuration);
                            if (executeInline)
                            {
                                configuration.Headers.Add("X-Execute-Mode", "inline");
                            }

                            ConfigurePriorityHeader(configuration, request.Priority);
                        },
                        ct)
                    .ConfigureAwait(false);

                return ToSubmission(request.TransactionId, response?.TransactionId, response?.Status, response?.ProcessedAt);
            },
            cancellationToken,
            // ADR-023: a 400 to a submission is CoreBank's verdict on the
            // payment, never a transport fault -- for this operation only.
            badRequestIsVerdict: true);
    }

    /// <summary>
    /// Only ever on the wire for a non-standard priority, so the standard
    /// rail's request stays byte-identical.
    /// </summary>
    private static void ConfigurePriorityHeader<TQueryParameters>(
        RequestConfiguration<TQueryParameters> configuration, int priority)
        where TQueryParameters : class, new()
    {
        if (priority != MessageConstants.Priority.Standard)
        {
            configuration.Headers.Add(
                "X-Payment-Priority",
                priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// The shared well-formedness gate for every <c>TransactionResponse</c>-
    /// shaped body: <see langword="null"/> (malformed) unless every required
    /// field is present, non-blank, and the echoed identifier matches.
    /// </summary>
    private static TransactionSubmission? ToSubmission(
        string requestedTransactionId, string? transactionId, string? status, DateTimeOffset? processedAt)
    {
        if (transactionId is null
            || status is null
            || processedAt is null
            || IsBlank(transactionId)
            || IsBlank(status)
            || IsMismatchedIdentifier(requestedTransactionId, transactionId))
        {
            return null;
        }

        return new TransactionSubmission(transactionId, status, processedAt.Value);
    }

    public Task<CoreBankResult<TransactionSubmission>> CancelTransactionAsync(
        TransactionSubmissionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ExecuteAsync(
            async ct =>
            {
                var body = new GeneratedModels.TransactionRequest
                {
                    FromAccount = request.FromAccount,
                    ToAccount = request.ToAccount,
                    Amount = request.Amount,
                    Currency = request.Currency,
                    TransactionId = request.TransactionId
                };
                var response = await client.Api.Transactions.Cancel
                    .PostAsync(
                        body,
                        configuration =>
                        {
                            ConfigureTraceContext(configuration);
                            ConfigurePriorityHeader(configuration, request.Priority);
                        },
                        ct)
                    .ConfigureAwait(false);

                return ToSubmission(request.TransactionId, response?.TransactionId, response?.Status, response?.ProcessedAt);
            },
            cancellationToken,
            // A 409 is CoreBank's own answer ("not cancellable, here is the
            // current state"), never a transport fault: classified as
            // Conflict with the reported state, or -- when its body is
            // unreadable -- left to the ordinary non-2xx retry classification.
            exception => exception is GeneratedModels.TransactionConflictResponse conflict
                && ToSubmission(request.TransactionId, conflict.TransactionId, conflict.Status, conflict.ProcessedAt) is { } current
                    ? CoreBankResult<TransactionSubmission>.Conflict(current)
                    : null);
    }

    public Task<CoreBankResult<TransactionStatus>> GetTransactionStatusAsync(
        string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        return ExecuteAsync(
            async ct =>
            {
                var response = await client.Api.Transactions[idempotencyKey]
                    .GetAsync(ConfigureTraceContext, ct)
                    .ConfigureAwait(false);

                if (response?.TransactionId is null
                    || response.Status is null
                    || IsBlank(response.TransactionId)
                    || IsBlank(response.Status)
                    || IsMismatchedIdentifier(idempotencyKey, response.TransactionId))
                {
                    return null;
                }

                return new TransactionStatus(
                    response.TransactionId,
                    response.Status,
                    response.ReceivedAt,
                    response.ProcessedAt);
            },
            cancellationToken);
    }

    /// <summary>
    /// Shared exception-to-outcome classification for every operation above.
    /// A <see langword="null"/> <paramref name="operation"/> result means
    /// empty or malformed required success data (edge-case matrix) — that is
    /// a <see cref="CoreBankRetryReason.MalformedResponse"/> retry, not an
    /// exception.
    /// </summary>
    private static async Task<CoreBankResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<T?>> operation,
        CancellationToken cancellationToken,
        Func<ApiException, CoreBankResult<T>?>? classifyApiException = null,
        bool badRequestIsVerdict = false)
        where T : class
    {
        var statusCapture = LastResponseStatusHandler.BeginCapture();
        try
        {
            var value = await operation(cancellationToken).ConfigureAwait(false);
            return value is null
                ? CoreBankResult<T>.Retry(CoreBankRetryReason.MalformedResponse)
                : CoreBankResult<T>.Success(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller asked for this — propagate cooperatively rather than
            // reporting it as a transport outcome (edge-case matrix).
            throw;
        }
        catch (JsonException)
        {
            var statusCode = statusCapture.StatusCode;
            return statusCode is >= 400 and <= 599
                ? RejectionOrRetry<T>(statusCode, badRequestIsVerdict)
                : CoreBankResult<T>.Retry(CoreBankRetryReason.MalformedResponse);
        }
        catch (ApiException ex)
        {
            // Any non-2xx response -- a mapped ErrorResponse (itself an
            // ApiException) or an unmapped status. An operation may first
            // classify a specific mapped status as a non-retry outcome (the
            // cancel's 409); otherwise preserve only the status code the
            // generated client already surfaces on the base ApiException
            // type, never the generated error body.
            return classifyApiException?.Invoke(ex)
                ?? RejectionOrRetry<T>(ex.ResponseStatusCode, badRequestIsVerdict);
        }
        catch (TimeoutRejectedException)
        {
            // The standard resilience pipeline's own attempt/total-request
            // timeout fired (Microsoft.Extensions.Http.Resilience, applied
            // to every named HttpClient -- including "corebank-api" -- via
            // ServiceDefaults' ConfigureHttpClientDefaults). Recognized
            // explicitly so a real timeout is classified as Timeout rather
            // than falling through to the generic TransportException below.
            return CoreBankResult<T>.Retry(CoreBankRetryReason.Timeout);
        }
        catch (OperationCanceledException)
        {
            // A timeout fired independently of the caller's own token (e.g.
            // HttpClient.Timeout with no resilience pipeline attached, such
            // as in a test).
            return CoreBankResult<T>.Retry(CoreBankRetryReason.Timeout);
        }
        catch (Exception)
        {
            // A non-2xx status is still meaningful diagnostic context even
            // when Kiota's own error-body deserialization throws before it
            // can construct an ApiException (e.g. a malformed mapped error
            // body) -- LastResponseStatusHandler observed the real status
            // directly from the HTTP pipeline for exactly this case, so it
            // isn't lost to a generic TransportException.
            var statusCode = statusCapture.StatusCode;
            return statusCode is >= 400 and <= 599
                ? RejectionOrRetry<T>(statusCode, badRequestIsVerdict)
                : CoreBankResult<T>.Retry(CoreBankRetryReason.TransportException);
        }
    }

    /// <summary>
    /// Classifies a status-bearing non-2xx answer. Only an operation that
    /// opted in (<paramref name="badRequestIsVerdict"/> -- the transaction
    /// submission, ADR-023) reads a <c>400</c> as CoreBank's verdict; every
    /// other status, and a <c>400</c> to any other operation, stays a
    /// <see cref="CoreBankRetryReason.TransportRejection"/> retry.
    /// </summary>
    private static CoreBankResult<T> RejectionOrRetry<T>(int? statusCode, bool badRequestIsVerdict)
        where T : class =>
        badRequestIsVerdict && statusCode == BadRequest
            ? CoreBankResult<T>.Rejected(BadRequest)
            : CoreBankResult<T>.Retry(CoreBankRetryReason.TransportRejection, statusCode);

    /// <summary>
    /// A required response string that is missing entirely already fails an
    /// explicit <c>is null</c> guard; this additionally catches the
    /// malformed edge case where CoreBankAPI returns the field present but
    /// empty or whitespace-only (edge-case matrix).
    /// </summary>
    private static bool IsBlank(string value) => string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// A response echoing back an account/transaction identifier that
    /// differs from the one this call actually requested is malformed data,
    /// not a different (valid) success -- compared ordinally, since these
    /// are opaque wire identifiers, never culture-sensitive text.
    /// </summary>
    private static bool IsMismatchedIdentifier(string requested, string returned) =>
        !string.Equals(requested, returned, StringComparison.Ordinal);

    /// <summary>
    /// Propagates the current W3C trace context (AD-3/AD-8, observability
    /// skill) onto the outgoing request. Deliberately a no-op when there is
    /// no ambient <see cref="Activity"/> — never invents headers (edge-case
    /// matrix).
    /// </summary>
    private static void ConfigureTraceContext<TQueryParameters>(
        RequestConfiguration<TQueryParameters> configuration)
        where TQueryParameters : class, new()
    {
        var activity = Activity.Current;
        if (activity?.Id is null)
            return;

        configuration.Headers.Add("traceparent", activity.Id);

        if (!string.IsNullOrEmpty(activity.TraceStateString))
            configuration.Headers.Add("tracestate", activity.TraceStateString);
    }
}
