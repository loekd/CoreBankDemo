using System.Reflection;
using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.DistributedLock;

/// <summary>
/// Story 3.2's core compatibility guarantee: <c>CoreBankDemo.Messaging</c>'s
/// <c>InboxProcessorBase</c>/<c>OutboxProcessorBase</c> (epic 2, already merged
/// and tested — 153 passing tests) compile against and call
/// <see cref="IDistributedLockService.ExecuteWithLockAsync"/>'s exact current
/// signature. This reflection guard is a permanent regression check, in
/// addition to (not instead of) the real proof: building
/// <c>CoreBankDemo.Messaging.csproj</c> unmodified against the rebuilt
/// interface — see the story's Verification commands.
/// </summary>
public class IDistributedLockServiceSignatureTests
{
    // The non-blocking form Messaging's processors call. Resolved by parameter
    // list because the port also has the bounded-acquire overload (ADR-018
    // priority addendum), which the instant rail's inline paths use.
    private static readonly MethodInfo Method =
        typeof(IDistributedLockService).GetMethod(
            nameof(IDistributedLockService.ExecuteWithLockAsync),
            [typeof(string), typeof(int), typeof(Func<CancellationToken, Task>), typeof(CancellationToken)])!;

    private static readonly MethodInfo BoundedMethod =
        typeof(IDistributedLockService).GetMethod(
            nameof(IDistributedLockService.ExecuteWithLockAsync),
            [typeof(string), typeof(int), typeof(TimeSpan), typeof(Func<CancellationToken, Task>), typeof(CancellationToken)])!;

    [Fact]
    public void Bounded_acquire_overload_has_a_default_implementation_so_existing_implementations_keep_working()
    {
        BoundedMethod.Should().NotBeNull();
        BoundedMethod.ReturnType.Should().Be(typeof(Task<bool>));
        BoundedMethod.GetParameters()[2].Name.Should().Be("acquireTimeout");
        BoundedMethod.IsAbstract.Should().BeFalse("a default interface implementation delegates to the non-blocking form");
    }

    [Fact]
    public void ExecuteWithLockAsync_exists_and_returns_TaskOfBool()
    {
        Method.Should().NotBeNull();
        Method.ReturnType.Should().Be(typeof(Task<bool>));
    }

    [Fact]
    public void ExecuteWithLockAsync_takes_exactly_the_four_parameters_Messaging_calls_it_with()
    {
        var parameters = Method.GetParameters();

        parameters.Should().HaveCount(4);

        parameters[0].Name.Should().Be("lockName");
        parameters[0].ParameterType.Should().Be(typeof(string));

        parameters[1].Name.Should().Be("lockExpirySeconds");
        parameters[1].ParameterType.Should().Be(typeof(int));

        parameters[2].Name.Should().Be("workload");
        parameters[2].ParameterType.Should().Be(typeof(Func<CancellationToken, Task>));

        parameters[3].Name.Should().Be("cancellationToken");
        parameters[3].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [Fact]
    public void CancellationToken_parameter_defaults_to_default_CancellationToken()
    {
        var cancellationTokenParameter = Method.GetParameters()[3];

        // Note: for a non-primitive struct parameter defaulted via "= default", the C#
        // compiler emits only the [Optional] metadata flag (HasDefaultValue) — there is
        // no constant to recover, so ParameterInfo.DefaultValue reads null even though
        // callers that omit the argument really do get default(CancellationToken). This
        // is a reflection quirk, not a signature difference, so DefaultValue itself isn't
        // asserted here.
        cancellationTokenParameter.HasDefaultValue.Should().BeTrue(
            "Messaging calls ExecuteWithLockAsync with a real token but the interface still declares a default so other callers may omit it");
    }

    [Fact]
    public void IDistributedLockService_is_a_public_interface()
    {
        var type = typeof(IDistributedLockService);

        type.IsInterface.Should().BeTrue();
        type.IsPublic.Should().BeTrue("Messaging references this type across assembly boundaries");
    }
}
