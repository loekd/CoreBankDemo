namespace CoreBankDemo.LoadTestSupport;

public static class LoadTestConstants
{
    public const decimal InitialBalance = 10_000_000.00m;
    public const int AccountCount = 10;

    /// <summary>
    /// The system-wide partition count (ADR-010: both APIs refuse to start
    /// with any other value). LoadTestSupport has no processor options of its
    /// own, so the partition-routing check (ADR-026) recomputes with this.
    /// </summary>
    public const int PartitionCount = 4;
}
