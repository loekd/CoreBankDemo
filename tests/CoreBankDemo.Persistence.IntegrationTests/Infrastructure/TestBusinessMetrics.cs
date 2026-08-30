using CoreBankDemo.ServiceDefaults;

namespace CoreBankDemo.Persistence.IntegrationTests.Infrastructure;

/// <summary>
/// Shared <see cref="BusinessMetrics"/> instance for Tier 2 (real PostgreSQL)
/// repository tests (spec-6-5): these tests exercise
/// <c>MessageRepositoryBase{TMessage,TDbContext}.StoreIfNewAsync</c>'s
/// store-operation recording as a side effect of proving persistence
/// behavior, not as their primary subject -- dedicated metric-behavior
/// assertions live in the Moq-tier unit test projects instead. A single
/// static instance is enough here since no test in this project asserts on
/// its measurements.
/// </summary>
public static class TestBusinessMetrics
{
    public static BusinessMetrics Instance { get; } = new();
}
