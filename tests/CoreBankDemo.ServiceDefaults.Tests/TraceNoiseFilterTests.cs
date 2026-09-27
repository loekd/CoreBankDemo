using System.Diagnostics;
using AwesomeAssertions;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests;

/// <summary>
/// Runs the tracing pipeline the way <c>AddServiceDefaults</c> wires it --
/// parent-based sampler, <see cref="TraceNoiseFilter"/>, then an export
/// processor that honours <see cref="Activity.Recorded"/> -- and checks what
/// comes out for poll ticks, health probes, suppressed scopes, and real work.
/// </summary>
public class TraceNoiseFilterTests
{
    private static readonly ActivitySource Npgsql = new("Npgsql");
    private static readonly ActivitySource Redis = new("OpenTelemetry.Instrumentation.StackExchangeRedis");
    private static readonly ActivitySource Own = new(nameof(TraceNoiseFilterTests));

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
    public void Dependency_spans_under_an_unrecorded_request_are_dropped()
    {
        // The /health request: the ASP.NET Core filter clears Recorded on the
        // request span; the parent-based sampler then drops its DB ping and
        // Redis PING children -- the one the instrumentation starts from the
        // captured context included.
        var exported = Run(() =>
        {
            using var health = Own.StartActivity("GET /health");
            health!.IsAllDataRequested = false;
            health.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            using (Npgsql.StartActivity("postgresql")) { }
            using (Redis.StartActivity("PING", ActivityKind.Client, health.Context)) { }
        });

        exported.Should().BeEmpty();
    }

    [Fact]
    public void A_suppressed_scope_exists_as_the_current_activity_but_is_not_recorded()
    {
        Run(() =>
        {
            using var request = Own.StartActivity("POST api/Payments");
            using var scope = TraceSuppression.Begin(Own, "WaitForDispatchTurn");

            scope.Should().NotBeNull("the scope must exist to be the parent of what it hides");
            Activity.Current.Should().BeSameAs(scope);
            scope!.Recorded.Should().BeFalse();
            scope.IsAllDataRequested.Should().BeFalse();
        });
    }

    [Fact]
    public void Nothing_under_a_suppressed_scope_is_exported_and_the_request_is_current_again_after_it()
    {
        var exported = Run(() =>
        {
            using var request = Own.StartActivity("POST api/Payments");
            using (var scope = TraceSuppression.Begin(Own, "WaitForDispatchTurn"))
            {
                using (Redis.StartActivity("SET")) { }
                using (Npgsql.StartActivity("postgresql")) { }
                using (Redis.StartActivity("EVAL", ActivityKind.Client, scope!.Context)) { }
                using (Own.StartActivity("SomethingOfOurs")) { }
            }

            Activity.Current.Should().BeSameAs(request);
            using (Npgsql.StartActivity("postgresql")) { }
        });

        exported.Select(a => a.DisplayName).Should().Equal("postgresql", "POST api/Payments");
    }

    [Fact]
    public void A_span_started_inside_a_suppressed_scope_with_the_request_as_explicit_parent_is_recorded_under_the_request()
    {
        var exported = Run(() =>
        {
            using var request = Own.StartActivity("POST api/Payments");
            using (TraceSuppression.Begin(Own, "WaitForDispatchTurn"))
            {
                using (Own.StartActivity("ProcessOutboxMessage", ActivityKind.Producer, request!.Context))
                {
                    using (Npgsql.StartActivity("postgresql")) { }
                }
            }
        });

        exported.Select(a => a.DisplayName).Should().Equal("postgresql", "ProcessOutboxMessage", "POST api/Payments");
        exported[1].ParentSpanId.Should().Be(exported[2].SpanId);
        exported[0].ParentSpanId.Should().Be(exported[1].SpanId);
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
    public void A_dependency_span_under_a_sampled_remote_parent_is_exported()
    {
        var exported = Run(() =>
        {
            using (Npgsql.StartActivity(
                "postgresql",
                ActivityKind.Client,
                new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, isRemote: true)))
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

    private static List<Activity> Run(Action work)
    {
        var exported = new List<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(Npgsql.Name, Redis.Name, Own.Name)
            .SetSampler(new ParentBasedSampler(new AlwaysOnSampler()))
            .AddProcessor(new TraceNoiseFilter())
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
