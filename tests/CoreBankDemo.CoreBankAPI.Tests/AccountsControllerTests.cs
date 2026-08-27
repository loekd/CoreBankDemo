using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI.Controllers;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace CoreBankDemo.CoreBankAPI.Tests;

/// <summary>
/// Thin controller tests (spec-4-5's code map): the <see cref="ModelState"/>-
/// invalid path for both actions, and delegation-to-handler outcome mapping,
/// against a mocked <see cref="IAccountQueryHandler"/>. No HTTP pipeline: the
/// controller is constructed directly with a manually-built
/// <see cref="ControllerContext"/> (mirrors <c>TransactionsControllerTests</c>).
/// </summary>
public class AccountsControllerTests
{
    private const string AccountNumber = "NL91ABNA0417164300";

    private readonly Mock<IAccountQueryHandler> _handler = new(MockBehavior.Strict);

    private AccountsController CreateController()
    {
        return new AccountsController(_handler.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    [Fact]
    public async Task ValidateAccount_returns_bad_request_with_all_errors_when_model_state_is_invalid()
    {
        var controller = CreateController();
        controller.ModelState.AddModelError("AccountNumber", "AccountNumber is required");

        var result = await controller.ValidateAccount(new AccountValidationRequest(AccountNumber), TestContext.Current.CancellationToken);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        GetErrors(badRequest.Value).Should().Equal("AccountNumber is required");

        _handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ValidateAccount_returns_200_ok_with_the_handlers_response_even_when_invalid()
    {
        var response = new AccountValidationResponse(AccountNumber, false, null, null);
        _handler.Setup(h => h.ValidateAsync(It.IsAny<AccountValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var controller = CreateController();

        var result = await controller.ValidateAccount(new AccountValidationRequest(AccountNumber), TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(response);
    }

    [Fact]
    public async Task ValidateAccount_returns_200_ok_with_the_handlers_response_when_valid()
    {
        var response = new AccountValidationResponse(AccountNumber, true, "Test Holder", 100m);
        _handler.Setup(h => h.ValidateAsync(It.IsAny<AccountValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var controller = CreateController();

        var result = await controller.ValidateAccount(new AccountValidationRequest(AccountNumber), TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(response);
    }

    [Fact]
    public async Task GetAccountDetails_returns_bad_request_with_all_errors_when_model_state_is_invalid()
    {
        var controller = CreateController();
        controller.ModelState.AddModelError("accountNumber", "AccountNumber must be between 15 and 34 characters");

        var result = await controller.GetAccountDetails("short", TestContext.Current.CancellationToken);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        GetErrors(badRequest.Value).Should().Equal("AccountNumber must be between 15 and 34 characters");

        _handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetAccountDetails_returns_not_found_when_the_handler_reports_not_found()
    {
        _handler.Setup(h => h.GetDetailsAsync(AccountNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountDetailsResult(false, null));

        var controller = CreateController();

        var result = await controller.GetAccountDetails(AccountNumber, TestContext.Current.CancellationToken);

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        GetErrors(notFound.Value).Should().Equal($"Account {AccountNumber} not found");
    }

    [Fact]
    public async Task GetAccountDetails_returns_ok_with_the_response_when_the_handler_reports_found()
    {
        var response = new AccountDetailsResponse(
            AccountNumber, "Test Holder", 100m, "EUR", true,
            DateTimeOffset.UtcNow, null);
        _handler.Setup(h => h.GetDetailsAsync(AccountNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountDetailsResult(true, response));

        var controller = CreateController();

        var result = await controller.GetAccountDetails(AccountNumber, TestContext.Current.CancellationToken);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(response);
    }

    private static IEnumerable<string> GetErrors(object? value)
    {
        var property = value!.GetType().GetProperty("Errors");
        property.Should().NotBeNull();
        return ((IEnumerable<string>)property!.GetValue(value)!).ToList();
    }
}
