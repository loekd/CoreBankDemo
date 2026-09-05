using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Infrastructure;
using Moq;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

/// <summary>
/// The wire-level half of the feedback loop. Everything above this class works against
/// <c>OutcomeEvent</c>, so nothing else in the suite would notice if a CloudEvent type string
/// or a payload field name were wrong — the console would simply stop settling payments, in
/// front of an audience. These assertions build the exact JSON the publisher emits.
/// </summary>
public class DaprOutcomeFeedTests
{
    private static readonly DateTimeOffset ProcessedAt = new(2026, 9, 5, 12, 4, 31, 882, TimeSpan.Zero);

    [Fact]
    public void TryParse_CompletedEvent_ReadsEveryFieldTheRowRenders()
    {
        var payload = Payload(new
        {
            transactionId = "tx-8821",
            status = "Completed",
            processedAt = ProcessedAt,
        });

        var parsed = DaprOutcomeFeed.TryParse(OutcomeEventTypes.TransactionCompleted, payload);

        parsed.Should().NotBeNull();
        parsed!.EventType.Should().Be("com.corebank.transaction.completed");
        parsed.TransactionId.Should().Be("tx-8821");
        parsed.Completed!.Status.Should().Be("Completed");
        parsed.Completed.ProcessedAt.Should().Be(ProcessedAt);
        parsed.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void TryParse_FailedEvent_KeepsTheErrorReasonTheRoomAsksAbout()
    {
        var payload = Payload(new
        {
            transactionId = "tx-8822",
            status = "Failed",
            processedAt = ProcessedAt,
            errorReason = "insufficient funds",
        });

        var parsed = DaprOutcomeFeed.TryParse(OutcomeEventTypes.TransactionFailed, payload);

        parsed.Should().NotBeNull();
        parsed!.Failed!.ErrorReason.Should().Be("insufficient funds");
        parsed.ProcessedAt.Should().Be(ProcessedAt);
        parsed.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void TryParse_BalanceEvent_ReadsTheAmountsTheLegColumnPrints()
    {
        var payload = Payload(new
        {
            transactionId = "tx-8821",
            accountNumber = "1001",
            delta = -250.00m,
            newBalance = 4750.00m,
            currency = "EUR",
        });

        var parsed = DaprOutcomeFeed.TryParse(OutcomeEventTypes.BalanceUpdated, payload);

        parsed.Should().NotBeNull();
        parsed!.BalanceUpdated.Should().Be(new BalanceUpdatedWireEvent("tx-8821", "1001", -250.00m, 4750.00m, "EUR"));
        parsed.IsTerminal.Should().BeFalse("a balance leg is not a terminal outcome");
        parsed.ProcessedAt.Should().BeNull("this event type carries no clock of its own");
    }

    [Fact]
    public void TryParse_UnknownEventType_IsDroppedRatherThanGuessedAt()
    {
        var payload = Payload(new { transactionId = "tx-9000" });

        DaprOutcomeFeed.TryParse("com.corebank.something.else", payload).Should().BeNull();
    }

    [Fact]
    public void TryParse_MalformedBody_IsDroppedWithoutThrowingIntoTheStream()
    {
        var payload = Encoding.UTF8.GetBytes("{ this is not json");

        DaprOutcomeFeed.TryParse(OutcomeEventTypes.TransactionCompleted, payload).Should().BeNull();
    }

    [Fact]
    public void TryParse_MissingTransactionId_IsDropped()
    {
        // TransactionId is the only correlation identifier this console has; an event without
        // one can neither resolve a row nor be honestly labelled unattributed.
        var payload = Payload(new { status = "Completed", processedAt = ProcessedAt });

        DaprOutcomeFeed.TryParse(OutcomeEventTypes.TransactionCompleted, payload).Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_SidecarRefusesToStart_ReportsUnavailableWithTheSidecarsOwnReason()
    {
        var sidecar = new FakeSidecar
        {
            Result = new DaprSidecarStartResult(false, null, "daprd is not on PATH or could not be started: no such file"),
            Output = "daprd: exec format error",
        };
        var feed = NewFeed(() => sidecar);
        var announced = new List<OutcomeFeedStatus>();
        feed.StatusChanged += announced.Add;

        var status = await feed.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        status.State.Should().Be(OutcomeFeedState.Unavailable);
        status.Detail.Should().Contain("daprd is not on PATH")
            .And.Contain("daprd: exec format error", "a sidecar that refused to come up has to explain itself");
        announced.Should().ContainSingle().Which.State.Should().Be(OutcomeFeedState.Unavailable);
        sidecar.Disposed.Should().BeTrue("a sidecar that failed to start is never left behind");
    }

    [Fact]
    public async Task StartAsync_SidecarThrows_LeavesNoOrphanAndStillReportsUnavailable()
    {
        var sidecar = new FakeSidecar { Exception = new InvalidOperationException("boom") };
        var feed = NewFeed(() => sidecar);

        var status = await feed.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        status.State.Should().Be(OutcomeFeedState.Unavailable);
        status.Detail.Should().Contain("boom");
        sidecar.StopCount.Should().BeGreaterThan(0, "a throwing start must not leave a live daprd behind");
        sidecar.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_PortConflict_RetriesOnceWithFreshPorts()
    {
        // The probe frees a port and daprd binds it a moment later; losing that race must not
        // leave the console permanently without a feed.
        var sidecars = new List<FakeSidecar>();
        var feed = NewFeed(() =>
        {
            var sidecar = new FakeSidecar
            {
                Result = new DaprSidecarStartResult(false, null, "listen tcp 127.0.0.1:53100: bind: address already in use"),
            };
            sidecars.Add(sidecar);
            return sidecar;
        });

        await feed.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        sidecars.Should().HaveCount(2);
        var first = new[]
        {
            sidecars[0].Launch!.GrpcPort,
            sidecars[0].Launch!.HttpPort,
            sidecars[0].Launch!.MetricsPort,
            sidecars[0].Launch!.InternalGrpcPort,
        };
        var second = new[]
        {
            sidecars[1].Launch!.GrpcPort,
            sidecars[1].Launch!.HttpPort,
            sidecars[1].Launch!.MetricsPort,
            sidecars[1].Launch!.InternalGrpcPort,
        };
        second.Should().NotIntersectWith(first, "retrying the same four ports would lose the same race again");
    }

    [Fact]
    public async Task StartAsync_AllocatesFourDistinctPortsIncludingTheInternalGrpcOne()
    {
        var sidecar = new FakeSidecar
        {
            Result = new DaprSidecarStartResult(false, null, "not started"),
        };
        var feed = NewFeed(() => sidecar);

        await feed.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        var launch = sidecar.Launch;
        launch.Should().NotBeNull();
        // --metrics-port defaults to 9090 and --dapr-internal-grpc-port to 50002; both are the
        // AppHost's own sidecars' likeliest neighbours, so neither may be left to default.
        new[] { launch!.GrpcPort, launch.HttpPort, launch.MetricsPort, launch.InternalGrpcPort }
            .Should().OnlyHaveUniqueItems().And.HaveCount(4);
        launch.AppId.Should().Be(DaprOutcomeFeed.AppId)
            .And.NotBe(KnownResources.PaymentsApi, "a shared app-id would share the consumer group");
    }

    [Fact]
    public async Task StartAsync_NoTopology_SaysSoRatherThanSpawningASidecar()
    {
        var sidecar = new FakeSidecar();
        var feed = NewFeed(() => sidecar);

        var status = await feed.StartAsync(TopologyProfile.None, CancellationToken.None);

        status.State.Should().Be(OutcomeFeedState.Unavailable);
        sidecar.Launch.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_NoFreePorts_ReportsUnavailableRatherThanGuessingOne()
    {
        var probe = new Mock<IEnvironmentProbe>();
        probe.Setup(item => item.IsPortFreeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var sidecar = new FakeSidecar();
        await using var feed = new DaprOutcomeFeed(
            RepositoryRoot(),
            probe.Object,
            TimeProvider.System,
            () => sidecar);

        var status = await feed.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        status.State.Should().Be(OutcomeFeedState.Unavailable);
        status.Detail.Should().Contain("free ports");
        sidecar.Launch.Should().BeNull();
    }

    [Fact]
    public async Task StopAsync_WithNothingRunning_IsSafeAndReportsNotStarted()
    {
        var feed = NewFeed(() => new FakeSidecar());
        var announced = new List<OutcomeFeedStatus>();
        feed.StatusChanged += announced.Add;

        await feed.StopAsync(CancellationToken.None);

        announced.Should().ContainSingle().Which.State.Should().Be(OutcomeFeedState.NotStarted);
    }

    [Fact]
    public async Task StopAsync_AfterAFailedStart_TearsTheSidecarDown()
    {
        var sidecar = new FakeSidecar { Result = new DaprSidecarStartResult(false, null, "no") };
        var feed = NewFeed(() => sidecar);
        await feed.StartAsync(TopologyProfile.Regular, CancellationToken.None);

        await feed.StopAsync(CancellationToken.None);

        sidecar.Disposed.Should().BeTrue();
    }

    private static DaprOutcomeFeed NewFeed(Func<IDaprSidecar> sidecarFactory)
    {
        var probe = new Mock<IEnvironmentProbe>();
        probe.Setup(item => item.IsPortFreeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new DaprOutcomeFeed(RepositoryRoot(), probe.Object, TimeProvider.System, sidecarFactory);
    }

    private static string RepositoryRoot() => CheckedInDevProxyProfileTests.RepositoryRoot();

    /// <summary>
    /// Serialises exactly as the publisher does — Dapr's own <c>JsonSerializerDefaults.Web</c>
    /// camel casing over the CloudEvent's <c>data</c> object.
    /// </summary>
    private static byte[] Payload(object value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class FakeSidecar : IDaprSidecar
    {
        public DaprSidecarLaunch? Launch { get; private set; }
        public DaprSidecarStartResult Result { get; set; } =
            new(true, new DaprSidecarHandle(4242, 1, 2, "daprd"), "started");
        public Exception? Exception { get; set; }
        public string Output { get; set; } = string.Empty;
        public int StopCount { get; private set; }
        public bool Disposed { get; private set; }

        public bool IsRunning { get; private set; }

        public string RecentOutput => Output;

        public Task<DaprSidecarStartResult> StartAsync(DaprSidecarLaunch launch, CancellationToken ct)
        {
            Launch = launch;
            if (Exception is not null)
            {
                throw Exception;
            }

            IsRunning = Result.Succeeded;
            return Task.FromResult(Result);
        }

        public Task StopAsync(CancellationToken ct)
        {
            StopCount++;
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
