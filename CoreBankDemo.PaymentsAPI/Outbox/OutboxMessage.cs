namespace CoreBankDemo.PaymentsAPI.Outbox;

public class OutboxMessage
{
    public Guid Id { get; set; }
    public required string MessageId { get; set; } // Unique message ID for deduplication
    public int PartitionId { get; set; } // Partition assignment for load distribution
    public required string TransactionId { get; set; }
    public required string FromAccount { get; set; }
    public required string ToAccount { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Processing, Completed, Failed
    public string? TraceParent { get; set; }
    public string? TraceState { get; set; }
}