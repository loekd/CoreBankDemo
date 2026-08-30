using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.Messaging;

/// <summary>
/// Defensive/construction-time behavior of <see cref="MessageRepositoryBase{TMessage,TDbContext}"/>
/// (story 2.2) that sits outside the I/O matrix but still needs coverage:
/// constructor argument validation, the entity-configuration hook's own
/// argument validation, and null-message rejection on <c>StoreIfNewAsync</c>.
/// </summary>
public class MessageRepositoryBaseTests(PostgresContainerFixture fixture) : MessagingPostgresTestBase(fixture)
{
    [Fact]
    public void Constructor_rejects_null_dbContext()
    {
        var act = () => new TestInboxMessageRepository(null!, TimeProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("dbContext");
    }

    [Fact]
    public void Constructor_rejects_null_timeProvider()
    {
        using var context = CreateContext();
        var act = () => new TestInboxMessageRepository(context, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("timeProvider");
    }

    [Fact]
    public async Task StoreIfNewAsync_rejects_null_message()
    {
        await using var context = CreateContext();
        var repository = new TestInboxMessageRepository(context, TimeProvider);

        var act = async () => await repository.StoreIfNewAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("message");
    }

    [Fact]
    public void ConfigureDedupeIndex_rejects_null_builder()
    {
        var act = () => InboxMessageRepositoryBase<TestInboxMessage, TestMessagingDbContext>
            .ConfigureDedupeIndex(null!, "IdempotencyKey");

        act.Should().Throw<ArgumentNullException>().WithParameterName("builder");
    }

    [Fact]
    public void ConfigureDedupeIndex_rejects_empty_property_name_list()
    {
        var entityBuilder = new ModelBuilder().Entity<TestInboxMessage>();

        var act = () => InboxMessageRepositoryBase<TestInboxMessage, TestMessagingDbContext>
            .ConfigureDedupeIndex(entityBuilder);

        act.Should().Throw<ArgumentException>().WithParameterName("dedupePropertyNames");
    }

    [Fact]
    public void ConfigureDedupeIndex_rejects_null_property_name_array()
    {
        var entityBuilder = new ModelBuilder().Entity<TestInboxMessage>();

        var act = () => InboxMessageRepositoryBase<TestInboxMessage, TestMessagingDbContext>
            .ConfigureDedupeIndex(entityBuilder, (string[])null!);

        act.Should().Throw<ArgumentException>().WithParameterName("dedupePropertyNames");
    }

    [Fact]
    public void ConfigureDedupeIndex_rejects_name_that_is_not_a_public_instance_property()
    {
        var entityBuilder = new ModelBuilder().Entity<TestInboxMessage>();

        var act = () => InboxMessageRepositoryBase<TestInboxMessage, TestMessagingDbContext>
            .ConfigureDedupeIndex(entityBuilder, "NotAProperty");

        act.Should().Throw<ArgumentException>()
            .WithParameterName("dedupePropertyNames")
            .WithMessage("*NotAProperty*");
    }

    [Fact]
    public void ConfigureDedupeIndex_rejects_duplicate_property_names()
    {
        var entityBuilder = new ModelBuilder().Entity<TestOutboxEventMessage>();

        var act = () => OutboxMessageRepositoryBase<TestOutboxEventMessage, TestMessagingDbContext>
            .ConfigureDedupeIndex(
                entityBuilder,
                nameof(TestOutboxEventMessage.IdempotencyKey),
                nameof(TestOutboxEventMessage.IdempotencyKey));

        act.Should().Throw<ArgumentException>()
            .WithParameterName("dedupePropertyNames")
            .WithMessage("*IdempotencyKey*");
    }
}
