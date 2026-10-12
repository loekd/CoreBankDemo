namespace CoreBankDemo.CoreBankAPI;

/// <summary>
/// How <see cref="Inbox.TransactionExecutor"/> serializes concurrent transfers:
/// <see cref="Row"/> locks each account row (<c>FOR UPDATE</c>);
/// <see cref="Partition"/> takes one transaction-scoped advisory lock per
/// partition touched.
/// </summary>
internal enum LockStrategy
{
    Row,
    Partition
}

/// <summary>
/// Demo switches bound to the <c>Demo</c> configuration section
/// (<c>Demo:LockStrategy</c> = <c>row</c> | <c>partition</c>, default <c>row</c>).
/// </summary>
internal sealed record DemoOptions
{
    public const string SectionName = "Demo";

    public LockStrategy LockStrategy { get; init; } = LockStrategy.Row;
}
