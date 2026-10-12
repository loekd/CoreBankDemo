namespace CoreBankDemo.LoadTestSupport;

public static class LoadTestConstants
{
    public const decimal InitialBalance = 10_000_000.00m;
    // 32 spreads the NL{i:D2}LOAD{i:D10} accounts over all four partitions
    // (11/13/4/4); 10 only ever reached three. Keep k6/script.js and
    // ResetResponseValidator.ExpectedAccountCount in sync.
    public const int AccountCount = 32;

    /// <summary>
    /// The system-wide partition count (ADR-010: both APIs refuse to start
    /// with any other value). LoadTestSupport has no processor options of its
    /// own, so the partition-routing check (ADR-026) recomputes with this.
    /// </summary>
    public const int PartitionCount = 4;
}
