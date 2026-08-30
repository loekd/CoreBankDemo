using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

/// <summary>
/// Tests that boot PaymentsAPI's real <c>Program</c> entry point share this
/// collection. <c>WebApplication.CreateBuilder</c> reads
/// <c>ConnectionStrings__*</c> from process-wide environment variables before
/// any test hook can run, so the entry-point tests must not overwrite each
/// other's values concurrently. Every other class in this assembly stays
/// parallel — isolation comes from per-test databases, not from serializing
/// the suite.
/// </summary>
[CollectionDefinition(nameof(PaymentsEntryPointCollection), DisableParallelization = true)]
public sealed class PaymentsEntryPointCollection;

/// <summary>
/// Points PaymentsAPI's real entry point at this test's own PostgreSQL
/// database, and restores the previous environment on dispose.
/// </summary>
internal sealed class PaymentsEntryPointEnvironment : IAsyncDisposable
{
    private const string PaymentsConnectionStringVariable = "ConnectionStrings__paymentsdb";
    private const string RedisConnectionStringVariable = "ConnectionStrings__redis";

    private readonly string? _previousPaymentsConnectionString;
    private readonly string? _previousRedisConnectionString;

    private PaymentsEntryPointEnvironment(string connectionString)
    {
        _previousPaymentsConnectionString = Environment.GetEnvironmentVariable(PaymentsConnectionStringVariable);
        _previousRedisConnectionString = Environment.GetEnvironmentVariable(RedisConnectionStringVariable);

        // WebApplication.CreateBuilder(args) reads environment variables
        // synchronously as Program.cs's very first configuration source, so
        // these are already visible by the time its AddNpgsqlDbContext(...)/
        // AddRedisClient(...) calls run — unlike WebApplicationFactory's
        // ConfigureServices/ConfigureAppConfiguration callbacks, which only
        // apply once WebApplicationBuilder.Build() executes.
        Environment.SetEnvironmentVariable(PaymentsConnectionStringVariable, connectionString);

        // Redis is not under test here; a non-connecting placeholder keeps the
        // host from waiting on infrastructure this tier does not own (ADR-016
        // tier 3 owns real Redis).
        Environment.SetEnvironmentVariable(
            RedisConnectionStringVariable, "localhost:6379,abortConnect=false,connectTimeout=100");
    }

    public static PaymentsEntryPointEnvironment Apply(string connectionString) => new(connectionString);

    public ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(
            PaymentsConnectionStringVariable, _previousPaymentsConnectionString);
        Environment.SetEnvironmentVariable(
            RedisConnectionStringVariable, _previousRedisConnectionString);
        return ValueTask.CompletedTask;
    }
}
