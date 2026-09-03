using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application;

public class LoadWorkflowResultTests
{
    [Fact]
    public void Success_RequiresInvariantsAndInlineSettlement()
    {
        var passed = LoadWorkflowResult.Success(
            [new InvariantResult("one", true, "ok")],
            new InlineSettlementResult(true, "observed"),
            "detail");
        var missingInline = LoadWorkflowResult.Success(
            [new InvariantResult("one", true, "ok")],
            new InlineSettlementResult(false, "missing"),
            "detail");

        passed.AllPassed.Should().BeTrue();
        missingInline.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void Failure_PreservesPartialEvidence()
    {
        var result = LoadWorkflowResult.Failure(
            LoadWorkflowPhase.Assert,
            "failed",
            [new InvariantResult("one", false, "bad")],
            "raw");

        result.Completed.Should().BeFalse();
        result.AllPassed.Should().BeFalse();
        result.FinalPhase.Should().Be(LoadWorkflowPhase.Assert);
        result.Invariants.Should().ContainSingle();
        result.InvestigationDetail.Should().Be("raw");
    }
}
