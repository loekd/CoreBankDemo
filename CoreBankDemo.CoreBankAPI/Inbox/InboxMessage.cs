namespace CoreBankDemo.CoreBankAPI.Inbox;

public class InboxMessage
{
    public Guid Id { get; set; }
    public required string IdempotencyKey { get; set; }
    public int PartitionId { get; set; } // Partition assignment for load distribution
    public required string FromAccount { get; set; }
    public required string ToAccount { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public required string TransactionId { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Processing, Completed, Failed
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string? ResponsePayload { get; set; }
}