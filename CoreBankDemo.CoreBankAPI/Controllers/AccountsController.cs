using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CoreBankDemo.CoreBankAPI.Models;

namespace CoreBankDemo.CoreBankAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AccountsController : ControllerBase
{
    private readonly CoreBankDbContext _dbContext;

    public AccountsController(CoreBankDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpPost("validate")]
    public async Task<IActionResult> ValidateAccount([FromBody] AccountValidationRequest request)
    {
        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .ToList();
            return BadRequest(new { Errors = errors });
        }

        var account = await _dbContext.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == request.AccountNumber);

        var isValid = account != null && account.IsActive;

        var response = new AccountValidationResponse(
            request.AccountNumber,
            isValid,
            account?.AccountHolderName,
            account?.Balance
        );

        return Ok(response);
    }

    [HttpGet("{accountNumber}")]
    public async Task<IActionResult> GetAccountDetails(
        [FromRoute]
        [StringLength(34, MinimumLength = 15, ErrorMessage = "AccountNumber must be between 15 and 34 characters")]
        string accountNumber)
    {
        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .ToList();
            return BadRequest(new { Errors = errors });
        }

        var account = await _dbContext.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == accountNumber);

        if (account == null)
            return NotFound(new { Errors = new[] { $"Account {accountNumber} not found" } });

        var response = new AccountDetailsResponse(
            account.AccountNumber,
            account.AccountHolderName,
            account.Balance,
            account.Currency,
            account.IsActive,
            new DateTimeOffset(account.CreatedAt, TimeSpan.Zero),
            account.UpdatedAt.HasValue ? new DateTimeOffset(account.UpdatedAt.Value, TimeSpan.Zero) : null
        );

        return Ok(response);
    }
}
