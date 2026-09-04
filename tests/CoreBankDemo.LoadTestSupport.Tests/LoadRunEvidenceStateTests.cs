using AwesomeAssertions;
using CoreBankDemo.LoadTestSupport.Services;
using Xunit;

namespace CoreBankDemo.LoadTestSupport.Tests;

public class LoadRunEvidenceStateTests
{
    [Fact]
    public void RecordInlineSettlement_DeduplicatesKeysAndResetClearsRun()
    {
        var state = new LoadRunEvidenceState();

        state.RecordInlineSettlement("load-test-1").Should().BeTrue();
        state.RecordInlineSettlement("load-test-1").Should().BeFalse();
        state.RecordInlineSettlement("load-test-2").Should().BeTrue();
        state.InlineSettlementCount.Should().Be(2);

        state.Reset();

        state.InlineSettlementCount.Should().Be(0);
    }
}
