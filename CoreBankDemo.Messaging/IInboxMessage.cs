namespace CoreBankDemo.Messaging;

/// <summary>
/// Contract for inbox pattern messages.
/// </summary>
public interface IInboxMessage : IMessage
{
    /// <summary>
    /// Ordering identity (AD-4): drives partition assignment. Also the baseline
    /// dedupe identity; dedupe is per store — command stores dedupe on this key
    /// alone, event stores on a composite event identity enforced via the
    /// repository's unique-index hooks (story 2.2).
    /// </summary>
    string IdempotencyKey { get; set; }

    /// <summary>When the message was received (UTC); ordering timestamp for claims.</summary>
    DateTime ReceivedAt { get; set; }
}
