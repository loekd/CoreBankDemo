using System.ComponentModel.DataAnnotations;
using System.Reflection;
using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI.Controllers;
using CoreBankDemo.CoreBankAPI.Models;
using Xunit;

namespace CoreBankDemo.CoreBankAPI.Tests;

/// <summary>
/// Proves the <c>[StringLength(34, MinimumLength = 15)]</c> boundary on both
/// account-number entry points is actually configured correctly, by invoking
/// the real <see cref="ValidationAttribute"/> instances directly via
/// reflection rather than hand-faking <c>ModelState</c> as
/// <c>AccountsControllerTests</c> does. Review finding (blind-hunter +
/// edge-case-hunter, convergent): nothing else in the suite exercises these
/// attributes at their actual boundary values, so an off-by-one edit (e.g.
/// <c>MinimumLength</c> drifting from 15) would pass CI unnoticed.
/// Reflection is over the primary constructor *parameter*, not the
/// synthesized property: C# attaches attributes written on a positional
/// record parameter to the parameter itself once the attribute's
/// <see cref="AttributeUsageAttribute"/> allows <c>AttributeTargets.Parameter</c>
/// (true for <see cref="RequiredAttribute"/>/<see cref="StringLengthAttribute"/>
/// since .NET 7's record-validation support) — confirmed empirically the
/// property carries no validation attributes at all, so
/// <see cref="Validator.TryValidateObject"/> (which reflects over properties)
/// would silently report every value as valid and is not a faithful test
/// here; ASP.NET Core's own model metadata provider does unify parameter and
/// property metadata for records, which is why the real HTTP pipeline
/// enforces this correctly (verified separately by blind-hunter's live probe).
/// </summary>
public class AccountValidationAttributeTests
{
    [Theory]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(34, true)]
    [InlineData(35, false)]
    public void AccountValidationRequest_AccountNumber_enforces_the_15_to_34_length_boundary(int length, bool expectedValid)
    {
        var attribute = GetAccountNumberParameterAttribute<StringLengthAttribute>();

        var isValid = attribute.IsValid(new string('A', length));

        isValid.Should().Be(expectedValid);
    }

    [Fact]
    public void AccountValidationRequest_AccountNumber_rejects_a_missing_value()
    {
        var attribute = GetAccountNumberParameterAttribute<RequiredAttribute>();

        var isValid = attribute.IsValid(null);

        isValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(34, true)]
    [InlineData(35, false)]
    public void GetAccountDetails_route_parameter_enforces_the_same_15_to_34_length_boundary(int length, bool expectedValid)
    {
        var attribute = GetRouteAccountNumberParameterAttribute<StringLengthAttribute>();

        var isValid = attribute.IsValid(new string('A', length));

        isValid.Should().Be(expectedValid);
    }

    private static T GetAccountNumberParameterAttribute<T>() where T : ValidationAttribute
    {
        var ctor = typeof(AccountValidationRequest).GetConstructors().Single();
        var parameter = ctor.GetParameters().Single(p => p.Name == nameof(AccountValidationRequest.AccountNumber));
        return parameter.GetCustomAttribute<T>()!;
    }

    private static T GetRouteAccountNumberParameterAttribute<T>() where T : ValidationAttribute
    {
        var method = typeof(AccountsController).GetMethod(nameof(AccountsController.GetAccountDetails))!;
        var parameter = method.GetParameters().Single(p => p.Name == "accountNumber");
        return parameter.GetCustomAttribute<T>()!;
    }
}
