using CoreBankDemo.Messaging;

namespace CoreBankDemo.CoreBankAPI.Outbox;

/// <summary>
/// Outbound domain-event message (kernel outbox pattern, AD-4). <see cref="AccountNumber"/>
/// is the account this particular row's event concerns (from-account or
/// to-account, depending on which of the two <c>BalanceUpdated</c> events the
/// row represents) — renamed from the legacy misnomer <c>FromAccount</c>
/// (epic-4-context.md's Technical Decisions). <see cref="IdempotencyKey"/>
/// always equals <see cref="TransactionId"/>; the composite dedupe identity is
/// <c>(TransactionId, EventType, AccountNumber)</c>, enforced by a unique
/// index in <see cref="CoreBankDbContext"/>.
/// </summary>
public class MessagingOutboxMessage : IOutboxMessage
{
    // IOutboxMessage / IMessage properties
    public Guid Id { get; set; }
    public int PartitionId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public required DateTime EventOccurredAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string? TraceParent { get; set; }
    public string? TraceState { get; set; }

    // Domain-specific properties
    public required string TransactionId { get; set; }
    public required string EventType { get; set; }
    public required string EventSource { get; set; }
    public required string AccountNumber { get; set; }
    public required string ToAccount { get; set; }
    public decimal Amount { get; set; }
    public decimal? NewBalance { get; set; }
    public required string Currency { get; set; }
    public required string TransactionStatus { get; set; }
    public string? ErrorReason { get; set; }
}
