using CoreBankDemo.Messaging;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI;

/// <summary>
/// Idempotent startup seeding of the 3 demo accounts (FR-14; AD-4). Extracted
/// from <c>Program.cs</c> into a directly-unit-testable, constructor-injected
/// component per epic-4-context.md's Technical Decisions — <see cref="TimeProvider"/>
/// is injected (never <see cref="TimeProvider.System"/> directly), matching
/// this repo's established pattern (stories 3.1-3.4). Account numbers, holder
/// names, balances, and currency are external demo-narrative data and must be
/// preserved byte-for-byte.
/// <para>
/// AD-4: "never check-then-insert" — the emptiness check below is only a fast
/// path, not the correctness guarantee. Two instances racing to seed an empty
/// database (Aspire restart/scale-out) can both pass the check before either
/// commits; the loser's <see cref="DbUpdateException"/> is a genuine unique-PK
/// violation on <c>AccountNumber</c>, not a real failure — <see cref="UniqueViolation.IsUniqueViolation"/>
/// (the same provider-aware helper the kernel's <c>StoreIfNewAsync</c> uses)
/// classifies and swallows it, since the outcome (3 accounts seeded) is
/// identical either way.
/// </para>
/// </summary>
public class DemoAccountSeeder
{
    private readonly CoreBankDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public DemoAccountSeeder(CoreBankDbContext dbContext, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    /// <summary>No-ops if any account already exists; otherwise inserts exactly the 3 demo accounts.</summary>
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await _dbContext.Accounts.AnyAsync(cancellationToken))
            return;

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        _dbContext.Accounts.AddRange(
            new Account
            {
                AccountNumber = "NL91ABNA0417164300",
                AccountHolderName = "John Doe",
                Balance = 5000.00m,
                Currency = "EUR",
                IsActive = true,
                CreatedAt = now
            },
            new Account
            {
                AccountNumber = "NL20INGB0001234567",
                AccountHolderName = "Jane Smith",
                Balance = 10000.00m,
                Currency = "EUR",
                IsActive = true,
                CreatedAt = now
            },
            new Account
            {
                AccountNumber = "NL39RABO0300065264",
                AccountHolderName = "Bob Johnson",
                Balance = 2500.00m,
                Currency = "EUR",
                IsActive = true,
                CreatedAt = now
            });

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsUniqueViolation(ex))
        {
            // Lost a concurrent seed race — another instance already committed
            // the same 3 accounts. Identical outcome to a normal no-op.
        }
    }
}
