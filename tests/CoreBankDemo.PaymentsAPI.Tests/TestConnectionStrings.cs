namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// Connection strings for unit tests that must register the production Npgsql
/// provider so a DI graph resolves, but never open a connection. This tier is
/// Docker-free by contract (ADR-016); anything that actually touches the
/// database lives in <c>CoreBankDemo.Persistence.IntegrationTests</c>.
/// </summary>
internal static class TestConnectionStrings
{
    internal const string NeverConnected =
        "Host=never-connected.invalid;Database=unused;Username=unused;Password=unused";
}
