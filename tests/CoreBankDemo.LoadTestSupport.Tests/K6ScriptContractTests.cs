using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.LoadTestSupport.Tests;

public sealed class K6ScriptContractTests
{
    private static readonly string Script = File.ReadAllText(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../k6/script.js")));

    [Fact]
    public void Every_named_check_is_a_fail_closed_threshold()
    {
        Script.Should().Contain("'checks': ['rate==1']");
    }

    [Theory]
    [InlineData("state gate: payments outbox endpoint returned 200")]
    [InlineData("state gate: payments outbox contains submitted messages")]
    [InlineData("inbox drained within timeout")]
    [InlineData("assert endpoint returned 200")]
    [InlineData("all checks passed")]
    [InlineData("stage cardinality N/N/3N/3N")]
    [InlineData("canonical account set exact")]
    public void Critical_state_gate_is_named_and_therefore_thresholded(string checkName)
    {
        Script.Should().Contain($"'{checkName}'");
    }

    [Fact]
    public void Malformed_endpoint_json_records_a_failed_check_before_returning()
    {
        Script.Should().Contain("state gate: drain endpoint returned valid JSON': () => false");
        Script.Should().Contain("state gate: assert endpoint returned valid JSON': () => false");
        Script.Should().Contain("state gate: payments outbox returned valid JSON': () => false");
    }
}
