using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application.Ports;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application.Ports;

public class JournalRedactionTests
{
    [Fact]
    public void Apply_ShortText_IsUnchanged()
    {
        JournalRedaction.Apply("payments.submit accepted (202).").Should().Be("payments.submit accepted (202).");
    }

    [Fact]
    public void Apply_TextLongerThanMaxLength_IsTruncatedWithEllipsis()
    {
        var text = new string('x', JournalRedaction.MaxLength + 50);

        var result = JournalRedaction.Apply(text);

        result.Length.Should().Be(JournalRedaction.MaxLength + 1);
        result.Should().EndWith("…");
    }

    [Theory]
    [InlineData("Authorization: Bearer abcdef123")]
    [InlineData("authorization=abcdef123")]
    [InlineData("Idempotency-Key: 11111111-2222-3333-4444-555555555555")]
    public void Apply_SecretLikeHeaderText_IsRedacted(string text)
    {
        var result = JournalRedaction.Apply(text);

        result.Should().Contain("[redacted]");
        result.Should().NotContain("abcdef123");
    }

    [Fact]
    public void Apply_OrdinaryEvidenceText_IsNotRedacted()
    {
        var result = JournalRedaction.Apply("corebank.transactions.process accepted (202).");

        result.Should().NotContain("[redacted]");
    }
}
