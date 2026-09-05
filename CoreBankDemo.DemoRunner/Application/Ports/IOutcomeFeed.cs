using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Application.Ports;

/// <summary>
/// The three CloudEvent types the console listens for on <c>transaction-events</c>. Declared
/// here as plain constants rather than taken from
/// <c>CoreBankDemo.ServiceDefaults.CloudEventTypes.Constants</c>: ADR-015's project-graph
/// invariant forbids a reference from this console to any banking project, and the wire
/// contract is frozen (spec-5-5), so copying it is the boundary working as intended.
/// </summary>
public static class OutcomeEventTypes
{
    public const string TransactionCompleted = "com.corebank.transaction.completed";
    public const string TransactionFailed = "com.corebank.transaction.failed";
    public const string BalanceUpdated = "com.corebank.account.balance.updated";

    public const string Topic = "transaction-events";
    public const string PubSubComponent = "pubsub";
}

/// <summary>Local wire record for <c>com.corebank.transaction.completed</c>.</summary>
public sealed record TransactionCompletedWireEvent(
    string TransactionId,
    string? Status,
    DateTimeOffset ProcessedAt);

/// <summary>Local wire record for <c>com.corebank.transaction.failed</c>.</summary>
public sealed record TransactionFailedWireEvent(
    string TransactionId,
    string? Status,
    DateTimeOffset ProcessedAt,
    string? ErrorReason);

/// <summary>Local wire record for <c>com.corebank.account.balance.updated</c>.</summary>
public sealed record BalanceUpdatedWireEvent(
    string TransactionId,
    string AccountNumber,
    decimal Delta,
    decimal NewBalance,
    string Currency);

/// <summary>
/// One event as it came off the topic. Exactly one of the three payload properties is set;
/// <see cref="EventType"/> always carries the CloudEvent type verbatim, because the console
/// prints it rather than flattening the three into one generic "processed" signal.
/// </summary>
/// <remarks>
/// Deliberately carries no observed-at stamp: the console's own clock belongs to the
/// controller's <see cref="TimeProvider"/>, so that the two figures a row prints — the event's
/// <c>ProcessedAt</c> and the console's observed-at — can never come from the same source.
/// </remarks>
public sealed record OutcomeEvent(
    string EventType,
    string TransactionId,
    TransactionCompletedWireEvent? Completed = null,
    TransactionFailedWireEvent? Failed = null,
    BalanceUpdatedWireEvent? BalanceUpdated = null)
{
    /// <summary>The event's own clock, when it carries one. Balance events do not.</summary>
    public DateTimeOffset? ProcessedAt => Completed?.ProcessedAt ?? Failed?.ProcessedAt;

    public bool IsTerminal => Completed is not null || Failed is not null;

    public static OutcomeEvent From(TransactionCompletedWireEvent completed) =>
        new(OutcomeEventTypes.TransactionCompleted, completed.TransactionId, Completed: completed);

    public static OutcomeEvent From(TransactionFailedWireEvent failed) =>
        new(OutcomeEventTypes.TransactionFailed, failed.TransactionId, Failed: failed);

    public static OutcomeEvent From(BalanceUpdatedWireEvent balance) =>
        new(OutcomeEventTypes.BalanceUpdated, balance.TransactionId, BalanceUpdated: balance);
}

public enum OutcomeFeedState
{
    /// <summary>No subscription has been attempted for the current topology.</summary>
    NotStarted,

    /// <summary>A subscription is live. Only in this state may a row read "Awaiting settlement".</summary>
    Listening,

    /// <summary>A live subscription dropped. Every unresolved row withdraws its claim.</summary>
    Lost,

    /// <summary>No subscription could be established — a missing binary, a refused sidecar.</summary>
    Unavailable,
}

/// <summary>
/// What the console can honestly say about its own ability to hear the broadcast. Never a
/// chip: it is stated on the rows that depend on it and in the feed header
/// (<c>EXPERIENCE.md</c>, Feed status (inline)).
/// </summary>
/// <param name="ListeningSince">
/// When the current subscription was established. The feed never replays history, so an empty
/// feed is only meaningful with this attached.
/// </param>
/// <param name="LostAt">When the subscription dropped, for the row that withdraws its claim.</param>
/// <param name="GapStart">Start of the window this console did not observe, after a reconnect.</param>
/// <param name="GapEnd">End of that window. Never back-filled — only stamped.</param>
/// <param name="Detail">The reason, verbatim, for an unavailable or lost feed.</param>
public sealed record OutcomeFeedStatus(
    OutcomeFeedState State,
    DateTimeOffset? ListeningSince = null,
    DateTimeOffset? LostAt = null,
    DateTimeOffset? GapStart = null,
    DateTimeOffset? GapEnd = null,
    string Detail = "")
{
    public static readonly OutcomeFeedStatus NotStarted = new(OutcomeFeedState.NotStarted);

    public bool IsListening => State == OutcomeFeedState.Listening;
}

/// <summary>
/// The console's own read-only listener on <c>transaction-events</c>.
/// <para>
/// The adapter owns a <c>daprd</c> sidecar of its own and dials out to it over gRPC, so the
/// console hosts no inbound listener and opens no port. Its own app-id gives it its own
/// consumer group: PaymentsAPI's <c>payments-api</c>-scoped subscription keeps receiving every
/// event, and this copy is a fan-out, never a diversion.
/// </para>
/// </summary>
public interface IOutcomeFeed
{
    /// <summary>
    /// Raised for every event received. Raised off the UI thread, so a subscriber must
    /// marshal before touching Terminal.Gui.
    /// </summary>
    event Action<OutcomeEvent>? EventReceived;

    /// <summary>
    /// Raised whenever the feed's own state changes — established, dropped, resumed. A fault
    /// is surfaced here rather than thrown into the UI, because a feed that cannot listen is
    /// a state the console must render, not an error that aborts an operator's action.
    /// </summary>
    event Action<OutcomeFeedStatus>? StatusChanged;

    // Deliberately no Status property. The console's single source of truth for what it can
    // hear is OperatorConsoleState.Feed, kept current by StatusChanged; a second readable copy
    // on the port could silently disagree with the one the screen is drawn from.

    /// <summary>
    /// Spawns the sidecar for <paramref name="profile"/> (its components directory decides
    /// which Redis it reaches) and subscribes. Never throws for an unreachable feed: an
    /// unavailable status with the reason is the answer, so a topology still starts.
    /// </summary>
    Task<OutcomeFeedStatus> StartAsync(TopologyProfile profile, CancellationToken ct);

    /// <summary>
    /// Tears the subscription and the sidecar down. Safe to call when nothing is running.
    /// </summary>
    Task StopAsync(CancellationToken ct);
}
