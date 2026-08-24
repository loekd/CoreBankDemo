using System.Reflection;
using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.EventPublisher;

/// <summary>
/// Story 3.3: <see cref="IEventPublisher.PublishAsync"/>'s parameter list is
/// mandated verbatim by epics.md — <c>(string type, string source, string
/// subject, object payload, string? traceParent, CancellationToken
/// cancellationToken = default)</c> — and is deliberately call-site-simple:
/// no <c>pubsubName</c>/<c>topicName</c> parameters (those are DI-bound via
/// <c>MessagingOutboxProcessingOptions</c>), no <c>id</c>/<c>tracestate</c>
/// parameters (Ask-First resolution: deliberate scope decision, not an
/// oversight — see the story spec's Boundaries &amp; Constraints). This
/// reflection guard is a permanent regression check against that exact
/// contract.
/// </summary>
public class IEventPublisherSignatureTests
{
    private static readonly MethodInfo Method =
        typeof(IEventPublisher).GetMethod(nameof(IEventPublisher.PublishAsync))!;

    [Fact]
    public void PublishAsync_exists_and_returns_Task()
    {
        Method.Should().NotBeNull();
        Method.ReturnType.Should().Be(typeof(Task));
    }

    [Fact]
    public void PublishAsync_takes_exactly_the_six_parameters_epics_md_mandates()
    {
        var parameters = Method.GetParameters();

        parameters.Should().HaveCount(6);

        parameters[0].Name.Should().Be("type");
        parameters[0].ParameterType.Should().Be(typeof(string));

        parameters[1].Name.Should().Be("source");
        parameters[1].ParameterType.Should().Be(typeof(string));

        parameters[2].Name.Should().Be("subject");
        parameters[2].ParameterType.Should().Be(typeof(string));

        parameters[3].Name.Should().Be("payload");
        parameters[3].ParameterType.Should().Be(typeof(object));

        parameters[4].Name.Should().Be("traceParent");
        parameters[4].ParameterType.Should().Be(typeof(string));
        new NullabilityInfoContext().Create(parameters[4]).ReadState.Should().Be(NullabilityState.Nullable);

        parameters[5].Name.Should().Be("cancellationToken");
        parameters[5].ParameterType.Should().Be(typeof(CancellationToken));
        parameters[5].HasDefaultValue.Should().BeTrue();
    }

    [Fact]
    public void PublishAsync_does_not_declare_pubsubName_topicName_id_or_tracestate_parameters()
    {
        var names = Method.GetParameters().Select(p => p.Name).ToArray();

        names.Should().NotContain(new[] { "pubsubName", "topicName", "id", "tracestate", "traceState" });
    }

    [Fact]
    public void IEventPublisher_is_a_public_interface()
    {
        var type = typeof(IEventPublisher);

        type.IsInterface.Should().BeTrue();
        type.IsPublic.Should().BeTrue();
    }
}
