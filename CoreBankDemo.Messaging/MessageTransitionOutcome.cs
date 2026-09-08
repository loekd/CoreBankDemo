namespace CoreBankDemo.Messaging;

public enum MessageTransitionOutcome
{
    Applied,
    AlreadyTerminal,

    /// <summary>
    /// The transition was withheld because a concurrent writer moved the row
    /// since the caller last saw it and it is still non-terminal (spec:
    /// instant-rail-timeout-cancel: a cancel is never re-applied after a
    /// conflict -- the row may be <c>Processing</c> under another claim, and
    /// the caller's cached payload was discarded by the reload). Only ever
    /// returned by
    /// <see cref="MessageRepositoryBase{TMessage,TDbContext}.MarkAsCancelledAsync"/>.
    /// </summary>
    Conflicted
}
