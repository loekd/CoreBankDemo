namespace CoreBankDemo.ServiceDefaults.CloudEventTypes;

public static class Constants
{
    public const string TransactionCompleted = "com.corebank.transaction.completed";
    public const string TransactionFailed = "com.corebank.transaction.failed";
    public const string BalanceUpdated = "com.corebank.account.balance.updated";

    /// <summary>
    /// Published by CoreBankAPI for every cancellation it commits -- a tombstone
    /// stored before the original arrived, or a still-pending command cancelled
    /// before execution -- enqueued atomically with the cancel (spec:
    /// instant-rail-cancelled-event). Never published for a cancellation that
    /// never left PaymentsAPI, and never for a replayed one.
    /// </summary>
    public const string TransactionCancelled = "com.corebank.transaction.cancelled";
}
