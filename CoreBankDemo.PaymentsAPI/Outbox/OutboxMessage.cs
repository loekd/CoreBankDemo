using CoreBankDemo.Messaging;

namespace CoreBankDemo.PaymentsAPI.Outbox;

public class OutboxMessage : IOutboxMessage
{
    public Guid Id { get; set; }
    public required string IdempotencyKey { get; set; }
    public int PartitionId { get; set; }
    public string Status { get; set; } = MessageConstants.Status.Pending;
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string? TraceParent { get; set; }
    public string? TraceState { get; set; }

    public required string TransactionId { get; set; }
    public required string FromAccount { get; set; }
    public required string ToAccount { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }

    /// <summary>
    /// Serialized delivery outcome (mirrors CoreBank's own
    /// <c>InboxMessage.ResponsePayload</c>), populated on every completed
    /// delivery -- inline and background alike -- by
    /// <see cref="HttpForwardOutboxDeliveryStrategy.ForwardAsync"/> (spec:
    /// add-instant-payment-rail, review loop 1). <c>Status</c> above is
    /// transport-state-only (AD-11) and never distinguishes a committed
    /// business success from a committed business rejection; this payload is
    /// what lets a duplicate replay recover that distinction.
    /// </summary>
    public string? ResponsePayload { get; set; }
}