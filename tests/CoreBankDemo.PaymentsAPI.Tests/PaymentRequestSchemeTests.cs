using System.ComponentModel.DataAnnotations;
using System.Reflection;
using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Models;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// Proves the additive <c>Scheme</c> field's closed-set validation and
/// default (spec: add-instant-payment-rail) directly against the real
/// <see cref="AllowedValuesAttribute"/> instance, mirroring
/// <c>AccountValidationAttributeTests</c>'s reflection-over-the-constructor-
/// parameter approach: attributes on a positional record parameter attach to
/// the parameter, not the synthesized property, so
/// <see cref="Validator.TryValidateObject"/> would not exercise them here.
/// </summary>
public class PaymentRequestSchemeTests
{
    [Fact]
    public void Scheme_defaults_to_standard_when_omitted()
    {
        var request = new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 10m, "EUR");

        request.Scheme.Should().Be(PaymentSchemes.Standard);
    }

    [Theory]
    [InlineData(PaymentSchemes.Standard, true)]
    [InlineData(PaymentSchemes.Instant, true)]
    [InlineData("express", false)]
    [InlineData("Standard", false)]
    [InlineData("", false)]
    public void Scheme_enforces_the_closed_standard_instant_set(string value, bool expectedValid)
    {
        var attribute = GetSchemeParameterAttribute<AllowedValuesAttribute>();

        var isValid = attribute.IsValid(value);

        isValid.Should().Be(expectedValid);
    }

    private static T GetSchemeParameterAttribute<T>() where T : ValidationAttribute
    {
        var ctor = typeof(PaymentRequest).GetConstructors().Single();
        var parameter = ctor.GetParameters().Single(p => p.Name == nameof(PaymentRequest.Scheme));
        return parameter.GetCustomAttribute<T>()!;
    }
}
