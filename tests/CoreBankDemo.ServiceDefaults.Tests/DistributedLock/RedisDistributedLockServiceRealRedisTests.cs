using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.DistributedLock;

/// <summary>
/// Story 6.2's real-Redis proof (spec Verification: "Run the Redis
/// integration proof against an Aspire-started or disposable local Redis
/// instance"). Deliberately separated from the mocked unit-gate coverage in
/// <see cref="RedisDistributedLockServiceTests"/>: this class talks to an
/// actual Redis instance and dynamically skips (rather than failing) when
/// one isn't reachable, so plain <c>dotnet test</c> never requires
/// infrastructure. Point a real instance at it via the
/// <c>COREBANKDEMO_TEST_REDIS_CONNECTION</c> environment variable (defaults
/// to <c>localhost:6379</c>, matching the regular AppHost's Redis host
/// port), for example an Aspire-started run or a disposable
/// <c>docker run -p 6379:6379 redis:7-alpine</c>.
/// </summary>
[Trait("Category", "Integration")]
[Trait("RequiresInfrastructure", "Redis")]
public class RedisDistributedLockServiceRealRedisTests
{
    private const int LockExpirySeconds = 2;

    [Fact]
    public async Task Bounded_acquire_waits_for_a_busy_lock_while_the_non_blocking_form_skips_it()
    {
        // ADR-018 priority addendum: the instant rail's inline paths queue for
        // a busy partition lock for a bounded time; the background processors
        // keep the skip-immediately behaviour.
        var connectionString = Environment.GetEnvironmentVariable("COREBANKDEMO_TEST_REDIS_CONNECTION")
            ?? "localhost:6379,connectTimeout=1000,abortConnect=false";
        ConnectionMultiplexer? multiplexer = null;
        try
        {
            multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString);
        }
        catch
        {
            // Not reachable: skipped below.
        }

        Xunit.Assert.SkipUnless(
            multiplexer is { IsConnected: true },
            $"requires a reachable real Redis instance (tried '{connectionString}')");

        await using var connection = multiplexer;
        var lockName = $"bounded-acquire-{Guid.NewGuid():N}";
        var factory = new RedisDistributedLockFactory(connection!);
        var holder = new RedisDistributedLockService(factory, NullLogger<RedisDistributedLockService>.Instance);
        var contender = new RedisDistributedLockService(factory, NullLogger<RedisDistributedLockService>.Instance);
        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holderTask = holder.ExecuteWithLockAsync(lockName, LockExpirySeconds, async _ =>
        {
            holderStarted.SetResult();
            await release.Task;
        });
        await holderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var skipped = await contender.ExecuteWithLockAsync(lockName, LockExpirySeconds, _ => Task.CompletedTask);
        var waited = contender.ExecuteWithLockAsync(lockName, LockExpirySeconds, TimeSpan.FromSeconds(3), _ => Task.CompletedTask);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        waited.IsCompleted.Should().BeFalse("the bounded form queues behind the holder");
        release.SetResult();

        (await holderTask).Should().BeTrue();
        skipped.Should().BeFalse("the non-blocking form never queues");
        (await waited.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task Lease_survives_past_its_initial_expiry_and_a_second_contender_only_acquires_after_release()
    {
        var connectionString = Environment.GetEnvironmentVariable("COREBANKDEMO_TEST_REDIS_CONNECTION")
            ?? "localhost:6379,connectTimeout=1000,abortConnect=false";

        ConnectionMultiplexer? multiplexer = null;
        try
        {
            multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString);
        }
        catch
        {
            // Connection failures are exactly the "not reachable" case this test
            // dynamically skips for below — swallow and let SkipUnless report it.
        }

        Xunit.Assert.SkipUnless(
            multiplexer is { IsConnected: true },
            $"requires a reachable real Redis instance (tried '{connectionString}'); " +
            "set COREBANKDEMO_TEST_REDIS_CONNECTION or run one on localhost:6379");

        await using var connection = multiplexer;
        var lockName = $"real-redis-proof-{Guid.NewGuid():N}";
        var factory = new RedisDistributedLockFactory(connection!);
        var holder = new RedisDistributedLockService(factory, NullLogger<RedisDistributedLockService>.Instance);
        var contender = new RedisDistributedLockService(factory, NullLogger<RedisDistributedLockService>.Instance);

        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contentionWhileHeldResults = new List<bool>();

        var holderTask = holder.ExecuteWithLockAsync(lockName, LockExpirySeconds, async ct =>
        {
            holderStarted.SetResult();

            // Hold the lock well past its initial 2s expiry: only automatic
            // renewal (not a fixed cutoff) keeps this exclusive.
            await Task.Delay(TimeSpan.FromSeconds(3.5), ct);

            // Prove a contender still cannot acquire while we hold it, even
            // this deep past the initial expiry.
            var stillContended = await contender.ExecuteWithLockAsync(lockName, LockExpirySeconds, _ => Task.CompletedTask, ct);
            contentionWhileHeldResults.Add(stillContended);
        }, TestContext.Current.CancellationToken);

        await holderStarted.Task;

        // A second, independent contention attempt from outside the holder's
        // workload, timed to land after the initial 2s expiry would have
        // fired under the old fixed-lifetime behavior.
        await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);
        var contendedFromOutside = await contender.ExecuteWithLockAsync(lockName, LockExpirySeconds, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        var holderResult = await holderTask;

        holderResult.Should().BeTrue("the holder's workload should complete normally under a renewed lease");
        contendedFromOutside.Should().BeFalse("the lease must still be exclusively owned past its initial expiry");
        contentionWhileHeldResults.Should().ContainSingle().Which.Should().BeFalse(
            "a contender inside the holder's own workload must also observe exclusive ownership");

        var afterReleaseResult = await contender.ExecuteWithLockAsync(lockName, LockExpirySeconds, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        afterReleaseResult.Should().BeTrue("once the holder releases, a contender must be able to acquire the same lock name");
    }
}
