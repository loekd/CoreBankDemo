using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

public class SmokeTests
{
    [Fact]
    public void Runner_and_assertions_are_wired()
    {
        true.Should().BeTrue();
    }
}
