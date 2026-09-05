using System.Text.Json;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;
using Dapr.Messaging.PublishSubscribe;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>
/// Subscribes to <c>transaction-events</c> through a <c>daprd</c> sidecar this console spawns
/// and owns.
/// </summary>
/// <remarks>
/// <para>
/// The subscription is a <b>streaming</b> one: the console dials out to its own sidecar over
/// gRPC and receives messages on that stream, so it hosts no inbound listener and opens no
/// port. Its app-id (<see cref="AppId"/>) is its own, which is what makes this a fan-out
/// rather than a diversion — Dapr derives the Redis consumer group from the app-id, so
/// PaymentsAPI's <c>payments-api</c>-scoped subscription keeps receiving every event.
/// </para>
/// <para>
/// Nothing here is added to a banking service: the component YAML is read as-is, and the
/// checked-in Subscription manifest is <c>scopes: [payments-api]</c>, which this sidecar parses
/// and correctly ignores.
/// </para>
/// </remarks>
public sealed class DaprOutcomeFeed : IOutcomeFeed, IAsyncDisposable
{
    /// <summary>
    /// Distinct from every banking app-id on purpose. Sharing one would share the consumer
    /// group and steal deliveries from PaymentsAPI — the one thing this feature must not do.
    /// </summary>
    internal const string AppId = "demorunner-console";

    /// <summary>
    /// Where the sidecar's three ports are searched for. High and contiguous so a failed scan
    /// is obvious, and every one of them is probed rather than assumed free.
    /// </summary>
    internal const int PortSearchStart = 53100;
    internal const int PortSearchEnd = 53400;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan SidecarWatchInterval = TimeSpan.FromSeconds(1);

    private readonly string _repositoryRoot;
    private readonly IEnvironmentProbe _probe;
    private readonly Func<IDaprSidecar> _sidecarFactory;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IDaprSidecar? _sidecar;
    private IAsyncDisposable? _subscription;
    private DaprPublishSubscribeClient? _client;
    private CancellationTokenSource? _watchdog;

    /// <summary>
    /// Owns the stream's lifetime. The caller's token starts the subscription; it must not also
    /// end it, or one future caller whose command completes would tear down a feed that is
    /// meant to outlive that command.
    /// </summary>
    private CancellationTokenSource? _streamCancellation;

    /// <summary>
    /// The last per-message handler failure, surfaced in the next published status. A message
    /// this console could not digest is a fact worth stating, but it is never a stream fault.
    /// </summary>
    private string? _lastHandlerError;
    private int _handlerErrorCount;

    private OutcomeFeedStatus _status = OutcomeFeedStatus.NotStarted;

    public DaprOutcomeFeed(string repositoryRoot, IEnvironmentProbe probe, TimeProvider time)
        : this(repositoryRoot, probe, time, () => new DaprSidecarProcess())
    {
    }

    public DaprOutcomeFeed(
        string repositoryRoot,
        IEnvironmentProbe probe,
        TimeProvider time,
        Func<IDaprSidecar> sidecarFactory)
    {
        _repositoryRoot = repositoryRoot;
        _probe = probe;
        _time = time;
        _sidecarFactory = sidecarFactory;
    }

    public event Action<OutcomeEvent>? EventReceived;

    public event Action<OutcomeFeedStatus>? StatusChanged;

    private OutcomeFeedStatus Status => Volatile.Read(ref _status);

    public async Task<OutcomeFeedStatus> StartAsync(TopologyProfile profile, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // B7: a failed reconnect must not erase when the feed was lost, or the reconnect
            // that eventually works would report no gap at all.
            var lostAt = Status.LostAt;
            await TearDownAsync(CancellationToken.None);

            if (profile == TopologyProfile.None)
            {
                return PublishUnavailable(
                    "No topology is active, so there is no broker to listen to.",
                    lostAt);
            }

            var componentsPath = Path.GetFullPath(ProfileRegistry.DaprComponentsDirectory(_repositoryRoot, profile));
            if (!Directory.Exists(componentsPath))
            {
                return PublishUnavailable(
                    $"The Dapr components directory {componentsPath} does not exist.",
                    lostAt);
            }

            // B5: the probe frees a port and daprd binds it a moment later, so another process
            // can win that race. One retry with a fresh allocation is cheap and turns a
            // permanently unavailable feed back into a working one.
            DaprSidecarStartResult? started = null;
            IDaprSidecar? sidecar = null;
            (int Grpc, int Http, int Metrics, int InternalGrpc)? ports = null;
            // Ports already handed to a sidecar that lost the bind race. Retrying with the same
            // four would lose it again in exactly the same way.
            var tried = new HashSet<int>();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                ports = await AllocatePortsAsync(tried, ct);
                if (ports is null)
                {
                    return PublishUnavailable(
                        $"No four free ports were found between {PortSearchStart} and {PortSearchEnd} for the "
                        + "console's own Dapr sidecar.",
                        lostAt);
                }

                sidecar = _sidecarFactory();
                // B4: owned before it is started, so a throwing start cannot leave an orphan
                // that teardown does not know about.
                _sidecar = sidecar;
                try
                {
                    started = await sidecar.StartAsync(
                        new DaprSidecarLaunch(
                            AppId,
                            componentsPath,
                            ports.Value.Grpc,
                            ports.Value.Http,
                            ports.Value.Metrics,
                            ports.Value.InternalGrpc),
                        ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await TearDownAsync(CancellationToken.None);
                    return PublishUnavailable($"The console's Dapr sidecar could not be started: {ex.Message}", lostAt);
                }

                if (started.Succeeded || !LooksLikeAPortConflict(started.Detail))
                {
                    break;
                }

                tried.Add(ports.Value.Grpc);
                tried.Add(ports.Value.Http);
                tried.Add(ports.Value.Metrics);
                tried.Add(ports.Value.InternalGrpc);
                await TearDownAsync(CancellationToken.None);
            }

            if (started is null || !started.Succeeded || sidecar is null || ports is null)
            {
                var detail = started?.Detail ?? "The console's Dapr sidecar did not start.";
                var output = sidecar?.RecentOutput ?? string.Empty;
                await TearDownAsync(CancellationToken.None);
                return PublishUnavailable(
                    output.Length > 0 ? $"{detail} {output}" : detail,
                    lostAt);
            }

            _streamCancellation = new CancellationTokenSource();
            try
            {
                _client = new DaprPublishSubscribeClientBuilder()
                    .UseGrpcEndpoint($"http://127.0.0.1:{ports.Value.Grpc}")
                    .Build();
                _subscription = await _client.SubscribeAsync(
                    OutcomeEventTypes.PubSubComponent,
                    OutcomeEventTypes.Topic,
                    new DaprSubscriptionOptions(new MessageHandlingPolicy(
                        TimeSpan.FromSeconds(10),
                        // A message this console cannot parse is dropped, never retried: the
                        // console is a read-only observer and must not make the broker redeliver
                        // on its behalf.
                        TopicResponseAction.Drop))
                    {
                        ErrorHandler = error =>
                        {
                            OnSubscriptionFault(error.Message);
                            return Task.CompletedTask;
                        },
                    },
                    HandleAsync,
                    // B8: the stream outlives whatever command started it.
                    _streamCancellation.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var output = sidecar.RecentOutput;
                await TearDownAsync(CancellationToken.None);
                return PublishUnavailable(
                    $"The console's Dapr sidecar started but the subscription to "
                    + $"{OutcomeEventTypes.Topic} failed: {ex.Message} {output}".TrimEnd(),
                    lostAt);
            }

            var now = _time.GetUtcNow();
            var established = new OutcomeFeedStatus(
                OutcomeFeedState.Listening,
                ListeningSince: now,
                // A reconnect stamps the window this console did not observe. It is never
                // back-filled: events broadcast while nobody was listening are gone from view.
                GapStart: lostAt,
                GapEnd: lostAt is null ? null : now,
                Detail: started.Detail);
            _lastHandlerError = null;
            _handlerErrorCount = 0;
            StartSidecarWatchdog();
            return Publish(established);
        }
        finally
        {
            _gate.Release();
        }
    }

    private OutcomeFeedStatus PublishUnavailable(string detail, DateTimeOffset? lostAt) =>
        Publish(new OutcomeFeedStatus(
            OutcomeFeedState.Unavailable,
            LostAt: lostAt,
            Detail: WithHandlerErrors(detail)));

    /// <summary>
    /// Whether a failed start reads like something else already holds the port. Matched on the
    /// sidecar's own words rather than an exit code, because daprd reports this in its log.
    /// </summary>
    private static bool LooksLikeAPortConflict(string detail) =>
        detail.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("bind:", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("listen tcp", StringComparison.OrdinalIgnoreCase);

    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await TearDownAsync(ct);
            Publish(OutcomeFeedStatus.NotStarted);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Taken before tearing down: a Start or Stop still in flight owns the same sidecar and
        // subscription fields, and disposing the gate under one of them is worse than waiting.
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await TearDownAsync(CancellationToken.None);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    /// <summary>
    /// Turns one delivered message into an <see cref="OutcomeEvent"/>. The CloudEvent type is
    /// taken verbatim from the envelope; an event this console does not know is acknowledged
    /// and dropped rather than retried, because retrying would make the console's parsing
    /// problem the broker's problem.
    /// </summary>
    internal static OutcomeEvent? TryParse(string? eventType, ReadOnlySpan<byte> data)
    {
        try
        {
            return eventType switch
            {
                OutcomeEventTypes.TransactionCompleted =>
                    Deserialize<TransactionCompletedWireEvent>(data) is { TransactionId.Length: > 0 } completed
                        ? OutcomeEvent.From(completed)
                        : null,
                OutcomeEventTypes.TransactionFailed =>
                    Deserialize<TransactionFailedWireEvent>(data) is { TransactionId.Length: > 0 } failed
                        ? OutcomeEvent.From(failed)
                        : null,
                OutcomeEventTypes.BalanceUpdated =>
                    Deserialize<BalanceUpdatedWireEvent>(data) is { TransactionId.Length: > 0 } balance
                        ? OutcomeEvent.From(balance)
                        : null,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T? Deserialize<T>(ReadOnlySpan<byte> data) => JsonSerializer.Deserialize<T>(data, Json);

    private Task<TopicResponseAction> HandleAsync(TopicMessage message, CancellationToken ct)
    {
        var parsed = TryParse(message.Type, message.Data.Span);
        if (parsed is not null)
        {
            try
            {
                EventReceived?.Invoke(parsed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // B1: the subscriber does real work on this thread. Letting one message's
                // failure escape reaches the subscription's ErrorHandler, which treats it as
                // the stream dying and re-labels every outstanding payment "Outcome unknown" --
                // withdrawing true claims about healthy payments because of one bad message.
                // A per-message failure is recorded and the stream carries on.
                Interlocked.Increment(ref _handlerErrorCount);
                _lastHandlerError = $"{parsed.EventType} for {parsed.TransactionId}: {ex.Message}";
            }
        }

        // Always Success: the console is a read-only listener with its own consumer group, and
        // a Retry here would only make its own sidecar redeliver to itself.
        return Task.FromResult(TopicResponseAction.Success);
    }

    /// <summary>
    /// Appends any per-message failures to a status detail, so messages this console could not
    /// digest are stated rather than silently absent from the feed.
    /// </summary>
    private string WithHandlerErrors(string detail)
    {
        var count = Volatile.Read(ref _handlerErrorCount);
        if (count == 0)
        {
            return detail;
        }

        return $"{detail} {count} message{(count == 1 ? string.Empty : "s")} could not be handled; "
            + $"last: {_lastHandlerError}".TrimEnd();
    }

    private void OnSubscriptionFault(string detail)
    {
        var current = Status;
        if (current.State != OutcomeFeedState.Listening)
        {
            return;
        }

        Publish(new OutcomeFeedStatus(
            OutcomeFeedState.Lost,
            ListeningSince: current.ListeningSince,
            LostAt: _time.GetUtcNow(),
            Detail: WithHandlerErrors(detail)));
    }

    /// <summary>
    /// Notices a sidecar that died underneath a live subscription. Without it the stream simply
    /// goes quiet and every awaiting row would sit at "Awaiting settlement" while nobody was
    /// listening — the console's most dangerous condition.
    /// </summary>
    private void StartSidecarWatchdog()
    {
        _watchdog?.Cancel();
        _watchdog?.Dispose();
        var cancellation = new CancellationTokenSource();
        _watchdog = cancellation;
        var sidecar = _sidecar;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        await Task.Delay(SidecarWatchInterval, _time, cancellation.Token);
                        if (sidecar is not null && !sidecar.IsRunning)
                        {
                            OnSubscriptionFault(
                                "The console's own Dapr sidecar is no longer running, so nothing is being received.");
                            return;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Torn down deliberately.
                }
            },
            CancellationToken.None);
    }

    private async Task<(int Grpc, int Http, int Metrics, int InternalGrpc)?> AllocatePortsAsync(
        IReadOnlySet<int> excluded,
        CancellationToken ct)
    {
        const int required = 4;
        var free = new List<int>(required);
        for (var port = PortSearchStart; port <= PortSearchEnd && free.Count < required; port++)
        {
            if (!excluded.Contains(port) && await _probe.IsPortFreeAsync(port, ct))
            {
                free.Add(port);
            }
        }

        return free.Count == required ? (free[0], free[1], free[2], free[3]) : null;
    }

    private async Task TearDownAsync(CancellationToken ct)
    {
        if (_streamCancellation is not null)
        {
            await _streamCancellation.CancelAsync();
        }

        if (_watchdog is not null)
        {
            await _watchdog.CancelAsync();
            _watchdog.Dispose();
            _watchdog = null;
        }

        if (_subscription is not null)
        {
            try
            {
                await _subscription.DisposeAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A subscription whose stream is already broken throws on dispose. The sidecar
                // teardown below is the part that must not be skipped.
            }

            _subscription = null;
        }

        _client?.Dispose();
        _client = null;
        _streamCancellation?.Dispose();
        _streamCancellation = null;

        if (_sidecar is not null)
        {
            await _sidecar.StopAsync(ct);
            await _sidecar.DisposeAsync();
            _sidecar = null;
        }
    }

    private OutcomeFeedStatus Publish(OutcomeFeedStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(status);
        return status;
    }
}
