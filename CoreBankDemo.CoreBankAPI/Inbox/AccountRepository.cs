using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI.Inbox;

public record AccountValidationResult(bool IsValid, List<string> Errors);

public interface IAccountRepository
{
    Task<AccountValidationResult> ValidateTransactionRequestAsync(
        string fromAccount,
        string toAccount,
        decimal amount,
        string currency,
        CancellationToken cancellationToken);
}

public class AccountRepository(IServiceProvider serviceProvider) : IAccountRepository
{
    public async Task<AccountValidationResult> ValidateTransactionRequestAsync(
        string fromAccount,
        string toAccount,
        decimal amount,
        string currency,
        CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoreBankDbContext>();

        var from = await dbContext.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == fromAccount, cancellationToken);

        var to = await dbContext.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == toAccount, cancellationToken);

        var errors = new List<string>();

        if (from == null)
            errors.Add($"Source account {fromAccount} not found");
        else if (!from.IsActive)
            errors.Add($"Source account {fromAccount} is not active");
        else if (from.Balance < amount)
            errors.Add($"Insufficient funds. Available: {from.Balance} {from.Currency}, Required: {amount} {currency}");
        else if (from.Currency != currency)
            errors.Add($"Currency mismatch. Account currency: {from.Currency}, Transaction currency: {currency}");

        if (to == null)
            errors.Add($"Destination account {toAccount} not found");
        else if (!to.IsActive)
            errors.Add($"Destination account {toAccount} is not active");

        return new AccountValidationResult(errors.Count == 0, errors);
    }
}

