using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.PaymentsAPI.Outbox;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

public sealed class OutboxRepositoryTests
{
    [Fact]
    public async Task StoreIfNewAsync_inserts_once_and_returns_false_for_the_duplicate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlite(connection).Options;
        await using var context = new PaymentsDbContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var repository = new OutboxRepository(context, TimeProvider.System);
        var winner = NewMessage("duplicate-key");
        var loser = NewMessage("duplicate-key");

        var inserted = await repository.StoreIfNewAsync(winner, TestContext.Current.CancellationToken);
        var duplicate = await repository.StoreIfNewAsync(loser, TestContext.Current.CancellationToken);
        var persisted = await repository.FindByIdempotencyKeyAsync(
            "duplicate-key", TestContext.Current.CancellationToken);

        inserted.Should().BeTrue();
        duplicate.Should().BeFalse();
        context.Entry(loser).State.Should().Be(EntityState.Detached);
        persisted.Should().BeSameAs(winner);
        (await context.OutboxMessages.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task FindByIdempotencyKeyAsync_returns_null_for_an_unknown_key()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlite(connection).Options;
        await using var context = new PaymentsDbContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var repository = new OutboxRepository(context, TimeProvider.System);

        var result = await repository.FindByIdempotencyKeyAsync(
            "missing", TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    private static OutboxMessage NewMessage(string key) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = key,
        TransactionId = key,
        FromAccount = "from",
        ToAccount = "to",
        Amount = 10m,
        Currency = "EUR",
        CreatedAt = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc)
    };
}
