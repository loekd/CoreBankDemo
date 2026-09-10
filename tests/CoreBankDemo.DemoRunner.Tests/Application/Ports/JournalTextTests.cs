using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application.Ports;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application.Ports;

public class JournalTextTests
{
    [Fact]
    public void Bound_ShortText_IsUnchanged()
    {
        JournalText.Bound("payments.submit accepted (202).").Should().Be("payments.submit accepted (202).");
    }

    [Fact]
    public void Bound_TextLongerThanMaxLength_IsTruncatedWithEllipsis()
    {
        var text = new string('x', JournalText.MaxLength + 50);

        var result = JournalText.Bound(text);

        result.Length.Should().Be(JournalText.MaxLength + 1);
        result.Should().EndWith("…");
    }

    [Theory]
    [InlineData("Authorization: Bearer abcdef123")]
    [InlineData("Idempotency-Key: 11111111-2222-3333-4444-555555555555")]
    public void Bound_SecretLikeHeaderText_IsKeptVerbatim(string text) =>
        // The substitution this class used to make blanked the one line the audience is asked
        // to read. Records now store the bytes as sent and as received.
        JournalText.Bound(text).Should().Be(text).And.NotContain("[redacted]");
}
