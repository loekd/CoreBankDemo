using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Infrastructure;

public class PostgresContainerFixtureTests
{
    [Fact]
    public void Container_start_failure_message_is_bounded_actionable_and_preserves_the_cause()
    {
        var message = PostgresContainerFixture.BuildUnavailableMessage(
            new InvalidOperationException("container runtime unavailable"));

        message.Should().Contain($"{PostgresContainerFixture.StartupTimeout.TotalMinutes:0} minutes");
        message.Should().Contain("never skipped or reported green");
        message.Should().Contain("docker info");
        message.Should().Contain($"docker pull {PostgresImage.Tag}");
        message.Should().Contain("dotnet test CoreBankDemo.UnitTests.slnf");
        message.Should().Contain("container runtime unavailable");
    }
}
