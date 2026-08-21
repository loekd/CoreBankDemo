using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.Messaging.Tests;

/// <summary>
/// Pins the verbatim legacy values (epic-2 context, Legacy Behavioral Reference).
/// Existing rows in message tables carry these status strings — any drift breaks them.
/// </summary>
public class MessageConstantsTests
{
    [Fact]
    public void Status_values_are_verbatim_legacy_strings()
    {
        MessageConstants.Status.Pending.Should().Be("Pending");
        MessageConstants.Status.Processing.Should().Be("Processing");
        MessageConstants.Status.Completed.Should().Be("Completed");
        MessageConstants.Status.Failed.Should().Be("Failed");
    }

    [Fact]
    public void Default_limits_are_verbatim_legacy_values()
    {
        MessageConstants.Defaults.MaxRetryCount.Should().Be(5);
        MessageConstants.Defaults.BatchSize.Should().Be(10);
        MessageConstants.Defaults.ProcessingTimeout.Should().Be(TimeSpan.FromMinutes(5));
        MessageConstants.Defaults.PollingInterval.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Status_values_are_distinct_and_non_empty()
    {
        var statuses = new[]
        {
            MessageConstants.Status.Pending,
            MessageConstants.Status.Processing,
            MessageConstants.Status.Completed,
            MessageConstants.Status.Failed,
        };

        statuses.Should().OnlyHaveUniqueItems("a duplicated status literal would corrupt every state machine");
        statuses.Should().NotContainNulls().And.NotContain(string.Empty);
    }
}
