using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.CoreBankApi;

/// <summary>
/// <see cref="DemoAccountSeeder"/> (spec-4-1 I/O matrix): empty database gets
/// exactly the 3 demo accounts inserted byte-for-byte; a non-empty database
/// (including a second seeder run) is always a no-op.
/// </summary>
public class DemoAccountSeederTests(PostgresContainerFixture fixture) : CoreBankApiPostgresTestBase(fixture)
{
    [Fact]
    public void Constructor_rejects_null_dbContext()
    {
        var act = () => new DemoAccountSeeder(null!, TimeProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("dbContext");
    }

    [Fact]
    public void Constructor_rejects_null_timeProvider()
    {
        using var context = CreateContext();
        var act = () => new DemoAccountSeeder(context, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("timeProvider");
    }

    [Fact]
    public async Task Empty_database_gets_exactly_the_3_demo_accounts()
    {
        await using var context = CreateContext();
        var seeder = new DemoAccountSeeder(context, TimeProvider);

        await seeder.SeedAsync(TestContext.Current.CancellationToken);

        var accounts = await context.Accounts.OrderBy(a => a.AccountNumber).ToListAsync(TestContext.Current.CancellationToken);
        accounts.Should().HaveCount(3);

        var johnDoe = accounts.Single(a => a.AccountNumber == "NL91ABNA0417164300");
        johnDoe.AccountHolderName.Should().Be("John Doe");
        johnDoe.Balance.Should().Be(5000.00m);
        johnDoe.Currency.Should().Be("EUR");
        johnDoe.IsActive.Should().BeTrue();
        johnDoe.CreatedAt.Should().Be(TimeProvider.GetUtcNow().UtcDateTime);

        var janeSmith = accounts.Single(a => a.AccountNumber == "NL20INGB0001234567");
        janeSmith.AccountHolderName.Should().Be("Jane Smith");
        janeSmith.Balance.Should().Be(10000.00m);
        janeSmith.Currency.Should().Be("EUR");
        janeSmith.IsActive.Should().BeTrue();

        var bobJohnson = accounts.Single(a => a.AccountNumber == "NL39RABO0300065264");
        bobJohnson.AccountHolderName.Should().Be("Bob Johnson");
        bobJohnson.Balance.Should().Be(2500.00m);
        bobJohnson.Currency.Should().Be("EUR");
        bobJohnson.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Non_empty_database_is_a_no_op()
    {
        await using var context = CreateContext();
        context.Accounts.Add(new Account
        {
            AccountNumber = "NL00EXISTING0000000",
            AccountHolderName = "Pre-existing Holder",
            Balance = 1m,
            Currency = "EUR",
            IsActive = true,
            CreatedAt = TimeProvider.GetUtcNow().UtcDateTime
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var seeder = new DemoAccountSeeder(context, TimeProvider);
        await seeder.SeedAsync(TestContext.Current.CancellationToken);

        var accounts = await context.Accounts.ToListAsync(TestContext.Current.CancellationToken);
        accounts.Should().ContainSingle();
        accounts.Single().AccountNumber.Should().Be("NL00EXISTING0000000");
    }

    [Fact]
    public async Task Calling_twice_in_sequence_stays_at_exactly_3_accounts()
    {
        await using var context = CreateContext();
        var seeder = new DemoAccountSeeder(context, TimeProvider);

        await seeder.SeedAsync(TestContext.Current.CancellationToken);
        await seeder.SeedAsync(TestContext.Current.CancellationToken);

        var count = await context.Accounts.CountAsync(TestContext.Current.CancellationToken);
        count.Should().Be(3);
    }

    /// <summary>
    /// AD-4 ("never check-then-insert"): two instances racing to seed an empty
    /// database — e.g. an Aspire restart or scale-out — must never crash. Runs
    /// two real <see cref="DemoAccountSeeder"/>s concurrently, on independent
    /// connections, against the same PostgreSQL database; the loser's unique-PK
    /// violation on <c>AccountNumber</c> must be swallowed, not propagated.
    /// </summary>
    [Fact]
    public async Task Two_seeders_racing_against_the_same_empty_database_never_throw_and_converge_to_3_accounts()
    {
        await using var contextA = CreateContext();
        await using var contextB = CreateContext();
        var seederA = new DemoAccountSeeder(contextA, TimeProvider);
        var seederB = new DemoAccountSeeder(contextB, TimeProvider);

        var act = () => Task.WhenAll(
            seederA.SeedAsync(TestContext.Current.CancellationToken),
            seederB.SeedAsync(TestContext.Current.CancellationToken));

        await act.Should().NotThrowAsync();

        await using var verifyContext = CreateContext();
        var count = await verifyContext.Accounts.CountAsync(TestContext.Current.CancellationToken);
        count.Should().Be(3);
    }
}
