using CoreBankDemo.CoreBankAPI.Models;

namespace CoreBankDemo.CoreBankAPI.Inbox;

/// <summary>
/// Result of <see cref="AccountQueryHandler.GetDetailsAsync"/>.
/// <see cref="Response"/> is populated only when <see cref="Found"/> is
/// <see langword="true"/>.
/// </summary>
public sealed record AccountDetailsResult(bool Found, AccountDetailsResponse? Response);

/// <summary>
/// Pure business logic for the account read surface (spec-4-5; AD-2): looks
/// up an <see cref="Account"/> via the existing <see cref="IAccountRepository"/>
/// and assembles either an <see cref="AccountValidationResponse"/> or an
/// <see cref="AccountDetailsResult"/>. Read-only — never calls
/// <see cref="IAccountRepository.LockForUpdateAsync"/>, which stays
/// execution-only (story 4.3). Returns domain types only, never
/// <see cref="Microsoft.AspNetCore.Mvc.IActionResult"/> (conventions skill:
/// controllers stay thin, business logic lives here). Public (unlike the
/// internal <see cref="IAccountRepository"/>): <see cref="Controllers.AccountsController"/>
/// is a public ASP.NET Core controller (MVC only discovers public controller
/// types), so its constructor-injected dependency cannot be less accessible
/// than the class itself (mirrors <see cref="ITransactionIntakeHandler"/>).
/// </summary>
public interface IAccountQueryHandler
{
    Task<AccountValidationResponse> ValidateAsync(AccountValidationRequest request, CancellationToken cancellationToken);

    Task<AccountDetailsResult> GetDetailsAsync(string accountNumber, CancellationToken cancellationToken);
}

internal sealed class AccountQueryHandler(IAccountRepository repository) : IAccountQueryHandler
{
    public async Task<AccountValidationResponse> ValidateAsync(AccountValidationRequest request, CancellationToken cancellationToken)
    {
        var account = await repository.FindByAccountNumberAsync(request.AccountNumber, cancellationToken)
            .ConfigureAwait(false);

        var isValid = account is not null && account.IsActive;

        return new AccountValidationResponse(
            request.AccountNumber,
            isValid,
            account?.AccountHolderName,
            account?.Balance);
    }

    public async Task<AccountDetailsResult> GetDetailsAsync(string accountNumber, CancellationToken cancellationToken)
    {
        var account = await repository.FindByAccountNumberAsync(accountNumber, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            return new AccountDetailsResult(false, null);
        }

        var response = new AccountDetailsResponse(
            account.AccountNumber,
            account.AccountHolderName,
            account.Balance,
            account.Currency,
            account.IsActive,
            new DateTimeOffset(account.CreatedAt, TimeSpan.Zero),
            account.UpdatedAt.HasValue ? new DateTimeOffset(account.UpdatedAt.Value, TimeSpan.Zero) : null);

        return new AccountDetailsResult(true, response);
    }
}
