namespace CoreBankDemo.CoreBankAPI.Outbox;

public class MessagingOutboxMessage
{
    public Guid Id { get; set; }
    public int PartitionId { get; set; }
    public required string TransactionId { get; set; }
    public required string Status { get; set; } // Pending, Processing, Completed, Failed
    public required string EventType { get; set; }
    public required string EventSource { get; set; }
    
    // Transaction details for CloudEvent creation
    public required string FromAccount { get; set; }
    public required string ToAccount { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public required string TransactionStatus { get; set; } // Completed, Failed
    public string? ErrorReason { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string? TraceParent { get; set; }
    public string? TraceState { get; set; }
}
