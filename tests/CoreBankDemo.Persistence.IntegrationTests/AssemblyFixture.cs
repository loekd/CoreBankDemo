using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using Xunit;

// One PostgreSQL container for the whole assembly (ADR-016): the cold-start
// cost is amortized across every persistence test, while per-test databases
// created from this fixture keep concurrently running classes isolated.
[assembly: AssemblyFixture(typeof(PostgresContainerFixture))]
