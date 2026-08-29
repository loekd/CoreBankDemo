using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application.StateMachine;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application.StateMachine;

public class IdempotencyKeyProviderTests
{
    [Fact]
    public void ForCueAction_SameInputs_ProducesTheSameKey()
    {
        var a = IdempotencyKeyProvider.ForCueAction("run-1", "cue-1", "ref-1");
        var b = IdempotencyKeyProvider.ForCueAction("run-1", "cue-1", "ref-1");

        a.Should().Be(b);
    }

    [Theory]
    [InlineData("run-2", "cue-1", "ref-1")]
    [InlineData("run-1", "cue-2", "ref-1")]
    [InlineData("run-1", "cue-1", "ref-2")]
    public void ForCueAction_DifferentInputs_ProducesDifferentKeys(string runId, string cueId, string keyRef)
    {
        var baseline = IdempotencyKeyProvider.ForCueAction("run-1", "cue-1", "ref-1");
        var other = IdempotencyKeyProvider.ForCueAction(runId, cueId, keyRef);

        other.Should().NotBe(baseline);
    }

    [Fact]
    public void ForCueAction_ProducesAParsableGuid()
    {
        var key = IdempotencyKeyProvider.ForCueAction("run-1", "cue-1", "ref-1");

        Guid.TryParse(key, out _).Should().BeTrue();
    }
}
