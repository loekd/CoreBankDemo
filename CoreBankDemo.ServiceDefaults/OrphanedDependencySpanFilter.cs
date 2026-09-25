using System.Diagnostics;
using OpenTelemetry;

namespace CoreBankDemo.ServiceDefaults;

/// <summary>
/// Drops database and cache spans that do not belong to a recorded trace.
///
/// <para>
/// The outbox/inbox processors poll every tick with no ambient activity, so
/// each claim query and every lock <c>SET</c>/<c>EVAL</c> would otherwise be
/// exported as a one-span trace -- thousands an hour, drowning the payment
/// traces. The <c>/health</c> request is the other source: the ASP.NET Core
/// filter clears <see cref="Activity.Recorded"/> on the request span, but its
/// DB ping and Redis <c>PING</c> children would still be sampled, because
/// Aspire configures the <c>always_on</c> sampler (<c>OTEL_TRACES_SAMPLER</c>),
/// which never consults the parent. A dependency call is only interesting as
/// part of the request or message trace it serves, so one whose parent is
/// missing or not sampled is marked not recorded at start: the export
/// processor skips it. The same span under a recorded parent is untouched.
/// </para>
///
/// <para>
/// A processor rather than a sampler because <c>SamplingParameters</c> does
/// not carry the <see cref="ActivitySource"/>; the source is what tells a
/// Npgsql span from one of ours.
/// </para>
/// </summary>
public sealed class OrphanedDependencySpanFilter : BaseProcessor<Activity>
{
    /// <summary>The instrumentation sources whose orphaned spans are noise.</summary>
    internal static readonly string[] DependencySources =
    [
        "Npgsql",
        "OpenTelemetry.Instrumentation.StackExchangeRedis",
    ];

    public override void OnStart(Activity data)
    {
        if (Array.IndexOf(DependencySources, data.Source.Name) < 0 || HasSampledParent(data))
        {
            return;
        }

        data.IsAllDataRequested = false;
        data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
    }

    /// <summary>
    /// An in-process parent answers for itself. Without one, the parent's
    /// trace flags are read from the W3C <see cref="Activity.ParentId"/>: that
    /// covers a remote parent from a <c>traceparent</c> header and a span the
    /// instrumentation started from a captured <see cref="ActivityContext"/>
    /// (the Redis instrumentation replays profiler sessions that way). A
    /// parent id that is not W3C-formatted is trusted rather than dropped.
    /// </summary>
    private static bool HasSampledParent(Activity activity)
    {
        if (activity.Parent is { } parent)
        {
            return parent.Recorded;
        }

        if (activity.ParentId is null)
        {
            return false;
        }

        return !ActivityContext.TryParse(activity.ParentId, activity.TraceStateString, out var parentContext)
            || (parentContext.TraceFlags & ActivityTraceFlags.Recorded) != 0;
    }
}
