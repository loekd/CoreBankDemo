using System.Reflection;
using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.EventPublisher;

/// <summary>
/// Reflection guard for the internal publisher port. ADR-017 adds
/// <c>traceState</c> to Story 3.3's original call-site-simple contract while
/// transport configuration remains DI-bound.
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
    public void PublishAsync_takes_the_seven_parameters_required_by_ADR_017()
    {
        var parameters = Method.GetParameters();

        parameters.Should().HaveCount(7);

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

        parameters[5].Name.Should().Be("traceState");
        parameters[5].ParameterType.Should().Be(typeof(string));
        new NullabilityInfoContext().Create(parameters[5]).ReadState.Should().Be(NullabilityState.Nullable);

        parameters[6].Name.Should().Be("cancellationToken");
        parameters[6].ParameterType.Should().Be(typeof(CancellationToken));
        parameters[6].HasDefaultValue.Should().BeTrue();
    }

    [Fact]
    public void PublishAsync_does_not_declare_transport_configuration_or_envelope_id_parameters()
    {
        var names = Method.GetParameters().Select(p => p.Name).ToArray();

        names.Should().NotContain(new[] { "pubsubName", "topicName", "id" });
        names.Should().Contain("traceState");
    }

    [Fact]
    public void IEventPublisher_is_a_public_interface()
    {
        var type = typeof(IEventPublisher);

        type.IsInterface.Should().BeTrue();
        type.IsPublic.Should().BeTrue();
    }
}
