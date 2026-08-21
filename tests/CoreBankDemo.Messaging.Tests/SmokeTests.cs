using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.Messaging.Tests;

public class SmokeTests
{
    [Fact]
    public void Runner_assertions_and_target_assembly_are_wired()
    {
        typeof(MessageConstants).Assembly.GetName().Name
            .Should().Be("CoreBankDemo.Messaging");
    }
}
