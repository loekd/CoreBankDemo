using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.Messaging.Tests;

/// <summary>
/// Pins the kernel message contracts (story 2.1): id, dedupe identity
/// (IdempotencyKey, AD-4), PartitionId, Status, RetryCount, timestamps,
/// TraceParent/TraceState, LastError — per the epic-2 context member lists.
/// </summary>
public class MessageContractsTests
{
    private sealed class TestInboxMessage : IInboxMessage
    {
        public Guid Id { get; set; }
        public int PartitionId { get; set; }
        public string Status { get; set; } = MessageConstants.Status.Pending;
        public DateTime? ProcessedAt { get; set; }
        public int RetryCount { get; set; }
        public string? LastError { get; set; }
        public string? TraceParent { get; set; }
        public string? TraceState { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public DateTime ReceivedAt { get; set; }
    }

    private sealed class TestOutboxMessage : IOutboxMessage
    {
        public Guid Id { get; set; }
        public int PartitionId { get; set; }
        public string Status { get; set; } = MessageConstants.Status.Pending;
        public DateTime? ProcessedAt { get; set; }
        public int RetryCount { get; set; }
        public string? LastError { get; set; }
        public string? TraceParent { get; set; }
        public string? TraceState { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    [Fact]
    public void Inbox_and_outbox_contracts_both_derive_from_IMessage()
    {
        typeof(IMessage).IsAssignableFrom(typeof(IInboxMessage)).Should().BeTrue();
        typeof(IMessage).IsAssignableFrom(typeof(IOutboxMessage)).Should().BeTrue();
    }

    [Fact]
    public void IMessage_exposes_exactly_the_epic_context_member_list()
    {
        var properties = typeof(IMessage).GetProperties()
            .ToDictionary(p => p.Name, p => p.PropertyType);

        properties.Should().HaveCount(8);
        properties["Id"].Should().Be(typeof(Guid));
        properties["PartitionId"].Should().Be(typeof(int));
        properties["Status"].Should().Be(typeof(string));
        properties["ProcessedAt"].Should().Be(typeof(DateTime?));
        properties["RetryCount"].Should().Be(typeof(int));
        properties["LastError"].Should().Be(typeof(string));
        properties["TraceParent"].Should().Be(typeof(string));
        properties["TraceState"].Should().Be(typeof(string));
    }

    [Fact]
    public void IMessage_members_are_all_read_write()
    {
        typeof(IMessage).GetProperties()
            .Should().OnlyContain(p => p.CanRead && p.CanWrite);
    }

    [Fact]
    public void IInboxMessage_adds_dedupe_identity_and_received_timestamp()
    {
        var properties = typeof(IInboxMessage).GetProperties()
            .ToDictionary(p => p.Name, p => p.PropertyType);

        properties.Should().HaveCount(2, "inherited IMessage members live on the base interface");
        properties["IdempotencyKey"].Should().Be(typeof(string));
        properties["ReceivedAt"].Should().Be(typeof(DateTime));
    }

    [Fact]
    public void IOutboxMessage_adds_dedupe_identity_and_created_timestamp()
    {
        var properties = typeof(IOutboxMessage).GetProperties()
            .ToDictionary(p => p.Name, p => p.PropertyType);

        properties.Should().HaveCount(2, "inherited IMessage members live on the base interface");
        properties["IdempotencyKey"].Should().Be(typeof(string));
        properties["CreatedAt"].Should().Be(typeof(DateTime));
    }

    [Fact]
    public void Inbox_contract_round_trips_all_members()
    {
        var received = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
        IInboxMessage message = new TestInboxMessage
        {
            Id = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301"),
            IdempotencyKey = "NL91ABNA0417164300",
            PartitionId = PartitionHelper.GetPartitionId("NL91ABNA0417164300", 4),
            Status = MessageConstants.Status.Processing,
            RetryCount = 2,
            ReceivedAt = received,
            ProcessedAt = received.AddSeconds(1),
            LastError = "boom",
            TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            TraceState = "congo=t61rcWkgMzE",
        };

        message.Id.Should().Be(Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301"));
        message.IdempotencyKey.Should().Be("NL91ABNA0417164300");
        message.PartitionId.Should().Be(3);
        message.Status.Should().Be("Processing");
        message.RetryCount.Should().Be(2);
        message.ReceivedAt.Should().Be(received);
        message.ProcessedAt.Should().Be(received.AddSeconds(1));
        message.LastError.Should().Be("boom");
        message.TraceParent.Should().Be("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        message.TraceState.Should().Be("congo=t61rcWkgMzE");
    }

    [Fact]
    public void Outbox_contract_round_trips_all_members()
    {
        var created = new DateTime(2026, 8, 21, 11, 0, 0, DateTimeKind.Utc);
        IOutboxMessage message = new TestOutboxMessage
        {
            Id = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"),
            IdempotencyKey = "payment-key-001",
            PartitionId = PartitionHelper.GetPartitionId("payment-key-001", 4),
            Status = MessageConstants.Status.Completed,
            RetryCount = 0,
            CreatedAt = created,
            ProcessedAt = created.AddSeconds(2),
            LastError = null,
            TraceParent = null,
            TraceState = null,
        };

        message.Id.Should().Be(Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"));
        message.IdempotencyKey.Should().Be("payment-key-001");
        message.PartitionId.Should().Be(1);
        message.Status.Should().Be("Completed");
        message.RetryCount.Should().Be(0);
        message.CreatedAt.Should().Be(created);
        message.ProcessedAt.Should().Be(created.AddSeconds(2));
        message.LastError.Should().BeNull();
        message.TraceParent.Should().BeNull();
        message.TraceState.Should().BeNull();
    }
}
