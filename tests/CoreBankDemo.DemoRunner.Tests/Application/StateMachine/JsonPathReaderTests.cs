using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application.StateMachine;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application.StateMachine;

public class JsonPathReaderTests
{
    [Fact]
    public void TryRead_TopLevelStringProperty_ReturnsValue()
    {
        var result = JsonPathReader.TryRead("""{"transactionId":"abc-123"}""", "$.transactionId");

        result.Should().Be("abc-123");
    }

    [Fact]
    public void TryRead_NestedProperty_ReturnsValue()
    {
        var result = JsonPathReader.TryRead("""{"checks":{"noFailedMessages":{"passed":true}}}""", "$.checks.noFailedMessages.passed");

        result.Should().Be("true");
    }

    [Fact]
    public void TryRead_NumericProperty_ReturnsRawText()
    {
        var result = JsonPathReader.TryRead("""{"count":42}""", "$.count");

        result.Should().Be("42");
    }

    [Fact]
    public void TryRead_ArrayProperty_ReturnsRawTextViaDefaultBranch()
    {
        var result = JsonPathReader.TryRead("""{"items":[1,2,3]}""", "$.items");

        result.Should().Be("[1,2,3]");
    }

    [Fact]
    public void TryRead_IsCaseInsensitiveToPropertyNames()
    {
        var result = JsonPathReader.TryRead("""{"Checks":{"NoFailedMessages":{"Passed":true}}}""", "$.checks.noFailedMessages.passed");

        result.Should().Be("true");
    }

    [Fact]
    public void TryRead_MissingProperty_ReturnsNull()
    {
        var result = JsonPathReader.TryRead("""{"a":1}""", "$.b");

        result.Should().BeNull();
    }

    [Fact]
    public void TryRead_InvalidJson_ReturnsNull()
    {
        var result = JsonPathReader.TryRead("not json", "$.a");

        result.Should().BeNull();
    }

    [Fact]
    public void TryRead_NullOrEmptyInputs_ReturnsNull()
    {
        JsonPathReader.TryRead(null, "$.a").Should().BeNull();
        JsonPathReader.TryRead("""{"a":1}""", null).Should().BeNull();
        JsonPathReader.TryRead("""{"a":1}""", "").Should().BeNull();
    }
}
