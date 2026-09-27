using System.Diagnostics;
using OpenTelemetry;

namespace CoreBankDemo.ServiceDefaults;

/// <summary>
/// Marks two kinds of span not recorded at start, so the export processor
/// skips them and -- under the parent-based sampler -- nothing is created
/// beneath them. The mechanism is the one the ASP.NET Core instrumentation's
/// request filter uses; a processor rather than a sampler because a sampler
/// cannot see the <see cref="ActivitySource"/>, and because in this SDK a
/// dropped span does not exist at all, while a suppressed scope must exist to
/// be the parent of what it hides.
///
/// <list type="bullet">
/// <item>
/// A span opened through <see cref="TraceSuppression"/>: deliberate chatter
/// such as the instant rail's wait-for-turn loop, whose lock and claim
/// round-trips every 25 ms said nothing but "still waiting".
/// </item>
/// <item>
/// A database or cache span with no parent: the outbox/inbox processors poll
/// every tick with no ambient activity, so each claim query and every lock
/// <c>SET</c>/<c>EVAL</c> would otherwise be a one-span trace of its own --
/// thousands an hour, drowning the payment traces. A dependency call is only
/// interesting as part of the request or message trace it serves.
/// </item>
/// </list>
/// </summary>
public sealed class TraceNoiseFilter : BaseProcessor<Activity>
{
    /// <summary>The instrumentation sources whose parentless spans are noise.</summary>
    internal static readonly string[] DependencySources =
    [
        "Npgsql",
        "OpenTelemetry.Instrumentation.StackExchangeRedis",
    ];

    public override void OnStart(Activity data)
    {
        if (IsSuppressedScope(data) || IsOrphanedDependencySpan(data))
        {
            data.IsAllDataRequested = false;
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }

    private static bool IsSuppressedScope(Activity activity) =>
        activity.GetTagItem(TraceSuppression.Tag) is not null;

    private static bool IsOrphanedDependencySpan(Activity activity) =>
        activity.ParentId is null && Array.IndexOf(DependencySources, activity.Source.Name) >= 0;
}
