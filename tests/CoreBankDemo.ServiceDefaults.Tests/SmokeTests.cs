using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests;

public class SmokeTests
{
    [Fact]
    public void Runner_assertions_and_target_assembly_are_wired()
    {
        typeof(IDistributedLockService).Assembly.GetName().Name
            .Should().Be("CoreBankDemo.ServiceDefaults");
    }
}
