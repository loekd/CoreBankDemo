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

    /// <inheritdoc/>
    public int Priority { get; set; } = MessageConstants.Priority.Standard;

    /// <inheritdoc/>
    public DateTime? HoldUntil { get; set; }

    /// <summary>
    /// The PostgreSQL transaction id of the accepting transaction, assigned by
    /// the database on insert (<c>pg_current_xact_id()</c>). The row's own
    /// <c>xmin</c> moves on every status update, so this is what the load
    /// test's ordering check joins to <c>pg_xact_commit_timestamp</c>: the
    /// lane forwards rows in commit order, not <see cref="CreatedAt"/> order.
    /// </summary>
    public long AcceptedXid { get; set; }
}