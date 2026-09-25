using System.Diagnostics;
using AwesomeAssertions;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests;

/// <summary>
/// The outbox/inbox processors poll every tick with no ambient activity, so
/// each claim query and lock SET/EVAL would become a one-span trace of its
/// own. The filter drops those orphans at the source; the same spans inside
/// a request or message trace are kept.
/// </summary>
public class OrphanedDependencySpanFilterTests
{
    private static readonly ActivitySource Npgsql = new("Npgsql");
    private static readonly ActivitySource Redis = new("OpenTelemetry.Instrumentation.StackExchangeRedis");
    private static readonly ActivitySource Own = new(nameof(OrphanedDependencySpanFilterTests));

    [Fact]
    public void A_poll_tick_exports_nothing()
    {
        var exported = Run(() =>
        {
            // Lock, claim query, unlock: each starts with no parent.
            using (Redis.StartActivity("SET")) { }
            using (Npgsql.StartActivity("postgresql")) { }
            using (Redis.StartActivity("EVAL")) { }
        });

        exported.Should().BeEmpty();
    }

    [Fact]
    public void A_child_of_a_dropped_dependency_root_is_dropped_with_it()
    {
        var exported = Run(() =>
        {
            using (Npgsql.StartActivity("CONNECT corebankdb"))
            {
                using (Npgsql.StartActivity("postgresql")) { }
            }
        });

        exported.Should().BeEmpty();
    }

    [Fact]
    public void Dependency_spans_under_an_unrecorded_parent_are_dropped()
    {
        // The /health request: the ASP.NET Core filter clears Recorded on the
        // request span, but its DB ping and Redis PING children would still
        // be sampled under Aspire's always_on sampler.
        var exported = Run(() =>
        {
            using (var health = Own.StartActivity("GET /health"))
            {
                health!.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
                using (Npgsql.StartActivity("postgresql")) { }
                using (Redis.StartActivity("PING")) { }
            }
        });

        exported.Should().BeEmpty();
    }

    [Fact]
    public void A_dependency_span_started_from_an_unrecorded_parents_context_is_dropped()
    {
        // The Redis instrumentation does not start its spans under
        // Activity.Current: it replays the profiler session and passes the
        // captured request activity's *context* as the parent. The child then
        // has no in-process Parent, only a W3C ParentId carrying the parent's
        // trace flags -- 00 once the ASP.NET Core filter cleared Recorded.
        var exported = Run(() =>
        {
            using var health = Own.StartActivity("GET /health");
            health!.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            using (Redis.StartActivity("PING", ActivityKind.Client, health.Context)) { }
        });

        exported.Should().BeEmpty();
    }

    [Fact]
    public void Dependency_spans_under_a_real_root_are_exported_with_it()
    {
        var exported = Run(() =>
        {
            using (Own.StartActivity("POST api/Payments"))
            {
                using (Npgsql.StartActivity("postgresql")) { }
                using (Redis.StartActivity("SET")) { }
            }
        });

        exported.Select(a => a.DisplayName).Should().Equal("postgresql", "SET", "POST api/Payments");
    }

    [Fact]
    public void A_dependency_span_under_a_remote_parent_is_exported()
    {
        // ParentId set from a traceparent header, no in-process Parent:
        // still part of a trace, never an orphan.
        var exported = Run(() =>
        {
            using (Npgsql.StartActivity(
                "postgresql",
                ActivityKind.Client,
                new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded)))
            {
            }
        });

        exported.Select(a => a.DisplayName).Should().Equal("postgresql");
    }

    [Fact]
    public void A_root_span_from_any_other_source_is_exported()
    {
        var exported = Run(() =>
        {
            using (Own.StartActivity("ProcessOutboxMessage")) { }
        });

        exported.Select(a => a.DisplayName).Should().Equal("ProcessOutboxMessage");
    }

    /// <summary>
    /// Runs <paramref name="work"/> under a provider wired the way
    /// <c>AddServiceDefaults</c> wires it: the filter ahead of an export
    /// processor that honours <see cref="Activity.Recorded"/>, and the
    /// <c>always_on</c> sampler Aspire sets through <c>OTEL_TRACES_SAMPLER</c>
    /// -- which samples a child regardless of its parent, so the filter
    /// cannot lean on a parent-based decision.
    /// </summary>
    private static List<Activity> Run(Action work)
    {
        var exported = new List<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(Npgsql.Name, Redis.Name, Own.Name)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new OrphanedDependencySpanFilter())
            .AddProcessor(new SimpleActivityExportProcessor(new CollectingExporter(exported)))
            .Build();

        work();
        return exported;
    }

    private sealed class CollectingExporter(List<Activity> sink) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                sink.Add(activity);
            }

            return ExportResult.Success;
        }
    }
}
