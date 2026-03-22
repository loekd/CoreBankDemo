using CoreBankDemo.Messaging.Inbox;

namespace CoreBankDemo.PaymentsAPI.Inbox;

public class InboxMessage : IInboxMessage
{
    // IInboxMessage properties
    public Guid Id { get; set; }
    public required string IdempotencyKey { get; set; }
    public int PartitionId { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Processing, Completed, Failed
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string? TraceParent { get; set; }
    public string? TraceState { get; set; }

    // Domain-specific properties
    public required string EventType { get; set; }
    public required string EventPayload { get; set; }
}

