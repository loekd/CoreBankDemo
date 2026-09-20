namespace CoreBankDemo.DemoRunner.Application;

/// <summary>
/// The one place an Evidence list row's title is written.
/// <para>
/// A title is what a room reads off the list: a few words naming what happened, and nothing
/// that explains it. The explanation is <see cref="EvidenceRecord.Summary"/>, which the Details
/// pane, the status line and the export keep whole. Display-only: no title is ever parsed,
/// exported or compared against.
/// </para>
/// </summary>
public static class EvidenceTitles
{
    // --- Transactions: every one of these rows also names the creditor account ---------------
    public const string TransactionPending = "Transaction pending";
    public const string InstantDeferred = "Instant deferred";
    public const string TransactionCompleted = "Transaction completed";
    public const string TransactionFailed = "Transaction failed";
    public const string TransactionCancelled = "Transaction cancelled";
    public const string TransactionOutcomeUnknown = "Outcome unknown";
    public const string TransactionSettled = "Transaction settled";
    public const string TransactionRejected = "Transaction rejected";
    public const string PaymentError = "Payment error";
    public const string CancelTooLate = "Cancel too late";
    public const string CancelRefused = "Cancel refused";
    public const string CancelFailed = "Cancel failed";

    // --- Inbound events that belong to no one transaction row -------------------------------
    public const string BalanceUpdated = "Balance updated";
    public const string EventWithoutTransactionId = "Event without transaction id";
    public const string EarlyOutcomeMatched = "Early outcome matched";

    // --- The outcome feed itself ------------------------------------------------------------
    public const string FeedListening = "Feed listening";
    public const string FeedListeningAgain = "Feed listening again";
    public const string FeedLost = "Feed lost";
    public const string FeedUnavailable = "Feed unavailable";
    public const string FeedReconnectGaveUp = "Feed reconnect gave up";
    public const string SidecarStopFailed = "Sidecar stop failed";

    // --- Aggregates, queries and exports ----------------------------------------------------
    public const string LoadTestPassed = "Load test passed";
    public const string LoadTestFailed = "Load test failed";
    public const string OutcomeQueried = "Outcome queried";
    public const string OutcomeQueryFailed = "Outcome query failed";
    public const string EvidenceExported = "Evidence exported";
    public const string EvidenceExportFailed = "Evidence export failed";

    // --- Faults -----------------------------------------------------------------------------
    public const string FaultsApplied = "Faults applied";
    public const string FaultsOff = "Faults off";
    public const string FaultsNotApplied = "Faults not applied";
    public const string FaultsDiscarded = "Faults discarded";
    public const string FaultsWrittenNotInForce = "Faults written, not in force";
    public const string FaultConfigResetFailed = "Fault config reset failed";
    public const string FaultConfigDeleteFailed = "Fault config delete failed";
    public const string FaultLevelsUnreadable = "Fault levels unreadable";

    // --- Topology ---------------------------------------------------------------------------
    public const string AppHostDisappeared = "AppHost disappeared";
    public const string StaleOwnershipNotCleared = "Stale ownership not cleared";

    /// <summary>
    /// For a record whose summary is already a title — <c>Stopped Regular</c>,
    /// <c>Restart corebank-api confirmed</c>. The row falls back to the summary, so the words
    /// are written once.
    /// </summary>
    public const string? SameAsSummary = null;

    public static string Started(TopologyProfile profile) => $"Started {profile}";

    /// <summary>
    /// <c>Transaction completed</c> resent under the same key reads <c>Resend completed</c>, and
    /// <c>Instant deferred</c> reads <c>Resend deferred</c>.
    /// </summary>
    public static string Resend(string title) => title
        .Replace("Transaction", "Resend", StringComparison.Ordinal)
        .Replace("Instant", "Resend", StringComparison.Ordinal);

    public static string UnknownEvent(string eventType) =>
        eventType is { Length: > 0 } ? $"Unknown event {eventType}" : "Unknown event";

    public static string BurstFinished(int sent, int count) => $"Burst finished {sent}/{count}";

    public static string BurstCancelled(int sent, int count) => $"Burst cancelled {sent}/{count}";

    public static string Refused(string action) => $"{action} refused";
}
