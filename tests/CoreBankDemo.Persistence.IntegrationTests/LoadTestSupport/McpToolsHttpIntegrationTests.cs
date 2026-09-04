using System.Text.Json.Nodes;
using System.Net.Http.Json;
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
using CoreBankDemo.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.LoadTestSupport;

/// <summary>
/// HTTP-level contract-lock coverage for story 7.2: <c>reset_database</c> and
/// the four <c>get_*_inbox</c>/<c>get_*_outbox</c> MCP tools must produce JSON
/// field-for-field identical to their REST counterparts
/// (<c>/reset</c>, <c>/corebank/inbox</c>, <c>/corebank/outbox</c>,
/// <c>/payments/inbox</c>, <c>/payments/outbox</c>), and <c>reset_database</c>
/// must delegate to the real <see cref="DatabaseResetCoordinator"/>
/// (transactional truncate + balance reset, and exactly-once processor-start-gate
/// release) rather than reimplementing it, mirroring
/// <see cref="AssertEndpointsHttpIntegrationTests"/>'s pattern from story 7.1:
/// a <see cref="TestServer"/> hosts the real minimal-API route delegates, and
/// the MCP tool's static method is invoked directly against the same
/// container-backed database. The processor start gate publisher is mocked
/// (no real Redis), exactly as <c>DatabaseResetCoordinatorTests</c> mocks it.
/// </summary>
public sealed class McpToolsHttpIntegrationTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private string? _coreBankConnectionString;
    private string? _paymentsConnectionString;
    private IHost? _host;
    private Mock<IProcessorStartGatePublisher> _publisher = null!;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _coreBankConnectionString = await fixture.CreateDatabaseAsync("mcptoolscore", cancellationToken);
        _paymentsConnectionString = await fixture.CreateDatabaseAsync("mcptoolspayments", cancellationToken);

        await using (var coreBank = CreateCoreBankContext())
        await using (var payments = CreatePaymentsContext())
        {
            await coreBank.Database.EnsureCreatedAsync(cancellationToken);
            await payments.Database.EnsureCreatedAsync(cancellationToken);
        }

        _publisher = new Mock<IProcessorStartGatePublisher>();
        _publisher.Setup(p => p.HasReleaseGenerationAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _publisher.Setup(p => p.ReleaseAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddDbContext<CoreBankDbContext>(o => o.UseNpgsql(_coreBankConnectionString));
                    services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(_paymentsConnectionString));
                    services.AddScoped<ILoadTestDatabaseResetter, LoadTestDatabaseResetter>();
                    services.AddSingleton<DatabaseResetState>();
                    services.AddSingleton<LoadRunEvidenceState>();
                    services.AddSingleton(_publisher.Object);
                    services.AddScoped<DatabaseResetCoordinator>();
                    services.AddRouting();
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapResetEndpoints();
                        endpoints.MapInboxEndpoints();
                        endpoints.MapOutboxEndpoints();
                        endpoints.MapRunEvidenceEndpoints();
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
    public async Task GetCoreBankInbox_rest_and_mcp_are_field_for_field_identical_at_the_fifty_row_cap()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var coreBank = CreateCoreBankContext())
        {
            var baseline = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 55; i++)
            {
                coreBank.InboxMessages.Add(CoreBankInbox($"key-{i:D2}", baseline.AddMinutes(i)));
            }
            await coreBank.SaveChangesAsync(cancellationToken);
        }

        var client = _host!.GetTestClient();
        var restResponse = await client.GetAsync("/corebank/inbox", cancellationToken);
        restResponse.EnsureSuccessStatusCode();
        var restJson = await restResponse.Content.ReadAsStringAsync(cancellationToken);

        await using var mcpDb = CreateCoreBankContext();
        var mcpJson = await LoadTestTools.GetCoreBankInbox(mcpDb, cancellationToken);

        AssertJsonEqual(restJson, mcpJson);
        JsonNode.Parse(mcpJson)!.AsArray().Count.Should().Be(50);
    }

    [Fact]
    public async Task GetCoreBankOutbox_rest_and_mcp_produce_field_for_field_identical_json()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var coreBank = CreateCoreBankContext())
        {
            coreBank.MessagingOutboxMessages.Add(
                CoreBankOutbox("older", new DateTime(2026, 8, 30, 9, 0, 0, DateTimeKind.Utc)));
            coreBank.MessagingOutboxMessages.Add(
                CoreBankOutbox("newer", new DateTime(2026, 8, 30, 11, 0, 0, DateTimeKind.Utc)));
            await coreBank.SaveChangesAsync(cancellationToken);
        }

        var client = _host!.GetTestClient();
        var restResponse = await client.GetAsync("/corebank/outbox", cancellationToken);
        restResponse.EnsureSuccessStatusCode();
        var restJson = await restResponse.Content.ReadAsStringAsync(cancellationToken);

        await using var mcpDb = CreateCoreBankContext();
        var mcpJson = await LoadTestTools.GetCoreBankOutbox(mcpDb, cancellationToken);

        AssertJsonEqual(restJson, mcpJson);
    }

    [Fact]
    public async Task GetPaymentsInbox_rest_and_mcp_produce_field_for_field_identical_json()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var payments = CreatePaymentsContext())
        {
            var older = PaymentsApiTestData.Inbox("older", "BalanceUpdated", "acct-1");
            older.ReceivedAt = new DateTime(2026, 8, 30, 8, 0, 0, DateTimeKind.Utc);
            var newer = PaymentsApiTestData.Inbox("newer", "BalanceUpdated", "acct-2");
            newer.ReceivedAt = new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc);
            payments.InboxMessages.Add(older);
            payments.InboxMessages.Add(newer);
            await payments.SaveChangesAsync(cancellationToken);
        }

        var client = _host!.GetTestClient();
        var restResponse = await client.GetAsync("/payments/inbox", cancellationToken);
        restResponse.EnsureSuccessStatusCode();
        var restJson = await restResponse.Content.ReadAsStringAsync(cancellationToken);

        await using var mcpDb = CreatePaymentsContext();
        var mcpJson = await LoadTestTools.GetPaymentsInbox(mcpDb, cancellationToken);

        AssertJsonEqual(restJson, mcpJson);
    }

    [Fact]
    public async Task GetPaymentsOutbox_rest_and_mcp_produce_field_for_field_identical_json()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var payments = CreatePaymentsContext())
        {
            var older = PaymentsApiTestData.Outbox("older");
            older.CreatedAt = new DateTime(2026, 8, 30, 7, 0, 0, DateTimeKind.Utc);
            var newer = PaymentsApiTestData.Outbox("newer");
            newer.CreatedAt = new DateTime(2026, 8, 30, 9, 0, 0, DateTimeKind.Utc);
            payments.OutboxMessages.Add(older);
            payments.OutboxMessages.Add(newer);
            await payments.SaveChangesAsync(cancellationToken);
        }

        var client = _host!.GetTestClient();
        var restResponse = await client.GetAsync("/payments/outbox", cancellationToken);
        restResponse.EnsureSuccessStatusCode();
        var restJson = await restResponse.Content.ReadAsStringAsync(cancellationToken);

        await using var mcpDb = CreatePaymentsContext();
        var mcpJson = await LoadTestTools.GetPaymentsOutbox(mcpDb, cancellationToken);

        AssertJsonEqual(restJson, mcpJson);
    }

    [Fact]
    public async Task ResetDatabase_rest_and_mcp_produce_field_for_field_identical_json()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var coreBank = CreateCoreBankContext())
        {
            SeedLoadAccounts(coreBank, 1, 2);
            await coreBank.SaveChangesAsync(cancellationToken);
        }

        var client = _host!.GetTestClient();
        var restResponse = await client.PostAsync("/reset", content: null, cancellationToken);
        restResponse.EnsureSuccessStatusCode();
        var restJson = await restResponse.Content.ReadAsStringAsync(cancellationToken);

        var restNode = JsonNode.Parse(restJson)!;
        restNode["accountsReset"]!.GetValue<int>().Should().Be(2);
        restNode["totalBalance"]!.GetValue<decimal>().Should().Be(2 * LoadTestConstants.InitialBalance);

        // The second call resets again but must not publish a second processor
        // release generation. The unchanged result shape proves the REST and MCP
        // paths stay field-for-field equivalent.
        using var scope = _host!.Services.CreateScope();
        var mcpJson = await LoadTestTools.ResetDatabase(scope.ServiceProvider, cancellationToken);

        AssertJsonEqual(restJson, mcpJson);
        _publisher.Verify(p => p.ReleaseAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResetDatabase_through_mcp_releases_the_processor_start_gate_exactly_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = _host!.Services.CreateScope();

        var json = await LoadTestTools.ResetDatabase(scope.ServiceProvider, cancellationToken);

        JsonNode.Parse(json)!.AsObject().ContainsKey("error").Should().BeFalse($"reset should have succeeded: {json}");
        _publisher.Verify(p => p.ReleaseAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Inline_settlement_evidence_is_deduplicated_and_reset_with_the_run()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = _host!.GetTestClient();
        using var first = await client.PostAsJsonAsync(
            "/run-evidence/inline-settlement",
            new InlineSettlementEvidence("load-test-0000000000"),
            cancellationToken);
        using var duplicate = await client.PostAsJsonAsync(
            "/run-evidence/inline-settlement",
            new InlineSettlementEvidence("load-test-0000000000"),
            cancellationToken);

        first.EnsureSuccessStatusCode();
        duplicate.EnsureSuccessStatusCode();
        using (var scope = _host!.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<LoadRunEvidenceState>()
                .InlineSettlementCount.Should().Be(1);
        }

        using var reset = await client.PostAsync("/reset", null, cancellationToken);
        reset.EnsureSuccessStatusCode();
        using var afterScope = _host.Services.CreateScope();
        afterScope.ServiceProvider.GetRequiredService<LoadRunEvidenceState>()
            .InlineSettlementCount.Should().Be(0);
    }

    [Fact]
    public async Task Inline_settlement_evidence_returns_bad_request_for_a_null_body_instead_of_500()
    {
        // Patch 6 regression test: a literal JSON "null" body binds `evidence`
        // to null; the handler must return a clean 400 rather than dereference
        // it and surface an unhandled 500.
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = _host!.GetTestClient();
        using var nullBody = new System.Net.Http.StringContent("null", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/run-evidence/inline-settlement", nullBody, cancellationToken);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResetDatabase_called_twice_through_mcp_resets_twice_but_releases_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = _host!.Services.CreateScope();

        var first = await LoadTestTools.ResetDatabase(scope.ServiceProvider, cancellationToken);
        var second = await LoadTestTools.ResetDatabase(scope.ServiceProvider, cancellationToken);

        AssertJsonEqual(first, second);
        _publisher.Verify(p => p.ReleaseAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResetDatabase_after_existing_release_generation_resets_without_releasing_again()
    {
        _publisher.Setup(p => p.HasReleaseGenerationAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = _host!.Services.CreateScope();

        var json = await LoadTestTools.ResetDatabase(scope.ServiceProvider, cancellationToken);

        var node = JsonNode.Parse(json)!.AsObject();
        node.ContainsKey("error").Should().BeFalse();
        node["message"]!.GetValue<string>().Should().Be("Database reset complete");
        _publisher.Verify(p => p.ReleaseAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static void AssertJsonEqual(string restJson, string mcpJson)
    {
        var restNode = JsonNode.Parse(restJson);
        var mcpNode = JsonNode.Parse(mcpJson);
        JsonNode.DeepEquals(restNode, mcpNode).Should()
            .BeTrue($"REST and MCP must produce field-for-field identical JSON.\nREST: {restJson}\nMCP: {mcpJson}");
    }

    private static string AccountNumber(int i) => $"NL{i:D2}LOAD{i:D10}";

    private static void SeedLoadAccounts(CoreBankDbContext coreBank, params int[] accountIndexes)
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
    }

    private static InboxMessage CoreBankInbox(string key, DateTime receivedAt) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = key,
        TransactionId = key,
        PartitionId = 0,
        Status = MessageConstants.Status.Pending,
        ReceivedAt = receivedAt,
        FromAccount = "from",
        ToAccount = "to",
        Amount = 1m,
        Currency = "EUR"
    };

    private static MessagingOutboxMessage CoreBankOutbox(string key, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = key,
        PartitionId = 0,
        Status = MessageConstants.Status.Pending,
        CreatedAt = createdAt,
        EventOccurredAt = createdAt,
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
