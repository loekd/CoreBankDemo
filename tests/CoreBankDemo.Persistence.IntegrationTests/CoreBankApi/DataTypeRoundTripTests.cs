using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.CoreBankApi;

/// <summary>
/// Data-type fidelity (ADR-016): money and timestamps must survive a real
/// PostgreSQL round trip with production precision and normalization. A
/// mapping that merely "looked fine" against a lighter engine — losing scale on
/// <see cref="decimal"/>, or silently shifting a <see cref="DateTimeOffset"/> —
/// fails here rather than in production.
/// </summary>
public class DataTypeRoundTripTests(PostgresContainerFixture fixture)
    : CoreBankApiPostgresTestBase(fixture)
{
    [Theory]
    [InlineData("0.01")]
    [InlineData("12.34")]
    [InlineData("-12.34")]
    [InlineData("10000000.00")]
    [InlineData("99999999999999.99")]
    public async Task Decimal_amounts_round_trip_with_full_precision_and_scale(string literal)
    {
        var ct = TestContext.Current.CancellationToken;
        var amount = decimal.Parse(literal, System.Globalization.CultureInfo.InvariantCulture);

        await using (var writeContext = CreateContext())
        {
            writeContext.InboxMessages.Add(NewInboxMessage("decimal-" + literal, amount));
            await writeContext.SaveChangesAsync(ct);
        }

        await using var readContext = CreateContext();
        var persisted = await readContext.InboxMessages
            .AsNoTracking()
            .SingleAsync(m => m.IdempotencyKey == "decimal-" + literal, ct);

        persisted.Amount.Should().Be(amount);
        persisted.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be(literal);
    }

    [Fact]
    public async Task Balances_do_not_lose_cents_to_a_floating_point_column_type()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var writeContext = CreateContext())
        {
            writeContext.Accounts.Add(new Account
            {
                AccountNumber = "NL91ABNA0417164300",
                AccountHolderName = "Precision Holder",
                Balance = 10_000_000.07m,
                Currency = "EUR",
                IsActive = true,
                CreatedAt = TimeProvider.GetUtcNow().UtcDateTime
            });
            await writeContext.SaveChangesAsync(ct);
        }

        await using var readContext = CreateContext();
        var account = await readContext.Accounts.AsNoTracking().SingleAsync(ct);
        account.Balance.Should().Be(10_000_000.07m);

        // And the column really is exact numeric, not a float type.
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT data_type FROM information_schema.columns
            WHERE table_name = 'Accounts' AND column_name = 'Balance'
            """;
        (await command.ExecuteScalarAsync(ct)).Should().Be("numeric");
    }

    [Fact]
    public async Task Utc_timestamps_round_trip_unchanged_and_keep_their_utc_kind()
    {
        var ct = TestContext.Current.CancellationToken;
        var receivedAt = new DateTime(2026, 8, 29, 23, 59, 58, 765, DateTimeKind.Utc).AddTicks(4321);

        await using (var writeContext = CreateContext())
        {
            var message = NewInboxMessage("timestamp-round-trip", 1m);
            message.ReceivedAt = receivedAt;
            message.ProcessedAt = receivedAt;
            writeContext.InboxMessages.Add(message);
            await writeContext.SaveChangesAsync(ct);
        }

        await using var readContext = CreateContext();
        var persisted = await readContext.InboxMessages
            .AsNoTracking()
            .SingleAsync(m => m.IdempotencyKey == "timestamp-round-trip", ct);

        // PostgreSQL timestamps have microsecond resolution, so the sub-tick
        // remainder is the only thing production may lose — never the offset,
        // never the kind.
        persisted.ReceivedAt.Kind.Should().Be(DateTimeKind.Utc);
        persisted.ReceivedAt.Should().BeCloseTo(receivedAt, TimeSpan.FromMicroseconds(1));
        persisted.ProcessedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
        persisted.ProcessedAt.Value.Should().BeCloseTo(receivedAt, TimeSpan.FromMicroseconds(1));
    }

    [Fact]
    public async Task DateTimeOffset_valued_event_times_round_trip_without_shifting_the_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var occurredAt = new DateTimeOffset(2026, 8, 29, 14, 30, 15, TimeSpan.FromHours(2));

        await using (var writeContext = CreateContext())
        {
            writeContext.MessagingOutboxMessages.Add(new MessagingOutboxMessage
            {
                Id = Guid.NewGuid(),
                PartitionId = 0,
                IdempotencyKey = "offset-round-trip",
                TransactionId = "offset-round-trip",
                Status = MessageConstants.Status.Pending,
                EventType = "com.corebank.transaction.completed",
                EventSource = "https://corebank-api/transactions",
                AccountNumber = "NL91ABNA0417164300",
                ToAccount = "NL20INGB0001234567",
                Amount = 12.34m,
                Currency = "EUR",
                TransactionStatus = MessageConstants.Status.Completed,
                CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
                EventOccurredAt = occurredAt.UtcDateTime
            });
            await writeContext.SaveChangesAsync(ct);
        }

        await using var readContext = CreateContext();
        var persisted = await readContext.MessagingOutboxMessages.AsNoTracking().SingleAsync(ct);

        new DateTimeOffset(persisted.EventOccurredAt, TimeSpan.Zero)
            .Should().Be(occurredAt.ToUniversalTime());
        persisted.EventOccurredAt.Should().Be(occurredAt.UtcDateTime);
    }

    [Fact]
    public async Task Max_length_columns_reject_over_length_values_at_the_database()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = CreateContext();
        var message = NewInboxMessage(new string('k', 101), 1m);
        context.InboxMessages.Add(message);

        var act = async () => await context.SaveChangesAsync(ct);

        // The declared MaxLength(100) is a real varchar(100) in PostgreSQL, not
        // an advisory annotation.
        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        thrown.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.StringDataRightTruncation);
    }

    private InboxMessage NewInboxMessage(string idempotencyKey, decimal amount) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = idempotencyKey,
        TransactionId = "txn-round-trip",
        FromAccount = "NL91ABNA0417164300",
        ToAccount = "NL20INGB0001234567",
        Amount = amount,
        Currency = "EUR",
        PartitionId = 0,
        Status = MessageConstants.Status.Pending,
        ReceivedAt = TimeProvider.GetUtcNow().UtcDateTime
    };
}
