using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.LoadTestSupport;
using CoreBankDemo.LoadTestSupport.Endpoints;
using CoreBankDemo.LoadTestSupport.McpTools;
using CoreBankDemo.LoadTestSupport.Services;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.LoadTestSupport;

/// <summary>
/// HTTP-level contract-lock coverage for <c>/assert/results</c> and
/// <c>/assert/drain</c> — story 7.1 code-review fix. Runs the real
/// minimal-API route delegates from
/// <see cref="AssertEndpoints.MapAssertEndpoints"/> over a
/// <see cref="TestServer"/> (never calling <see cref="LoadTestAssertionService"/>
/// directly), following <see cref="InboxOutboxEndpointsIntegrationTests"/>'s
/// pattern, and parses the raw JSON response. This locks in the pre-story-7.1
/// JSON shape where <c>duplicates</c>/<c>discrepancies</c> nest inside their
/// own check objects (<c>checks.noDuplicateProcessing.duplicates</c>,
/// <c>checks.balancesCorrect.discrepancies</c>) rather than sitting as
/// top-level siblings — the exact regression this fix restores — and proves
/// REST and the MCP tool produce field-for-field identical JSON for the same
/// seeded data, this story's own acceptance criterion, previously untested.
/// </summary>
public sealed class AssertEndpointsHttpIntegrationTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private string? _coreBankConnectionString;
    private string? _paymentsConnectionString;
    private IHost? _host;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _coreBankConnectionString = await fixture.CreateDatabaseAsync("asserthttpcore", cancellationToken);
        _paymentsConnectionString = await fixture.CreateDatabaseAsync("asserthttppayments", cancellationToken);

        await using (var coreBank = CreateCoreBankContext())
        await using (var payments = CreatePaymentsContext())
        {
            await coreBank.Database.EnsureCreatedAsync(cancellationToken);
            await payments.Database.EnsureCreatedAsync(cancellationToken);
        }

        var builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddDbContext<CoreBankDbContext>(o => o.UseNpgsql(_coreBankConnectionString));
                    services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(_paymentsConnectionString));
                    services.AddScoped<LoadTestAssertionService>();
                    services.AddRouting();
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapAssertEndpoints();
                    });
                });
            });

        _host = await builder.StartAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        if (_coreBankConnectionString is not null)
        {
            await fixture.DropDatabaseAsync(_coreBankConnectionString);
        }

        if (_paymentsConnectionString is not null)
        {
            await fixture.DropDatabaseAsync(_paymentsConnectionString);
        }
    }

    [Fact]
    public async Task AssertResults_nests_duplicates_and_discrepancies_inside_their_own_check_objects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var coreBank = CreateCoreBankContext())
        await using (var payments = CreatePaymentsContext())
        {
            var accounts = SeedLoadAccounts(coreBank, 1, 2);
            // Replay expects account 2 at InitialBalance + 100; persist a wrong value
            // so BalancesCorrect fails and carries a discrepancy to assert on.
            accounts[1].Balance = LoadTestConstants.InitialBalance + 40m;
            coreBank.InboxMessages.Add(CompletedTransfer("key-1", 1, 2, 100m));
            payments.OutboxMessages.Add(CompletedOutbox("key-1"));
            await coreBank.SaveChangesAsync(cancellationToken);
            await payments.SaveChangesAsync(cancellationToken);
        }

        var client = _host!.GetTestClient();
        var response = await client.GetAsync("/assert/results?expectedUnique=1", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);

        var checks = json.GetProperty("checks");

        checks.GetProperty("noDuplicateProcessing").GetProperty("duplicates").ValueKind.Should().Be(JsonValueKind.Array);
        checks.TryGetProperty("duplicates", out _).Should()
            .BeFalse("duplicates must live inside noDuplicateProcessing, not as a top-level checks sibling");

        var discrepancies = checks.GetProperty("balancesCorrect").GetProperty("discrepancies");
        discrepancies.GetArrayLength().Should().BeGreaterThan(0);
        checks.TryGetProperty("discrepancies", out _).Should()
            .BeFalse("discrepancies must live inside balancesCorrect, not as a top-level checks sibling");
    }

    [Fact]
    public async Task AssertDrain_reports_all_four_store_pending_counts_over_http()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var coreBank = CreateCoreBankContext())
        {
            coreBank.MessagingOutboxMessages.Add(CoreBankOutboxPending("outbox-1"));
            await coreBank.SaveChangesAsync(cancellationToken);
        }

        var client = _host!.GetTestClient();
        var response = await client.GetAsync("/assert/drain", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);

        json.GetProperty("isDrained").GetBoolean().Should().BeFalse();
        json.GetProperty("coreBankOutboxPending").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Rest_and_mcp_produce_field_for_field_identical_json_for_the_same_seeded_data()
    {
        // This story's own acceptance criterion ("REST and the MCP tools must
        // stay behaviorally identical") had no test proving it until now.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var coreBank = CreateCoreBankContext())
        await using (var payments = CreatePaymentsContext())
        {
            SeedLoadAccounts(coreBank, 1, 2);
            coreBank.InboxMessages.Add(CompletedTransfer("key-1", 1, 2, 100m));
            payments.OutboxMessages.Add(CompletedOutbox("key-1"));
            await coreBank.SaveChangesAsync(cancellationToken);
            await payments.SaveChangesAsync(cancellationToken);
        }

        var client = _host!.GetTestClient();
        var restResponse = await client.GetAsync("/assert/results?expectedUnique=1", cancellationToken);
        restResponse.EnsureSuccessStatusCode();
        var restJson = await restResponse.Content.ReadAsStringAsync(cancellationToken);

        await using var mcpCoreBank = CreateCoreBankContext();
        await using var mcpPayments = CreatePaymentsContext();
        var assertionService = new LoadTestAssertionService(mcpCoreBank, mcpPayments);
        var mcpJson = await LoadTestTools.GetAssertionResults(assertionService, expectedUnique: 1, cancellationToken);

        var restNode = JsonNode.Parse(restJson);
        var mcpNode = JsonNode.Parse(mcpJson);
        JsonNode.DeepEquals(restNode, mcpNode).Should()
            .BeTrue($"REST and MCP must produce field-for-field identical JSON.\nREST: {restJson}\nMCP: {mcpJson}");
    }

    private static string AccountNumber(int i) => $"NL{i:D2}LOAD{i:D10}";

    private static List<Account> SeedLoadAccounts(CoreBankDbContext coreBank, params int[] accountIndexes)
    {
        var accounts = accountIndexes
            .Select(i => new Account
            {
                AccountNumber = AccountNumber(i),
                AccountHolderName = $"Load Test Account {i:D2}",
                Balance = LoadTestConstants.InitialBalance,
                Currency = "EUR",
                IsActive = true,
                CreatedAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc)
            })
            .ToList();
        coreBank.Accounts.AddRange(accounts);
        return accounts;
    }

    private static InboxMessage CompletedTransfer(string key, int fromIndex, int toIndex, decimal amount) =>
        new()
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = key,
            TransactionId = key,
            PartitionId = 0,
            Status = MessageConstants.Status.Completed,
            ReceivedAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
            ProcessedAt = new DateTime(2026, 8, 30, 0, 1, 0, DateTimeKind.Utc),
            FromAccount = AccountNumber(fromIndex),
            ToAccount = AccountNumber(toIndex),
            Amount = amount,
            Currency = "EUR"
        };

    private static CoreBankDemo.PaymentsAPI.Outbox.OutboxMessage CompletedOutbox(string key)
    {
        var outbox = PaymentsApiTestData.Outbox(key);
        outbox.Status = MessageConstants.Status.Completed;
        return outbox;
    }

    private static MessagingOutboxMessage CoreBankOutboxPending(string key) =>
        new()
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = key,
            PartitionId = 0,
            Status = MessageConstants.Status.Pending,
            CreatedAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
            EventOccurredAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
            TransactionId = key,
            EventType = "test.event",
            EventSource = "test",
            AccountNumber = "from",
            ToAccount = "to",
            Amount = 1m,
            Currency = "EUR",
            TransactionStatus = MessageConstants.Status.Completed
        };

    private CoreBankDbContext CreateCoreBankContext() =>
        new(new DbContextOptionsBuilder<CoreBankDbContext>()
            .UseNpgsql(_coreBankConnectionString)
            .Options);

    private PaymentsDbContext CreatePaymentsContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(_paymentsConnectionString)
            .Options);
}
