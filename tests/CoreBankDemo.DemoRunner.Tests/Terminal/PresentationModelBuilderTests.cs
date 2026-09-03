using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Terminal;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

public class PresentationModelBuilderTests
{
    [Fact]
    public void Build_EmptyState_ShowsFourWorkspacesAndColdPlaceholders()
    {
        var model = PresentationModelBuilder.Build(OperatorConsoleState.Empty);

        model.Navigation.Should().HaveCount(4);
        model.Navigation.Should().Contain(item => item.Shortcut == "1" && item.Label == "Operations");
        model.EvidenceStrip.Should().Be("No actions yet this session.");
        model.LoadResults.Should().HaveCount(6);
        model.LoadResults.Should().OnlyContain(value => value.Contains("not yet observed"));
    }

    [Fact]
    public void Build_ResourceStates_UseSymbolTextAndStableActions()
    {
        var resources = new[]
        {
            new ResourceSnapshot(KnownResources.CoreBankApi, ResourceCondition.Healthy, "Healthy", ["http://core"], 2),
            new ResourceSnapshot(KnownResources.PaymentsApi, ResourceCondition.Stopped, "Stopped", []),
            new ResourceSnapshot(KnownResources.Redis, ResourceCondition.Unreachable, "Unreachable", []),
            new ResourceSnapshot(KnownResources.Postgres, ResourceCondition.Failed, "Failed", []),
        };
        var snapshot = OperatorHarness.Snapshot(TopologyProfile.Regular, resources: resources);
        var state = OperatorConsoleState.Empty with
        {
            Profile = TopologyProfile.Regular,
            Ownership = TopologyOwnership.Attached,
            RunGeneration = 3,
            Topology = snapshot,
            ResourceAuthorityAvailable = true,
        };

        var model = PresentationModelBuilder.Build(state);

        model.TopologyBar.Should().Contain("Regular").And.Contain("Attached");
        model.Resources.Should().Contain(row => row.Name == KnownResources.CoreBankApi && row.Symbol == "●" && row.NextAction == "Stop");
        model.Resources.Should().Contain(row => row.Name == KnownResources.PaymentsApi && row.Symbol == "○" && row.NextAction == "Start");
        model.Resources.Should().Contain(row => row.Name == KnownResources.Redis && row.State == "Unreachable" && !row.CanMutate);
        model.Resources.Should().Contain(row => row.Name == KnownResources.Postgres && row.Symbol == "✕" && row.NextAction == "Restart");
    }

    [Fact]
    public void Build_EvidenceAndLoadResults_ShowProvenanceAndIndividualVerdicts()
    {
        var evidence = new EvidenceRecord(
            7,
            DateTimeOffset.UnixEpoch,
            TopologyProfile.LoadTests,
            4,
            EvidenceKind.LoadTest,
            "Load workflow passed",
            "accepted load workflow",
            "load",
            null,
            TimeSpan.FromSeconds(2),
            "raw",
            true);
        var result = LoadWorkflowResult.Success(
            [new InvariantResult("Exactly-once processing", true, "ok")],
            new InlineSettlementResult(true, "count=20"),
            "raw");
        var state = OperatorConsoleState.Empty with
        {
            Profile = TopologyProfile.LoadTests,
            Ownership = TopologyOwnership.Owned,
            RunGeneration = 4,
            Evidence = [evidence],
            SelectedEvidence = evidence,
            LastLoadResult = result,
        };

        var model = PresentationModelBuilder.Build(state);

        model.Evidence.Single().Provenance.Should().Contain("LoadTests · generation 4");
        model.SelectedEvidenceDetail.Should().Contain("raw");
        model.LoadResults.Should().Contain(value => value.Contains("Inline instant settlement"));
        model.CanStopOrSwitch.Should().BeTrue();
        model.CanUseLoadTest.Should().BeTrue();
    }

    [Fact]
    public void Build_ActiveBurst_LeavesOnlyBurstCancelFlagEnabled()
    {
        var state = OperatorConsoleState.Empty with
        {
            ActiveMutation = new ActiveMutation(MutationKind.PaymentBurst, "burst", DateTimeOffset.UnixEpoch),
            Burst = new BurstProgress(10, 3, 3, 0, 0, false),
            CanResendLastPayment = true,
        };

        var model = PresentationModelBuilder.Build(state);

        model.IsBusy.Should().BeTrue();
        model.CanCancelBurst.Should().BeTrue();
        model.CanResend.Should().BeFalse();
        model.BurstStatus.Should().Contain("3/10");
    }
}
