using System.Diagnostics;

namespace CoreBankDemo.ServiceDefaults;

/// <summary>
/// Opens a span that <see cref="TraceNoiseFilter"/> marks not recorded at
/// start -- the same thing the ASP.NET Core instrumentation's request filter
/// does to a <c>/health</c> span. Under the parent-based sampler nothing is
/// then created beneath it: not the direct children, and not the spans an
/// instrumentation replays later from the scope's captured context (the
/// Redis instrumentation does that). The scope is still a real activity and
/// becomes <see cref="Activity.Current"/>, so the code inside needs no
/// knowledge of tracing. Work that must stay visible is started with an
/// explicit parent context captured before the scope opened.
/// </summary>
public static class TraceSuppression
{
    /// <summary>Creation tag the filter recognises; the value carries no meaning.</summary>
    public const string Tag = "trace.suppress";

    /// <summary>The creation tags a suppressed span is started with.</summary>
    public static readonly KeyValuePair<string, object?>[] Tags = [new(Tag, true)];

    /// <summary>
    /// Starts a suppressed scope named <paramref name="name"/> under the current
    /// activity. <see langword="null"/> when no listener is attached to
    /// <paramref name="source"/>, in which case nothing is traced anyway.
    /// </summary>
    public static Activity? Begin(ActivitySource source, string name) =>
        source.StartActivity(name, ActivityKind.Internal, parentContext: default, tags: Tags);
}
