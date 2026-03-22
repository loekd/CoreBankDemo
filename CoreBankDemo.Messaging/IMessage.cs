namespace CoreBankDemo.Messaging;

/// <summary>
/// Common base for outbox and inbox pattern messages.
/// </summary>
public interface IMessage
{
    Guid Id { get; set; }
    int PartitionId { get; set; }
    string Status { get; set; } // Pending, Processing, Completed, Failed
    DateTime? ProcessedAt { get; set; }
    int RetryCount { get; set; }
    string? LastError { get; set; }
    string? TraceParent { get; set; }
    string? TraceState { get; set; }
}

