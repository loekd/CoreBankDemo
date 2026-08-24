using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.DistributedLock;

/// <summary>
/// Story 3.2: proves the 5/6-of-lock-lifetime cooperative-cancellation math and
/// its <see cref="TimeProvider"/>-driven scheduling in complete isolation from
/// <see cref="Dapr.Client.DaprClient"/> and from real elapsed time.
/// <see cref="FakeTimeProvider"/>'s <c>Advance</c> deterministically fires any
/// <see cref="TimeProvider.CreateTimer"/> callback whose due time has passed —
/// no <c>Task.Delay</c>/<c>Thread.Sleep</c> anywhere in these tests.
/// </summary>
public class CooperativeLockCancellationTests
{
    [Theory]
    [InlineData(30, 25.0)]
    [InlineData(6, 5.0)]
    [InlineData(1, 5.0 / 6.0)]
    [InlineData(300, 250.0)]
    public void ComputeWorkTimeout_is_five_sixths_of_lock_expiry_seconds(int lockExpirySeconds, double expectedSeconds)
    {
        var timeout = CooperativeLockCancellation.ComputeWorkTimeout(lockExpirySeconds);

        timeout.TotalSeconds.Should().BeApproximately(expectedSeconds, 0.0001);
    }

    [Fact]
    public void Start_returns_a_token_that_is_not_cancelled_immediately()
    {
        var timeProvider = new FakeTimeProvider();

        using var scope = CooperativeLockCancellation.Start(timeProvider, CancellationToken.None, lockExpirySeconds: 30);

        scope.Token.IsCancellationRequested.Should().BeFalse();
        scope.WorkTimeout.Should().Be(TimeSpan.FromSeconds(25));
    }

    [Fact]
    public void Token_remains_uncancelled_before_five_sixths_of_expiry_elapses()
    {
        var timeProvider = new FakeTimeProvider();

        using var scope = CooperativeLockCancellation.Start(timeProvider, CancellationToken.None, lockExpirySeconds: 30);
        timeProvider.Advance(TimeSpan.FromSeconds(24));

        scope.Token.IsCancellationRequested.Should().BeFalse("only 24 of the 25s work timeout has elapsed");
    }

    [Fact]
    public void Token_cancels_once_five_sixths_of_expiry_elapses_simulated_via_time_provider()
    {
        var timeProvider = new FakeTimeProvider();

        using var scope = CooperativeLockCancellation.Start(timeProvider, CancellationToken.None, lockExpirySeconds: 30);
        timeProvider.Advance(TimeSpan.FromSeconds(25));

        scope.Token.IsCancellationRequested.Should().BeTrue("25s (5/6 of a 30s lock) has elapsed on the fake clock");
    }

    [Fact]
    public void Ambient_token_is_untouched_when_the_cooperative_cutoff_fires()
    {
        var timeProvider = new FakeTimeProvider();
        using var ambientCts = new CancellationTokenSource();

        using var scope = CooperativeLockCancellation.Start(timeProvider, ambientCts.Token, lockExpirySeconds: 30);
        timeProvider.Advance(TimeSpan.FromSeconds(25));

        scope.Token.IsCancellationRequested.Should().BeTrue();
        ambientCts.IsCancellationRequested.Should().BeFalse("the cooperative cutoff must only cancel the scope's own linked token, never the ambient one");
    }

    [Fact]
    public void Cancelling_the_ambient_token_also_cancels_the_scoped_token()
    {
        var timeProvider = new FakeTimeProvider();
        using var ambientCts = new CancellationTokenSource();

        using var scope = CooperativeLockCancellation.Start(timeProvider, ambientCts.Token, lockExpirySeconds: 30);
        ambientCts.Cancel();

        scope.Token.IsCancellationRequested.Should().BeTrue("the scoped token is linked to the ambient token, standard CreateLinkedTokenSource behavior");
    }

    [Fact]
    public void Dispose_does_not_throw_and_can_be_called_before_the_cutoff_fires()
    {
        var timeProvider = new FakeTimeProvider();

        var scope = CooperativeLockCancellation.Start(timeProvider, CancellationToken.None, lockExpirySeconds: 30);
        var act = scope.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public void CancelSafely_swallows_ObjectDisposedException_when_the_token_source_was_already_disposed()
    {
        // Reproduces the real-clock race the timer callback runs into: the
        // scope is disposed (workload already finished) right as the 5/6
        // cutoff timer fires on its own thread and invokes this callback.
        var cts = new CancellationTokenSource();
        cts.Dispose();

        var act = () => CooperativeLockCancellation.CancelSafely(cts);

        act.Should().NotThrow();
    }

    [Fact]
    public void CancelSafely_cancels_a_live_token_source()
    {
        using var cts = new CancellationTokenSource();

        CooperativeLockCancellation.CancelSafely(cts);

        cts.IsCancellationRequested.Should().BeTrue();
    }
}
