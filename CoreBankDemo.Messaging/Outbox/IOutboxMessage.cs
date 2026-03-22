namespace CoreBankDemo.Messaging.Outbox;

/// <summary>
/// Core interface for outbox pattern messages.
/// </summary>
public interface IOutboxMessage : IMessage
{
    string IdempotencyKey { get; set; }
    DateTime CreatedAt { get; set; }
}
