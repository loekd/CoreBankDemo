using CoreBankDemo.Messaging;

namespace CoreBankDemo.PaymentsAPI.Inbox;

public class InboxMessage : IInboxMessage
{
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

    /// <summary>
    /// The PostgreSQL transaction id of the accepting transaction, assigned by
    /// the database on insert (<c>pg_current_xact_id()</c>). The row's own
    /// <c>xmin</c> moves on every status update, so this is what the load
    /// test's ordering check joins to <c>pg_xact_commit_timestamp</c>: the
    /// lane processes rows in commit order, not <see cref="ReceivedAt"/> order.
    /// </summary>
    public long AcceptedXid { get; set; }

    public required string TransactionId { get; set; }
    public required string EventType { get; set; }
    public required string AccountNumber { get; set; }
    public required string Payload { get; set; }

    /// <inheritdoc/>
    public int Priority { get; set; } = MessageConstants.Priority.Standard;

    /// <inheritdoc/>
    public DateTime? HoldUntil { get; set; }
}
