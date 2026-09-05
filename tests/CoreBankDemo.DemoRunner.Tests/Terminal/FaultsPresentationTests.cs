using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Terminal;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

public class FaultsPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FaultChip_Unavailable_WhenTheTopologyStartedUnarmedOrIsAttached()
    {
        var model = PresentationModelBuilder.Build(Armed(false, FaultLevels.AllZero), Now);

        model.Faults.ChipSymbol.Should().Be("-");
        model.Faults.ChipLabel.Should().Be("Faults unavailable");
        model.TopologyBar.Should().Contain("- Faults unavailable");
        model.Faults.Available.Should().BeFalse();
        model.Faults.DisabledReason.Should().NotBeNullOrWhiteSpace();
        model.Faults.CanApply.Should().BeFalse();
    }

    [Theory]
    [InlineData(ResourceCondition.Stopped)]
    [InlineData(ResourceCondition.Failed)]
    [InlineData(ResourceCondition.Unknown)]
    public void FaultChip_Unavailable_WhenTheTopologyWasArmedButItsProxyIsNotRunning(ResourceCondition condition)
    {
        var state = Armed(true, new FaultLevels(40, 0, 0, 0), condition) with { FaultsObserved = true };

        var model = PresentationModelBuilder.Build(state, Now);

        model.Faults.ChipSymbol.Should().Be("-");
        model.Faults.ChipLabel.Should().Be("Faults unavailable — Dev Proxy not running");
        model.Faults.Available.Should().BeFalse();
        model.Faults.CanApply.Should().BeFalse();
        model.Faults.DisabledReason.Should().Contain("devproxy resource");
    }

    [Fact]
    public void FaultChip_Unavailable_WhenTheSnapshotCarriesNoDevProxyResourceAtAll()
    {
        var state = Armed(true, FaultLevels.AllZero) with
        {
            Topology = OperatorHarness.Snapshot(TopologyProfile.Regular),
        };

        PresentationModelBuilder.Build(state, Now).Faults.ChipSymbol.Should().Be("-");
    }

    [Fact]
    public void FaultChip_BoundsTheUnobservedReadoutInsteadOfCountingUpForever()
    {
        var state = Armed(true, new FaultLevels(0, 800, 2000, 0)) with
        {
            FaultsAppliedAt = Now - FaultLevels.ObservationWindow,
            FaultsObserved = false,
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.Faults.ChipLabel.Should().Contain("still not observed after 30s");
        model.Faults.ChipLabel.Should().NotContain("in force");
    }

    [Fact]
    public void FaultDetail_NamesWhereTheLevelsOnScreenCameFrom()
    {
        var fromProfile = Armed(true, FaultLevels.AllZero);
        PresentationModelBuilder.Build(fromProfile, Now).Faults.Detail
            .Should().Contain("checked-in Regular Dev Proxy profile");

        var fromSession = fromProfile with { FaultLevelsFromSession = true };
        PresentationModelBuilder.Build(fromSession, Now).Faults.Detail
            .Should().Contain("this session's generated Dev Proxy config");
    }

    [Fact]
    public void EvidenceProvenance_RendersTheFaultLevelsInForceAndOmitsThemWhenThereWereNone()
    {
        var quiet = Record(1, null);
        var underFaults = Record(2, new FaultLevels(40, 800, 2000, 0));
        var state = Armed(true, FaultLevels.AllZero) with
        {
            Evidence = [quiet, underFaults],
            SelectedEvidence = underFaults,
        };

        var model = PresentationModelBuilder.Build(state, Now);

        var renderedUnderFaults = model.Evidence.Single(row => row.Sequence == 2);
        renderedUnderFaults.Provenance.Should().Contain("faults error rate 40%").And.Contain("800–2000 ms");
        renderedUnderFaults.Detail.Should().Contain("Faults: error rate 40%");

        var renderedQuiet = model.Evidence.Single(row => row.Sequence == 1);
        renderedQuiet.Provenance.Should().NotContain("faults");
        renderedQuiet.Detail.Should().Contain("Faults: none in force");
        model.SelectedEvidenceDetail.Should().Contain("Faults: error rate 40%");
    }

    private static EvidenceRecord Record(long sequence, FaultLevels? faults) =>
        new(
            sequence,
            Now,
            TopologyProfile.Regular,
            1,
            EvidenceKind.Payment,
            "202 Pending — no committed outcome yet",
            "POST",
            KnownEndpoints.PaymentsSubmit,
            202,
            TimeSpan.FromMilliseconds(950),
            "body",
            true,
            faults);

    [Fact]
    public void FaultChip_Armed_WhenAProxyIsRunningButEveryKnobIsZero()
    {
        var model = PresentationModelBuilder.Build(Armed(true, FaultLevels.AllZero), Now);

        model.Faults.ChipSymbol.Should().Be("·");
        model.Faults.ChipLabel.Should().Be("Armed");
        model.TopologyBar.Should().Contain("· Armed");
    }

    [Fact]
    public void FaultChip_AppliedButNotObserved_ShowsAnElapsedReadoutAndNeverClaimsInForce()
    {
        var state = Armed(true, new FaultLevels(0, 800, 2000, 0)) with
        {
            FaultsAppliedAt = Now.AddSeconds(-4),
            FaultsObserved = false,
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.Faults.ChipSymbol.Should().Be("·");
        model.Faults.ChipLabel.Should().Be("Applied — not yet observed in traffic (4s)");
        model.Faults.ChipLabel.Should().NotContain("in force");
    }

    [Fact]
    public void FaultChip_InForce_OnlyOnceTrafficHasCarriedTheLevels()
    {
        var state = Armed(true, new FaultLevels(40, 0, 0, 0)) with
        {
            FaultsAppliedAt = Now.AddSeconds(-4),
            FaultsObserved = true,
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.Faults.ChipSymbol.Should().Be("!");
        model.Faults.ChipLabel.Should().Be("Faults in force");
    }

    [Fact]
    public void StagedKnob_RendersItsLiveValueAndItsStagedValueAsAnExplicitDelta()
    {
        var state = Armed(true, new FaultLevels(5, 20, 200, 1000)) with
        {
            StagedFaults = new FaultLevels(40, 800, 2000, 1000),
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.Faults.Knobs[0].ValueText.Should().Be("5% → 40%");
        model.Faults.Knobs[1].ValueText.Should().Be("20–200 ms → 800–2000 ms");
        model.Faults.Knobs[2].ValueText.Should().Be("1000/60s", "an unstaged knob shows its live value alone");
        model.Faults.Knobs[2].IsStaged.Should().BeFalse();
        model.Faults.CanApply.Should().BeTrue();
        model.Faults.ApplyCaption.Should().Be("Apply 2 staged knobs");
        model.Faults.PresetLabel.Should().Be("Custom");
    }

    [Fact]
    public void Apply_IsDisabledWithAReasonWhenNothingIsStaged()
    {
        var model = PresentationModelBuilder.Build(Armed(true, FaultLevels.AllZero), Now);

        model.Faults.CanApply.Should().BeFalse();
        model.Faults.ApplyCaption.Should().Be("Apply (nothing staged)");
        model.Faults.PresetLabel.Should().Be("All off");
    }

    [Fact]
    public void LoadTestsPresets_LeadWithTheTunedInstantRailOverrunBand()
    {
        var state = Armed(true, FaultLevels.AllZero) with { Profile = TopologyProfile.LoadTests };

        var model = PresentationModelBuilder.Build(state, Now);

        model.Faults.Presets[0].Name.Should().Be("Instant-rail overrun");
        model.Faults.Presets[0].Levels.Should().Be(new FaultLevels(0, 9500, 12000, 0));
    }

    [Fact]
    public void LoadHint_StatesThatConditionsAreNonDefaultBeforeRunIsFired()
    {
        var harness = new OperatorHarness();
        var state = Armed(true, new FaultLevels(40, 800, 2000, 0)) with
        {
            Profile = TopologyProfile.LoadTests,
            Ownership = TopologyOwnership.Owned,
            ResourceAuthorityAvailable = true,
            Topology = OperatorHarness.Snapshot(TopologyProfile.LoadTests, harness.Time.GetUtcNow()),
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.LoadHint.Should().Contain("Fault injection is in force");
        model.LoadHint.Should().Contain("40%");
        model.LoadHint.Should().Contain("Press 0");
    }

    [Fact]
    public void LoadHint_IsSilentAboutFaultsWhenEveryKnobIsZero()
    {
        var harness = new OperatorHarness();
        var state = Armed(true, FaultLevels.AllZero) with
        {
            Profile = TopologyProfile.LoadTests,
            Ownership = TopologyOwnership.Owned,
            ResourceAuthorityAvailable = true,
            Topology = OperatorHarness.Snapshot(TopologyProfile.LoadTests, harness.Time.GetUtcNow()),
        };

        PresentationModelBuilder.Build(state, Now).LoadHint.Should().NotContain("Fault injection");
    }

    [Theory]
    [InlineData(TopologyOwnership.None, true, "Faults armed on next AppHost start")]
    [InlineData(TopologyOwnership.None, false, "Faults not armed on next AppHost start")]
    [InlineData(TopologyOwnership.Attached, true, "read-only")]
    [InlineData(TopologyOwnership.Owned, true, "restart it to change")]
    public void ArmingCaption_AlwaysNamesItsLaunchTimeMeaning(
        TopologyOwnership ownership,
        bool requested,
        string expected)
    {
        var state = OperatorConsoleState.Empty with
        {
            Profile = TopologyProfile.Regular,
            Ownership = ownership,
            FaultArmingRequested = requested,
        };

        var model = PresentationModelBuilder.Build(state, Now);

        model.ArmingCaption.Should().Contain(expected);
        model.ArmingCaption.Should().NotBe("On").And.NotBe("Off");
        model.CanChangeArming.Should().Be(ownership == TopologyOwnership.None);
    }

    private static OperatorConsoleState Armed(
        bool armed,
        FaultLevels applied,
        ResourceCondition devProxyCondition = ResourceCondition.Healthy) =>
        OperatorConsoleState.Empty with
        {
            Profile = TopologyProfile.Regular,
            Ownership = armed ? TopologyOwnership.Owned : TopologyOwnership.None,
            // The chip refuses to claim Armed or Faults in force without a live proxy in the
            // snapshot, so an armed fixture has to carry one.
            Topology = OperatorHarness.ArmedSnapshot(TopologyProfile.Regular, devProxyCondition: devProxyCondition),
            FaultsArmed = armed,
            AppliedFaults = applied,
            StagedFaults = applied,
            FaultsObserved = !applied.IsAllZero,
        };
}
