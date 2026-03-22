namespace CoreBankDemo.Messaging.Inbox;

/// <summary>
/// Core interface for inbox pattern messages with idempotency and partition support.
/// </summary>
public interface IInboxMessage : IMessage
{
    string IdempotencyKey { get; set; }
    DateTime ReceivedAt { get; set; }
}
