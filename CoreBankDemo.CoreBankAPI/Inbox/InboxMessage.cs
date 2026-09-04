using CoreBankDemo.Messaging;

namespace CoreBankDemo.CoreBankAPI.Inbox;

/// <summary>
/// Incoming transaction-intake message (kernel inbox pattern, AD-4).
/// <see cref="IdempotencyKey"/> and <see cref="TransactionId"/> are separate
/// settable properties: the kernel's <see cref="IInboxMessage"/> requires the
/// former (dedupe identity, unique-indexed), while <see cref="TransactionId"/>
/// is this service's domain identity. Callers always populate both with the
/// same value; the entity itself does not enforce that equality — it is a
/// caller convention, not a runtime invariant (epic-4-context.md).
/// </summary>
public class InboxMessage : IInboxMessage
{
    // IInboxMessage / IMessage properties
    public Guid Id { get; set; }
    public required string IdempotencyKey { get; set; }
    public int PartitionId { get; set; }
    public string Status { get; set; } = MessageConstants.Status.Pending;
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string? TraceParent { get; set; }
    public string? TraceState { get; set; }

    // Domain-specific properties
    public required string FromAccount { get; set; }
    public required string ToAccount { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public required string TransactionId { get; set; }
    public string? ResponsePayload { get; set; }

    /// <inheritdoc/>
    public int Priority { get; set; } = MessageConstants.Priority.Standard;

    /// <inheritdoc/>
    public DateTime? HoldUntil { get; set; }
}
