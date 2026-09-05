using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application;

public class FaultLevelsTests
{
    [Fact]
    public void CheckedInDefaults_MirrorTheShippedProfiles()
    {
        FaultLevels.CheckedInDefaults(TopologyProfile.Regular)
            .Should().Be(new FaultLevels(5, 20, 200, 1000));
        FaultLevels.CheckedInDefaults(TopologyProfile.LoadTests)
            .Should().Be(new FaultLevels(0, 9500, 12000, 0));
    }

    [Fact]
    public void PresetsForLoadTests_OfferTheTunedInstantRailOverrunBandWithEverythingElseAtZero()
    {
        var presets = FaultLevels.PresetsFor(TopologyProfile.LoadTests);

        var overrun = presets.Should().ContainSingle(preset => preset.Name == "Instant-rail overrun").Subject;
        overrun.Levels.LatencyFloorMs.Should().Be(9500);
        overrun.Levels.LatencyCeilingMs.Should().Be(12000);
        overrun.Levels.ErrorRatePercent.Should().Be(0);
        overrun.Levels.ThrottleRequestsPerWindow.Should().Be(0);
        presets[0].Should().Be(overrun);
        presets.Should().Contain(preset => preset.Name == "All off");
    }

    [Fact]
    public void PresetsForRegular_DoNotCarryTheLoadTestsBandAcross()
    {
        var presets = FaultLevels.PresetsFor(TopologyProfile.Regular);

        presets.Should().Contain(preset => preset.Levels == FaultLevels.CheckedInDefaults(TopologyProfile.Regular));
        presets.Should().NotContain(preset => preset.Levels.LatencyCeilingMs == 12000);
    }

    [Fact]
    public void MatchingPresetName_IsNullOnceAnyKnobMovesOffThePreset()
    {
        var preset = FaultLevels.CheckedInDefaults(TopologyProfile.Regular);

        preset.MatchingPresetName(TopologyProfile.Regular).Should().Be("Regular profile");
        (preset with { ErrorRatePercent = 40 }).MatchingPresetName(TopologyProfile.Regular).Should().BeNull();
    }

    [Fact]
    public void Normalized_SnapsToTheLadderAndKeepsTheBandOrdered()
    {
        var normalized = new FaultLevels(41, 2100, 780, 1010).Normalized();

        normalized.ErrorRatePercent.Should().Be(40);
        normalized.LatencyFloorMs.Should().Be(800);
        normalized.LatencyCeilingMs.Should().Be(2000);
        normalized.ThrottleRequestsPerWindow.Should().Be(1000);
    }

    [Fact]
    public void Normalized_OrdersAnInvertedBandSoAZeroCeilingOnlyEverMeansTheKnobIsOff()
    {
        new FaultLevels(0, 800, 0, 0).Normalized().Should().Be(new FaultLevels(0, 0, 800, 0));
        FaultLevels.AllZero.Normalized().Should().Be(FaultLevels.AllZero);
    }

    [Fact]
    public void AllZero_IsQuietAndInjectsNothing()
    {
        FaultLevels.AllZero.IsAllZero.Should().BeTrue();
        FaultLevels.AllZero.InjectsErrors.Should().BeFalse();
        FaultLevels.AllZero.InjectsLatency.Should().BeFalse();
        FaultLevels.AllZero.InjectsThrottling.Should().BeFalse();
        FaultLevels.AllZero.LatencyText.Should().Be("off");
        FaultLevels.AllZero.ThrottleText.Should().Be("off");
    }

    [Fact]
    public void Text_RendersEveryKnobAsAnExactNumberForReadingAloud()
    {
        var levels = new FaultLevels(40, 800, 2000, 100);

        levels.ErrorRateText.Should().Be("40%");
        levels.LatencyText.Should().Be("800–2000 ms");
        levels.ThrottleText.Should().Be("100/60s");
        levels.ToString().Should().Contain("40%").And.Contain("800–2000 ms").And.Contain("100/60s");
    }
}
