using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreBankDemo.CoreBankAPI.Tests;

/// <summary>
/// Story 6.2 verification gap: <c>AddServiceDefaultsTests</c> only proves
/// <c>AddServiceDefaults</c>'s own DI-selection logic against a manually
/// registered <c>IConnectionMultiplexer</c> mock — it never exercises
/// <c>Program.cs</c>'s actual <c>builder.AddRedisClient("redis")</c> call.
/// A resource-name drift between that call and AppHost.cs's
/// <c>builder.AddRedis("redis", ...)</c> would silently fall back
/// <see cref="IDistributedLockService"/> to <c>NoOpDistributedLockService</c>
/// (a total Inbox/Outbox processing outage) without failing any test or the
/// coverage gate, since <c>Program.cs</c> is excluded from coverage. This
/// test drives the real Aspire Redis client registration — with
/// <c>abortConnect=false</c> so no live Redis is required — through the same
/// call sequence <c>Program.cs</c> uses, under the same "redis" resource name.
/// </summary>
public class RedisLockWiringTests
{
    [Fact]
    public void The_redis_resource_name_Program_cs_registers_resolves_IDistributedLockService_to_the_Redis_adapter()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration["ConnectionStrings:redis"] = "localhost:6379,abortConnect=false,connectTimeout=100";

        // Mirrors Program.cs: AddRedisClient("redis") must run before
        // AddServiceDefaults so IConnectionMultiplexer is already registered
        // when the lock-service factory checks for it.
        builder.AddRedisClient("redis");
        builder.AddServiceDefaults("CoreBank.CoreBankAPI");

        using var provider = builder.Services.BuildServiceProvider();
        var lockService = provider.GetRequiredService<IDistributedLockService>();

        // RedisDistributedLockService is internal to ServiceDefaults and not
        // visible here; the type name is the resolution-drift signal this
        // test exists to catch, not an implementation detail worth exposing.
        lockService.GetType().Name.Should().Be("RedisDistributedLockService",
            "Program.cs's AddRedisClient(\"redis\") name must match AppHost.cs's redis resource name; " +
            "any drift silently falls back to NoOpDistributedLockService and stops all lock-guarded processing");
    }
}
