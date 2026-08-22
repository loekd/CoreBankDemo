using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.Messaging.Tests;

/// <summary>
/// <see cref="OutboxProcessorOptions.PollingInterval"/>'s fail-fast guard
/// (story 2.4 review patch): a non-positive interval must never reach
/// <see cref="OutboxProcessorBase{TMessage}"/>'s poll loop, where
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> would throw
/// <see cref="ArgumentOutOfRangeException"/> and crash the whole
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> instead of
/// degrading. Full options validation is out of scope for this story (epic 3)
/// — this is the minimal guard needed to prevent that specific crash.
/// </summary>
public class OutboxProcessorOptionsTests
{
    [Fact]
    public void Default_polling_interval_is_positive()
    {
        var options = new OutboxProcessorOptions();

        options.PollingInterval.Should().Be(MessageConstants.Defaults.PollingInterval);
    }

    [Theory]
    [MemberData(nameof(NonPositiveIntervals))]
    public void Non_positive_polling_interval_throws_at_construction_rather_than_crashing_the_poll_loop_later(TimeSpan invalid)
    {
        var act = () => new OutboxProcessorOptions { PollingInterval = invalid };

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("PollingInterval");
    }

    public static TheoryData<TimeSpan> NonPositiveIntervals() => new()
    {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(-1),
        TimeSpan.FromMilliseconds(-1),
    };
}
