namespace CoreBankDemo.DemoRunner.Application;

/// <summary>
/// The three Dev Proxy knobs the operator steers, as one value. The same type is staged,
/// applied, stamped onto evidence, and read back from a config file, so a level can never
/// mean one thing on the slider and another on the record.
/// </summary>
/// <param name="ErrorRatePercent">Share of intercepted calls answered with an injected 503/429/500. Zero disables the plugin.</param>
/// <param name="LatencyFloorMs">Lower bound of the injected delay band. Zero (with the ceiling) disables the plugin.</param>
/// <param name="LatencyCeilingMs">Upper bound of the injected delay band.</param>
/// <param name="ThrottleRequestsPerWindow">Requests permitted per rate-limit window. Zero disables the plugin.</param>
public sealed record FaultLevels(
    int ErrorRatePercent,
    int LatencyFloorMs,
    int LatencyCeilingMs,
    int ThrottleRequestsPerWindow)
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
        // CoreBankDemo.AppHost/devproxy/config/devproxyrc.json
        TopologyProfile.Regular => new FaultLevels(5, 20, 200, 1000),
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
            new FaultPreset("Regular profile", CheckedInDefaults(TopologyProfile.Regular)),
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
    /// Snaps every knob onto its ladder and keeps the latency band ordered, so a value read
    /// back from a hand-edited or foreign config still lands on a reachable slider position.
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

        return new FaultLevels(
            Snap(ErrorRatePercent, ErrorRateSteps),
            floor,
            ceiling,
            Snap(ThrottleRequestsPerWindow, ThrottleSteps));
    }

    /// <summary>Renders one knob for reading aloud — the authoritative channel, never the bar.</summary>
    public string ErrorRateText => $"{ErrorRatePercent}%";

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
