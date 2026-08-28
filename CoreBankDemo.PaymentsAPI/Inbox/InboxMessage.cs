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

    public required string TransactionId { get; set; }
    public required string EventType { get; set; }
    public required string AccountNumber { get; set; }
    public required string Payload { get; set; }
}
