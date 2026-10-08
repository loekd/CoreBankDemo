using System.Text.Json;
using CoreBankDemo.Messaging;

namespace CoreBankDemo.PaymentsAPI.Outbox;

/// <summary>
/// Reads the business outcome cached on an outbox row
/// (<see cref="OutboxMessage.ResponsePayload"/>, a serialized
/// <see cref="TransactionSubmission"/>). Written by
/// <see cref="HttpForwardOutboxDeliveryStrategy"/> on delivery and upgraded by
/// <see cref="IOutboxRepository.RecordCommittedOutcomeAsync"/> from a
/// transaction event. This is the payment's business status; the row's
/// <see cref="OutboxMessage.Status"/> is transport state only (AD-11) and its
/// <c>Completed</c> means "delivered to CoreBank", not "executed".
/// </summary>
internal static class CachedTransactionOutcome
{
    /// <summary>
    /// Returns the cached submission, or <see langword="null"/> when there is
    /// none, it is corrupt, or it carries no status.
    /// </summary>
    public static TransactionSubmission? TryRead(string? responsePayload)
    {
        if (string.IsNullOrEmpty(responsePayload))
        {
            return null;
        }

        try
        {
            var submission = JsonSerializer.Deserialize<TransactionSubmission>(responsePayload);
            return string.IsNullOrWhiteSpace(submission?.Status) ? null : submission;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="status"/> is an outcome CoreBank committed:
    /// <c>Completed</c>, <c>Failed</c> or <c>Cancelled</c>. CoreBank's
    /// <c>Pending</c> acknowledgement is not.
    /// </summary>
    public static bool IsCommitted(string status) =>
        status is MessageConstants.Status.Completed
            or MessageConstants.Status.Failed
            or MessageConstants.Status.Cancelled;
}
