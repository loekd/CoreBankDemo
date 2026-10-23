namespace CoreBankDemo.DemoRunner.Application;

/// <summary>
/// Which checked-in errors file the error knob injects from (ADR-031). The rate says how
/// often the proxy answers for CoreBank; the mix says what it answers with.
/// </summary>
public enum ErrorMix
{
    /// <summary>
    /// <c>devproxy-errors.json</c>: the observability mix. Mostly <c>409</c>/<c>423</c>
    /// (retried, invisible to the breaker), a rare <c>500</c> (counted by the breaker), and a <c>423</c> on
    /// the instant rail's cancel so a payment whose attempts ran out sometimes ends
    /// <c>202 Pending</c> and settles through the background rail instead of <c>504</c>.
    /// </summary>
    Mixed,

    /// <summary>
    /// <c>devproxy-errors-retryable.json</c>: <c>409</c>/<c>423</c> on the transfer command
    /// only. Every injected answer is retried, the breaker never counts one, and the cancel is
    /// never touched, so a faulted instant payment ends <c>504 Cancelled</c>, never <c>202</c>
    /// (ADR-030).
    /// </summary>
    Retryable,
}

/// <summary>
/// The Dev Proxy knobs the operator steers, as one value. The same type is staged,
/// applied, stamped onto evidence, and read back from a config file, so a level can never
/// mean one thing on the slider and another on the record.
/// </summary>
/// <param name="ErrorRatePercent">Share of intercepted calls answered with an injected error from the <paramref name="Mix"/>. Zero disables the plugin.</param>
/// <param name="LatencyFloorMs">Lower bound of the injected delay band. Zero (with the ceiling) disables the plugin.</param>
/// <param name="LatencyCeilingMs">Upper bound of the injected delay band.</param>
/// <param name="ThrottleRequestsPerWindow">Requests permitted per rate-limit window. Zero disables the plugin.</param>
/// <param name="Mix">Which checked-in errors file the error knob answers from. Meaningless, and normalized to the default, while the rate is zero.</param>
public sealed record FaultLevels(
    int ErrorRatePercent,
    int LatencyFloorMs,
    int LatencyCeilingMs,
    int ThrottleRequestsPerWindow,
    ErrorMix Mix = ErrorMix.Mixed)
{
    /// <summary>Every knob quiet. What panic-off applies and what arming resets to.</summary>
    public static FaultLevels AllZero { get; } = new(0, 0, 0, 0);

    /// <summary>The rate-limit window the throttling knob's count applies to, matching the checked-in profiles.</summary>
    public const int ThrottleWindowSeconds = 60;

    /// <summary>
    /// How far past the applied ceiling a call may still count as carrying the injected
    /// latency. Bounded on purpose: without an upper bound a real outage — a dependency that
    /// took thirty seconds because it was dying — would be read as proof of injection.
    /// </summary>
    public static TimeSpan ObservationSlack { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the console waits for traffic to carry an applied level before it stops
    /// counting up and says so. An elapsed readout that climbs forever tells the operator
    /// nothing they did not already know after the first few seconds.
    /// </summary>
    public static TimeSpan ObservationWindow { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether one call's duration is evidence that these levels reached real traffic. A zero
    /// floor is never proof — <c>duration &gt;= 0</c> is trivially true of every call ever
    /// made — so latency can only be proven by a band that actually delays something.
    /// </summary>
    public bool IsCarriedByDuration(TimeSpan duration) =>
        InjectsLatency
        && LatencyFloorMs > 0
        && duration >= TimeSpan.FromMilliseconds(LatencyFloorMs)
        && duration <= TimeSpan.FromMilliseconds(LatencyCeilingMs) + ObservationSlack;

    /// <summary>
    /// Discrete steps each knob offers. The ladders exist because Terminal.Gui's
    /// <c>LinearRange</c> is a list of options, not a continuum, and because every value
    /// named by a checked-in profile or a talk flow must be exactly reachable.
    /// </summary>
    public static IReadOnlyList<int> ErrorRateSteps { get; } = [0, 5, 10, 25, 40, 60, 80, 100];

    public static IReadOnlyList<int> LatencySteps { get; } =
        [0, 20, 50, 100, 200, 400, 800, 1200, 2000, 3000, 5000, 8000, 9500, 12000];

    public static IReadOnlyList<int> ThrottleSteps { get; } = [0, 10, 25, 50, 100, 250, 500, 1000, 2000];

    public bool IsAllZero =>
        ErrorRatePercent <= 0
        && LatencyFloorMs <= 0
        && LatencyCeilingMs <= 0
        && ThrottleRequestsPerWindow <= 0;

    public bool InjectsErrors => ErrorRatePercent > 0;

    public bool InjectsLatency => LatencyCeilingMs > 0;

    public bool InjectsThrottling => ThrottleRequestsPerWindow > 0;

    /// <summary>
    /// The levels the checked-in profile ships with. Used as the named preset, and as the
    /// last-resort readback when a config file cannot be parsed — so the sliders fall back
    /// to what the profile really says rather than to an invented zero.
    /// </summary>
    public static FaultLevels CheckedInDefaults(TopologyProfile profile) => profile switch
    {
        // CoreBankDemo.AppHost/devproxy/config/devproxyrc.json + devproxy-errors.json
        TopologyProfile.Regular => new FaultLevels(40, 100, 400, 1000, ErrorMix.Mixed),
        // CoreBankDemo.LoadTests/devproxy/config/devproxyrc-latency.json
        TopologyProfile.LoadTests => new FaultLevels(0, 9500, 12000, 0),
        _ => AllZero,
    };

    /// <summary>
    /// Named starting points offered as preset chips. Per-profile: the LoadTests
    /// instant-rail-overrun band is the tuned default there and is never offered on Regular.
    /// Selecting one only stages it; it always goes through the same Apply path.
    /// </summary>
    public static IReadOnlyList<FaultPreset> PresetsFor(TopologyProfile profile) => profile switch
    {
        TopologyProfile.Regular =>
        [
            new FaultPreset("All off", AllZero),
            // The observability mix (ADR-031): 40 % of transfer commands answered from the
            // mixed errors file (18/19 a retried 409/423, 1/19 a 500) and 40 % of cancels
            // answered 423, under a 100–400 ms band. Measured on a 20-payment instant burst from
            // one debtor, four at once: a few payments per burst end 504 Cancelled or 202 Pending,
            // the rest succeed, many after a visible retry. No injected 400: PaymentsAPI would
            // mark the row Failed while CoreBank recorded nothing, a ghost verdict the dashboards
            // cannot count. The band is deliberately
            // short: a longer one lets the retry walks of the payments ahead starve the ones
            // behind them in the same partition (a cancelled payment heads the partition until
            // CoreBank confirms it; a deferred one until the background rail delivers it), and
            // the burst tips from "a few" to "half". The rare 500 is the only answer the
            // breaker counts; roughly one burst in ten still trips it.
            new FaultPreset("Regular profile", CheckedInDefaults(TopologyProfile.Regular)),
            // The retryable-only mix: every injected answer is a 409/423 on the transfer
            // command, which Polly's transient predicate ignores, so this knob can never open
            // the CoreBank circuit, and the cancel is never faulted, so a failed payment ends
            // 504 Cancelled, never 202 deferred. A payment is cancelled only when all three
            // attempts fail, so 60 % cancels about one in five; 100 % on a single payment
            // walks through every retry (ADR-030). The 200–800 ms band only paces the attempts
            // so the retries read on stage: it stays well under the 2.5 s attempt timeout, so
            // it can never overrun one.
            new FaultPreset("Flaky CoreBank", new FaultLevels(60, 200, 800, 0, ErrorMix.Retryable)),
            // Ten transfers per minute, then 429s that open the circuit (ADR-029). Trip it with a
            // standard burst from another debtor first: a payment whose own 429 trips the
            // breaker has its cancel refused by the open circuit, ends 202, and that Pending row
            // then heads its partition for the whole window, starving every instant payment
            // behind it (ADR-030). With the circuit already open, an instant burst is refused
            // locally in milliseconds; "All off" lets it close and the held rows drain.
            new FaultPreset("Throttled CoreBank", new FaultLevels(0, 0, 0, 10)),
        ],
        TopologyProfile.LoadTests =>
        [
            new FaultPreset("Instant-rail overrun", CheckedInDefaults(TopologyProfile.LoadTests)),
            new FaultPreset("All off", AllZero),
        ],
        _ => [new FaultPreset("All off", AllZero)],
    };

    /// <summary>
    /// The preset these levels match exactly, or <c>null</c> when the operator has moved a
    /// knob away from every preset — the chip row then reads <c>Custom</c> rather than
    /// leaving a preset looking selected while the values no longer match it.
    /// </summary>
    public string? MatchingPresetName(TopologyProfile profile) =>
        PresetsFor(profile).FirstOrDefault(preset => preset.Levels == this)?.Name;

    /// <summary>
    /// Snaps every knob onto its ladder, keeps the latency band ordered, and forgets the mix
    /// once the error knob is off, so a value read back from a hand-edited or foreign config
    /// still lands on a reachable slider position and a quiet config never reads as two
    /// different quiet levels.
    /// </summary>
    public FaultLevels Normalized()
    {
        var floor = Snap(LatencyFloorMs, LatencySteps);
        var ceiling = Snap(LatencyCeilingMs, LatencySteps);
        // A band read back from a hand-edited config can arrive inverted; ordering it here
        // means a zero ceiling can only ever mean "both ends at zero", so the knob is off.
        if (ceiling < floor)
        {
            (floor, ceiling) = (ceiling, floor);
        }

        var rate = Snap(ErrorRatePercent, ErrorRateSteps);
        return new FaultLevels(
            rate,
            floor,
            ceiling,
            Snap(ThrottleRequestsPerWindow, ThrottleSteps),
            rate > 0 ? Mix : AllZero.Mix);
    }

    /// <summary>Renders one knob for reading aloud — the authoritative channel, never the bar.</summary>
    public string ErrorRateText => ErrorRatePercent <= 0 ? "0%" : $"{ErrorRatePercent}% {MixText}";

    /// <summary>The mix as one word, so the error knob reads "40% mixed" or "60% retryable" aloud.</summary>
    public string MixText => Mix switch
    {
        ErrorMix.Retryable => "retryable",
        _ => "mixed",
    };

    public string LatencyText => LatencyCeilingMs <= 0 ? "off" : $"{LatencyFloorMs}–{LatencyCeilingMs} ms";

    public string ThrottleText => ThrottleRequestsPerWindow <= 0
        ? "off"
        : $"{ThrottleRequestsPerWindow}/{ThrottleWindowSeconds}s";

    public override string ToString() =>
        $"error rate {ErrorRateText} · latency {LatencyText} · throttling {ThrottleText}";

    private static int Snap(int value, IReadOnlyList<int> steps)
    {
        if (value <= steps[0])
        {
            return steps[0];
        }

        var nearest = steps[0];
        var distance = int.MaxValue;
        foreach (var step in steps)
        {
            var candidate = Math.Abs(step - value);
            if (candidate < distance)
            {
                distance = candidate;
                nearest = step;
            }
        }

        return nearest;
    }
}

public sealed record FaultPreset(string Name, FaultLevels Levels);
