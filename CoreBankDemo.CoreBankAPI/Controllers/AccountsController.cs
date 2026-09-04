using System.ComponentModel.DataAnnotations;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Models;
using Microsoft.AspNetCore.Mvc;

namespace CoreBankDemo.CoreBankAPI.Controllers;

/// <summary>
/// Account read-surface HTTP endpoints (spec-4-5). Thin by design (conventions
/// skill, AD-2): bind, check <see cref="ModelState"/>, call
/// <see cref="IAccountQueryHandler"/>, map its result to an
/// <see cref="IActionResult"/> — no lookup, validity computation, or response
/// assembly here; all of that lives in the handler.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AccountsController(IAccountQueryHandler handler) : ControllerBase
{
    [HttpPost("validate")]
    public async Task<IActionResult> ValidateAccount(
        [FromBody] AccountValidationRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
            return BadRequest(new { Errors = errors });
        }

        var response = await handler.ValidateAsync(request, cancellationToken);

        // Always 200 — legacy never 4xx's a "not valid" business outcome;
        // validity is reported in the body, not via status code.
        return Ok(response);
    }

    [HttpGet("{accountNumber}")]
    public async Task<IActionResult> GetAccountDetails(
        [FromRoute]
        [StringLength(34, MinimumLength = 15, ErrorMessage = "AccountNumber must be between 15 and 34 characters")]
        string accountNumber,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
            return BadRequest(new { Errors = errors });
        }

        var result = await handler.GetDetailsAsync(accountNumber, cancellationToken);

        if (!result.Found)
        {
            return NotFound(new { Errors = new[] { $"Account {accountNumber} not found" } });
        }

        return Ok(result.Response);
    }
}
