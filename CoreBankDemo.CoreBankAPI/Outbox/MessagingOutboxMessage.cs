namespace CoreBankDemo.CoreBankAPI.Outbox;

public class MessagingOutboxMessage
{
    public Guid Id { get; set; }
    public int PartitionId { get; set; }
    public required string TransactionId { get; set; }
    public required string Status { get; set; } // Pending, Processing, Completed, Failed
    public required string EventType { get; set; }
    public required string EventSource { get; set; }
    public required string EventData { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
}
