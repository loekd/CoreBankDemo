using StackExchange.Redis;
using Microsoft.Extensions.Logging;

namespace CoreBankDemo.ServiceDefaults;

public sealed class ProcessorStartGate : IProcessorStartGate, IProcessorStartGatePublisher
{
    public Task WaitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> HasReleaseGenerationAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task ReleaseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class RedisProcessorStartGate : IProcessorStartGate, IProcessorStartGatePublisher
{
    internal const string GenerationKey = "corebankdemo:processor-start:generation";
    internal const string ReleaseChannel = "corebankdemo:processor-start:release";
    internal const string ParticipantsKey = "corebankdemo:processor-start:participants";
    internal const string AdvanceAndPublishScript = """
        local generation = redis.call('INCR', KEYS[1])
        redis.call('PUBLISH', ARGV[1], tostring(generation))
        return generation
        """;

    private readonly IDatabase _database;
    private readonly ISubscriber _subscriber;
    private readonly int _expectedParticipants;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _releaseTimeout;
    private readonly ILogger<RedisProcessorStartGate>? _logger;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _subscriptionLock = new(1, 1);
    private readonly object _registrationLock = new();
    private Task? _registrationTask;
    private bool _subscribed;

    public RedisProcessorStartGate(
        IConnectionMultiplexer connectionMultiplexer,
        int expectedParticipants,
        TimeProvider timeProvider,
        TimeSpan releaseTimeout,
        ILogger<RedisProcessorStartGate>? logger = null)
        : this(
            connectionMultiplexer?.GetDatabase() ?? throw new ArgumentNullException(nameof(connectionMultiplexer)),
            connectionMultiplexer.GetSubscriber(),
            expectedParticipants,
            timeProvider,
            releaseTimeout,
            logger)
    {
    }

    internal RedisProcessorStartGate(
        IDatabase database,
        ISubscriber subscriber,
        int expectedParticipants,
        TimeProvider timeProvider,
        TimeSpan releaseTimeout,
        ILogger<RedisProcessorStartGate>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(subscriber);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedParticipants);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(releaseTimeout, TimeSpan.Zero);

        _database = database;
        _subscriber = subscriber;
        _expectedParticipants = expectedParticipants;
        _timeProvider = timeProvider;
        _releaseTimeout = releaseTimeout;
        _logger = logger;
    }

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        while (!_released.Task.IsCompleted)
        {
            try
            {
                await EnsureRegisteredAsync(cancellationToken).ConfigureAwait(false);
                await EnsureSubscribedAsync(cancellationToken).ConfigureAwait(false);
                await OpenFromCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);
                await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Processor start gate setup failed; retrying");
                await Task.Delay(TimeSpan.FromMilliseconds(100), _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (_expectedParticipants == 0)
        {
            throw new InvalidOperationException(
                "ProcessorStartGate:ExpectedParticipants must be positive for a release publisher.");
        }

        using var releaseTimeout = new CancellationTokenSource(_releaseTimeout, _timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, releaseTimeout.Token);
        var generation = 0L;

        try
        {
            var participants = await WaitForParticipantsAsync(timeout.Token).ConfigureAwait(false);
            var result = await _database.ScriptEvaluateAsync(
                AdvanceAndPublishScript,
                [GenerationKey],
                [ReleaseChannel]).WaitAsync(timeout.Token).ConfigureAwait(false);
            generation = long.Parse(result.ToString()!, System.Globalization.CultureInfo.InvariantCulture);

            while (!await EveryParticipantAcknowledgedAsync(
                       generation, participants, timeout.Token).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), _timeProvider, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Only some processor replicas acknowledged release generation {generation} within {_releaseTimeout}.");
        }
    }

    public async Task<bool> HasReleaseGenerationAsync(CancellationToken cancellationToken = default)
    {
        var generation = await _database.StringGetAsync(GenerationKey)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return long.TryParse(generation.ToString(), out var value) && value > 0;
    }

    private async Task EnsureSubscribedAsync(CancellationToken cancellationToken)
    {
        await _subscriptionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_subscribed)
            {
                return;
            }

            await _subscriber.SubscribeAsync(
                RedisChannel.Literal(ReleaseChannel),
                (channel, value) =>
                {
                    if (long.TryParse(value.ToString(), out var generation))
                    {
                        _ = AcknowledgeAndReleaseAsync(generation);
                    }
                }).WaitAsync(cancellationToken).ConfigureAwait(false);
            _subscribed = true;

        }
        finally
        {
            _subscriptionLock.Release();
        }
    }

    private async Task EnsureRegisteredAsync(CancellationToken cancellationToken)
    {
        Task registrationTask;
        lock (_registrationLock)
        {
            registrationTask = _registrationTask ??= _database.SetAddAsync(ParticipantsKey, _instanceId);
        }

        try
        {
            await registrationTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_registrationLock)
            {
                if (ReferenceEquals(_registrationTask, registrationTask))
                {
                    _registrationTask = null;
                }
            }

            throw;
        }
    }

    private async Task OpenFromCurrentGenerationAsync(CancellationToken cancellationToken)
    {
        var currentGeneration = await _database.StringGetAsync(GenerationKey)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (long.TryParse(currentGeneration.ToString(), out var generation) && generation > 0)
        {
            await AcknowledgeAndReleaseAsync(generation).ConfigureAwait(false);
        }
    }

    private async Task<RedisValue[]> WaitForParticipantsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var participants = await _database.SetMembersAsync(ParticipantsKey)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (participants.Length == _expectedParticipants)
            {
                return participants;
            }

            if (participants.Length > _expectedParticipants)
            {
                throw new InvalidOperationException(
                    $"Expected {_expectedParticipants} processor replicas but found {participants.Length}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> EveryParticipantAcknowledgedAsync(
        long generation,
        RedisValue[] participants,
        CancellationToken cancellationToken)
    {
        var acknowledgementKey = AcknowledgementKey(generation);
        var acknowledgements = await Task.WhenAll(participants.Select(
                participant => _database.SetContainsAsync(acknowledgementKey, participant)))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return acknowledgements.All(static acknowledged => acknowledged);
    }

    private async Task AcknowledgeAndReleaseAsync(long generation)
    {
        _released.TrySetResult();
        using var acknowledgementTimeout = new CancellationTokenSource(_releaseTimeout, _timeProvider);
        Exception? lastException = null;

        while (!acknowledgementTimeout.IsCancellationRequested)
        {
            try
            {
                await AcknowledgeAsync(generation).ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
            {
                lastException = exception;
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    _timeProvider,
                    acknowledgementTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger?.LogError(
            lastException,
            "Processor replica {InstanceId} could not acknowledge release generation {Generation}",
            _instanceId,
            generation);
    }

    private Task AcknowledgeAsync(long generation) =>
        _database.SetAddAsync(AcknowledgementKey(generation), _instanceId);

    private static RedisKey AcknowledgementKey(long generation) =>
        $"corebankdemo:processor-start:acknowledgements:{generation}";
}
