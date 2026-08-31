using AwesomeAssertions;
using CoreBankDemo.LoadTestInitializer;
using Xunit;

namespace CoreBankDemo.LoadTestInitializer.Tests;

public sealed class ResetResponseValidatorTests
{
    [Fact]
    public void Complete_reset_response_is_accepted()
    {
        var act = () => ResetResponseValidator.Validate(
            """{"message":"Database reset complete","accountsReset":10,"totalBalance":100000000,"initialBalancePerAccount":10000000}""");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("not-json", "not valid JSON")]
    [InlineData("null", "JSON null")]
    [InlineData("{}", "semantically incomplete")]
    [InlineData("{\"message\":\"Database reset complete\",\"accountsReset\":9,\"totalBalance\":90000000,\"initialBalancePerAccount\":10000000}", "accountsReset=9")]
    [InlineData("{\"message\":\"Database reset complete\",\"accountsReset\":10,\"totalBalance\":999,\"initialBalancePerAccount\":10000000}", "totalBalance=999")]
    public void Malformed_or_incomplete_reset_response_is_rejected(string json, string expectedMessage)
    {
        var act = () => ResetResponseValidator.Validate(json);

        act.Should().Throw<InvalidDataException>().WithMessage($"*{expectedMessage}*");
    }
}
