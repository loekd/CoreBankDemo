using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.CoreBankApi;

/// <summary>
/// Concurrent startup (ADR-014 replicated topology, proved on real PostgreSQL
/// per ADR-016): two replicas of the same service booting against one empty
/// database must both survive <c>EnsureCreatedAsync</c> plus demo-account
/// seeding, leaving the schema and the fixed demo data present exactly once.
/// The race is run for real here — the fixture does not serialize it away.
/// </summary>
public class ConcurrentSchemaInitializationTests(PostgresContainerFixture fixture)
    : PostgresDatabaseTestBase(fixture)
{
    // This class owns schema creation itself: that is the behavior under test.
    protected override Task InitializeSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [Fact]
    public async Task Two_replicas_initializing_and_seeding_one_empty_database_converge_to_one_schema_and_one_seed()
    {
        var ct = TestContext.Current.CancellationToken;

        async Task StartupAsync()
        {
            await using var context = CreateContext<CoreBankDbContext>();
            await context.Database.EnsureCreatedAsync(ct);
            await new DemoAccountSeeder(context, TimeProvider).SeedAsync(ct);
        }

        var act = () => Task.WhenAll(StartupAsync(), StartupAsync())
            .WaitAsync(PostgresContainerFixture.LockWaitTimeout, ct);

        await act.Should().NotThrowAsync(
            "a replica losing the create/seed race must tolerate it, not crash the service");

        await using var verification = CreateContext<CoreBankDbContext>();
        var accounts = await verification.Accounts.AsNoTracking().OrderBy(a => a.AccountNumber).ToListAsync(ct);
        accounts.Should().HaveCount(3);
        accounts.Select(a => a.AccountNumber).Should().OnlyHaveUniqueItems();
        accounts.Sum(a => a.Balance).Should().Be(17_500.00m);

        // The tables the application needs really exist, created exactly once.
        (await verification.InboxMessages.CountAsync(ct)).Should().Be(0);
        (await verification.MessagingOutboxMessages.CountAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task A_second_startup_against_an_already_initialized_database_is_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var first = CreateContext<CoreBankDbContext>())
        {
            (await first.Database.EnsureCreatedAsync(ct)).Should().BeTrue();
            await new DemoAccountSeeder(first, TimeProvider).SeedAsync(ct);
        }

        await using var second = CreateContext<CoreBankDbContext>();
        (await second.Database.EnsureCreatedAsync(ct))
            .Should().BeFalse("the schema already exists, so the second replica must not recreate it");
        await new DemoAccountSeeder(second, TimeProvider).SeedAsync(ct);

        (await second.Accounts.CountAsync(ct)).Should().Be(3);
    }
}
