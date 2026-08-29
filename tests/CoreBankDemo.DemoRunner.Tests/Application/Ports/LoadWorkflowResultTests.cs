using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application.Ports;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application.Ports;

public class LoadWorkflowResultTests
{
    [Fact]
    public void Success_AllInvariantsPassed_AllPassedIsTrue()
    {
        var result = LoadWorkflowResult.Success([new InvariantResult("Zero message loss", true, "ok")]);

        result.AllPassed.Should().BeTrue();
        result.Completed.Should().BeTrue();
    }

    [Fact]
    public void Success_SomeInvariantFailed_AllPassedIsFalse()
    {
        var result = LoadWorkflowResult.Success([new InvariantResult("Zero message loss", false, "mismatch")]);

        result.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void PhaseFailure_WithoutInvariants_DefaultsToEmptyList()
    {
        var result = LoadWorkflowResult.PhaseFailure(LoadWorkflowPhase.Wait, "timed out");

        result.Completed.Should().BeFalse();
        result.AllPassed.Should().BeFalse();
        result.FailedAtPhase.Should().Be(LoadWorkflowPhase.Wait);
        result.Invariants.Should().BeEmpty();
        result.ErrorSummary.Should().Be("timed out");
    }

    [Fact]
    public void PhaseFailure_WithPartialInvariants_PreservesThem()
    {
        var invariants = new[] { new InvariantResult("Balance conservation", true, "ok") };

        var result = LoadWorkflowResult.PhaseFailure(LoadWorkflowPhase.Assert, "one check failed", invariants);

        result.Invariants.Should().BeEquivalentTo(invariants);
    }
}
