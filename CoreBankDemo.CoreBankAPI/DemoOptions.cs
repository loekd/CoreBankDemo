namespace CoreBankDemo.CoreBankAPI;

/// <summary>
/// How <see cref="Inbox.TransactionExecutor"/> serializes concurrent transfers:
/// <see cref="Row"/> locks each account row (<c>FOR UPDATE</c>);
/// <see cref="Partition"/> takes one transaction-scoped advisory lock per
/// partition touched. Granularity only; how long a lock is kept is
/// <see cref="LockRelease"/>.
/// </summary>
internal enum LockStrategy
{
    Row,
    Partition
}

/// <summary>
/// When a transfer gives back what it serialized on: <see cref="Commit"/> is the
/// normal end of the database transaction; <see cref="AfterPublish"/> is the planted
/// flaw, where <see cref="Inbox.TransactionExecutor"/> also takes session-scoped
/// advisory locks on the same keys (account numbers under <see cref="LockStrategy.Row"/>,
/// partition ids under <see cref="LockStrategy.Partition"/>) and
/// <see cref="Inbox.TransactionExecutionHandler"/> keeps them past its commit until the
/// transfer's outcome events have left the messaging outbox: a ledger lock held across
/// asynchronous publishing.
/// </summary>
internal enum LockRelease
{
    Commit,
    AfterPublish
}

/// <summary>
/// Demo switches bound to the <c>Demo</c> configuration section:
/// <c>Demo:LockStrategy</c> = <c>row</c> | <c>partition</c> (default <c>row</c>),
/// <c>Demo:LockRelease</c> = <c>commit</c> | <c>afterPublish</c> (default <c>commit</c>),
/// <c>Demo:PublishWaitMilliseconds</c> (default 5000). The two switches are independent.
/// </summary>
internal sealed record DemoOptions
{
    public const string SectionName = "Demo";

    public LockStrategy LockStrategy { get; init; } = LockStrategy.Row;

    public LockRelease LockRelease { get; init; } = LockRelease.Commit;

    /// <summary>
    /// Upper bound on how long <see cref="LockRelease.AfterPublish"/> keeps its locks after
    /// commit while waiting for the outcome events to be published; the hold is released
    /// when it elapses, whatever the outbox state.
    /// </summary>
    public int PublishWaitMilliseconds { get; init; } = 5000;
}
