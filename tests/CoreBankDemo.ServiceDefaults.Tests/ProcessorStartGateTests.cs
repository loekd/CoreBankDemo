using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests;

public class ProcessorStartGateTests
{
    [Fact]
    public async Task Default_gate_is_always_open_and_release_is_idempotent()
    {
        var gate = new ProcessorStartGate();

        await gate.WaitAsync(TestContext.Current.CancellationToken);
        await gate.ReleaseAsync(TestContext.Current.CancellationToken);
        await gate.ReleaseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Redis_gate_waits_for_the_broadcast_and_acknowledges_it()
    {
        var database = CreateDatabase();
        database.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        Action<RedisChannel, RedisValue>? handler = null;
        var subscriber = CreateSubscriber((_, callback) => handler = callback);
        var gate = CreateGate(database.Object, subscriber.Object);

        var wait = gate.WaitAsync(TestContext.Current.CancellationToken);
        await Task.Yield();
        wait.IsCompleted.Should().BeFalse();

        handler.Should().NotBeNull();
        handler!(RedisChannel.Literal(RedisProcessorStartGate.ReleaseChannel), 7L);
        await wait;

        database.Verify(db => db.SetAddAsync(
            It.Is<RedisKey>(key => key.ToString().EndsWith(":7", StringComparison.Ordinal)),
            It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task Redis_gate_opens_from_the_generation_marker_after_a_missed_broadcast()
    {
        var database = CreateDatabase();
        database.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(3L);
        var subscriber = CreateSubscriber((_, _) => { });
        var gate = CreateGate(database.Object, subscriber.Object);

        await gate.WaitAsync(TestContext.Current.CancellationToken);
        await gate.WaitAsync(TestContext.Current.CancellationToken);

        subscriber.Verify(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);
        database.Verify(db => db.SetAddAsync(
            It.Is<RedisKey>(key => key.ToString().EndsWith(":3", StringComparison.Ordinal)),
            It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task Publisher_waits_until_every_expected_replica_acknowledges()
    {
        var database = CreateDatabase();
        database.Setup(db => db.SetMembersAsync(
                RedisProcessorStartGate.ParticipantsKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync(["replica-a", "replica-b"]);
        database.Setup(db => db.ScriptEvaluateAsync(
                RedisProcessorStartGate.AdvanceAndPublishScript,
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)4L));
        var acknowledgementChecks = 0;
        database.Setup(db => db.SetContainsAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => Interlocked.Increment(ref acknowledgementChecks) > 2);
        var subscriber = CreateSubscriber((_, _) => { });
        var gate = CreateGate(database.Object, subscriber.Object, expectedParticipants: 2);

        await gate.ReleaseAsync(TestContext.Current.CancellationToken);

        database.Verify(db => db.SetContainsAsync(
            It.Is<RedisKey>(key => key.ToString().EndsWith(":4", StringComparison.Ordinal)),
            It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()), Times.Exactly(4));
        database.Verify(db => db.ScriptEvaluateAsync(
            RedisProcessorStartGate.AdvanceAndPublishScript,
            It.Is<RedisKey[]>(keys => keys.SequenceEqual(new RedisKey[] { RedisProcessorStartGate.GenerationKey })),
            It.Is<RedisValue[]>(values => values.SequenceEqual(new RedisValue[] { RedisProcessorStartGate.ReleaseChannel })),
            It.IsAny<CommandFlags>()), Times.Once);
        subscriber.Verify(s => s.PublishAsync(
            It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task Publisher_rejects_zero_expected_participants()
    {
        var gate = CreateGate(CreateDatabase().Object, CreateSubscriber((_, _) => { }).Object);

        var act = () => gate.ReleaseAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ExpectedParticipants*");
    }

    [Fact]
    public async Task Wait_retries_a_transient_registration_failure()
    {
        var database = CreateDatabase();
        database.SetupSequence(db => db.SetAddAsync(
                RedisProcessorStartGate.ParticipantsKey,
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "transient"))
            .ReturnsAsync(true);
        database.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        Action<RedisChannel, RedisValue>? handler = null;
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriber = CreateSubscriber((_, callback) =>
        {
            handler = callback;
            subscribed.TrySetResult();
        });
        var gate = CreateGate(database.Object, subscriber.Object);

        var wait = gate.WaitAsync(TestContext.Current.CancellationToken);
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        handler!(RedisChannel.Literal(RedisProcessorStartGate.ReleaseChannel), 1L);
        await wait;

        database.Verify(db => db.SetAddAsync(
            RedisProcessorStartGate.ParticipantsKey,
            It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Wait_rechecks_the_marker_after_a_transient_read_failure()
    {
        var database = CreateDatabase();
        database.SetupSequence(db => db.StringGetAsync(
                RedisProcessorStartGate.GenerationKey, It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "transient"))
            .ReturnsAsync(2L);
        var subscriber = CreateSubscriber((_, _) => { });
        var gate = CreateGate(database.Object, subscriber.Object);

        await gate.WaitAsync(TestContext.Current.CancellationToken);

        subscriber.Verify(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);
        database.Verify(db => db.StringGetAsync(
            RedisProcessorStartGate.GenerationKey, It.IsAny<CommandFlags>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Publisher_times_out_when_not_all_replicas_acknowledge()
    {
        var database = CreateDatabase();
        database.Setup(db => db.SetMembersAsync(
                RedisProcessorStartGate.ParticipantsKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync(["replica-a"]);
        database.Setup(db => db.ScriptEvaluateAsync(
                RedisProcessorStartGate.AdvanceAndPublishScript,
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)1L));
        database.Setup(db => db.SetContainsAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(false);
        var subscriber = CreateSubscriber((_, _) => { });
        var timeProvider = new FakeTimeProvider();
        var gate = new RedisProcessorStartGate(
            database.Object, subscriber.Object, 1, timeProvider, TimeSpan.FromSeconds(5));

        var release = gate.ReleaseAsync(TestContext.Current.CancellationToken);
        await Task.Yield();
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        var act = async () => await release;
        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Waiting_can_be_cancelled_without_a_release()
    {
        var database = CreateDatabase();
        database.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        var subscriber = CreateSubscriber((_, _) => { });
        var gate = CreateGate(database.Object, subscriber.Object);
        using var cancellation = new CancellationTokenSource();

        var wait = gate.WaitAsync(cancellation.Token);
        cancellation.Cancel();

        var act = async () => await wait;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static Mock<IDatabase> CreateDatabase()
    {
        var database = new Mock<IDatabase>();
        database.Setup(db => db.SetAddAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        return database;
    }

    private static Mock<ISubscriber> CreateSubscriber(
        Action<RedisChannel, Action<RedisChannel, RedisValue>> capture)
    {
        var subscriber = new Mock<ISubscriber>();
        subscriber.Setup(s => s.SubscribeAsync(
                It.IsAny<RedisChannel>(),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                (channel, callback, _) => capture(channel, callback))
            .Returns(Task.CompletedTask);
        return subscriber;
    }

    private static RedisProcessorStartGate CreateGate(
        IDatabase database,
        ISubscriber subscriber,
        int expectedParticipants = 0) =>
        new(database, subscriber, expectedParticipants, TimeProvider.System, TimeSpan.FromSeconds(30));
}
