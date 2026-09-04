using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application;

public class TopologyObservationDebouncerTests
{
    [Fact]
    public void Observe_StableSnapshot_RefreshesImmediately()
    {
        var debouncer = new TopologyObservationDebouncer();
        var current = OperatorHarness.Snapshot(TopologyProfile.Regular);
        var observed = current with { CapturedAt = current.CapturedAt.AddSeconds(1) };

        debouncer.Observe(current, observed).Should().Be(observed);
    }

    [Fact]
    public void Observe_ChangedSnapshot_RequiresTwoMatchingObservations()
    {
        var debouncer = new TopologyObservationDebouncer();
        var current = OperatorHarness.Snapshot(TopologyProfile.Regular);
        var changed = current with
        {
            Resources =
            [
                new ResourceSnapshot(KnownResources.CoreBankApi, ResourceCondition.Stopped, "Stopped", []),
                .. current.Resources.Where(resource => resource.Name != KnownResources.CoreBankApi),
            ],
        };

        var first = debouncer.Observe(current, changed);
        var second = debouncer.Observe(first, changed);

        first.FindResource(KnownResources.CoreBankApi)!.Condition.Should().Be(ResourceCondition.Healthy);
        first.ErrorSummary.Should().Contain("confirming snapshot");
        second.FindResource(KnownResources.CoreBankApi)!.Condition.Should().Be(ResourceCondition.Stopped);
    }

    [Fact]
    public void Observe_UnreachableAndReset_DoNotRetainCandidate()
    {
        var debouncer = new TopologyObservationDebouncer();
        var current = OperatorHarness.Snapshot(TopologyProfile.Regular);
        var unreachable = TopologySnapshot.Unreachable(TopologyProfile.Regular, current.CapturedAt, "transport");

        debouncer.Observe(current, unreachable).Should().Be(unreachable);
        debouncer.Reset();
        debouncer.Observe(current, current).Should().Be(current);
    }
}
