using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application;

public class PaymentInputValidatorTests
{
    [Fact]
    public void Validate_ValidGeneratedSubmission_HasNoErrors()
    {
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Generated,
            "key");

        PaymentInputValidator.Validate(submission).Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "NL20INGB0001234567")]
    [InlineData("short", "NL20INGB0001234567")]
    [InlineData("NL91ABNA0417164300", "short")]
    public void Validate_InvalidAccountLengths_ReportErrors(string from, string to)
    {
        var submission = new PaymentSubmission(
            new PaymentRequest(from, to, 1m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Generated,
            "key");

        PaymentInputValidator.Validate(submission).Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_OmittedModeWithKey_IsRejected()
    {
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Omitted,
            "must-not-be-sent");

        PaymentInputValidator.Validate(submission).Should().ContainSingle(error => error.Contains("must not send"));
    }

    [Fact]
    public void Validate_OverlongSuppliedKey_IsRejected()
    {
        var submission = new PaymentSubmission(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 1m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Supplied,
            new string('k', 101));

        PaymentInputValidator.Validate(submission).Should().ContainSingle(error => error.Contains("100"));
    }
}
