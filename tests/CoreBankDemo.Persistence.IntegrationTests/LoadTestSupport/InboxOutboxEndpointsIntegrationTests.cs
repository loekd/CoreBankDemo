using System.Net.Http.Json;
using AwesomeAssertions;
using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.LoadTestSupport.Endpoints;
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
/// Integration coverage for the raw <c>InboxEndpoints</c>/<c>OutboxEndpoints</c>
/// inspection endpoints (story 7.1's Code Map: previously untested). Runs the
/// real minimal-API route delegates from
/// <see cref="InboxEndpoints.MapInboxEndpoints"/> and
/// <see cref="OutboxEndpoints.MapOutboxEndpoints"/> over a
/// <see cref="TestServer"/> wired to isolated PostgreSQL databases, following
/// this project's per-test-database pattern
/// (<see cref="LoadTestSupport.LoadTestDatabaseResetterTests"/>). LoadTestSupport's
/// real <c>Program.cs</c> is intentionally not hosted here — it also wires
/// Redis and the MCP server, neither of which these read-only inspection
/// endpoints depend on.
/// </summary>
public sealed class InboxOutboxEndpointsIntegrationTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private string? _coreBankConnectionString;
    private string? _paymentsConnectionString;
    private IHost? _host;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _coreBankConnectionString = await fixture.CreateDatabaseAsync("inboxoutboxcore", cancellationToken);
        _paymentsConnectionString = await fixture.CreateDatabaseAsync("inboxoutboxpayments", cancellationToken);

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
                    services.AddRouting();
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapInboxEndpoints();
                        endpoints.MapOutboxEndpoints();
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
    public async Task GetCoreBankInbox_returns_seeded_messages_newest_first()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        coreBank.InboxMessages.Add(CoreBankInbox("older", new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc)));
        coreBank.InboxMessages.Add(CoreBankInbox("newer", new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc)));
        await coreBank.SaveChangesAsync(cancellationToken);

        var client = _host!.GetTestClient();
        var messages = await client.GetFromJsonAsync<List<InboxMessage>>("/corebank/inbox", cancellationToken);

        messages.Should().NotBeNull();
        messages!.Select(m => m.IdempotencyKey).Should().Equal("newer", "older");
    }

    [Fact]
    public async Task GetCoreBankInbox_caps_results_at_fifty_most_recent_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        var baseline = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 55; i++)
        {
            coreBank.InboxMessages.Add(CoreBankInbox($"key-{i:D2}", baseline.AddMinutes(i)));
        }
        await coreBank.SaveChangesAsync(cancellationToken);

        var client = _host!.GetTestClient();
        var messages = await client.GetFromJsonAsync<List<InboxMessage>>("/corebank/inbox", cancellationToken);

        messages.Should().HaveCount(50);
        // The 50 most recent of 55 rows minute-spaced from `baseline` are keys 5..54 — newest first.
        messages!.First().IdempotencyKey.Should().Be("key-54");
        messages.Last().IdempotencyKey.Should().Be("key-05");
    }

    [Fact]
    public async Task GetCoreBankOutbox_returns_seeded_domain_events_newest_first()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coreBank = CreateCoreBankContext();
        coreBank.MessagingOutboxMessages.Add(CoreBankOutbox("older", new DateTime(2026, 8, 30, 9, 0, 0, DateTimeKind.Utc)));
        coreBank.MessagingOutboxMessages.Add(CoreBankOutbox("newer", new DateTime(2026, 8, 30, 11, 0, 0, DateTimeKind.Utc)));
        await coreBank.SaveChangesAsync(cancellationToken);

        var client = _host!.GetTestClient();
        var messages = await client.GetFromJsonAsync<List<MessagingOutboxMessage>>("/corebank/outbox", cancellationToken);

        messages.Should().NotBeNull();
        messages!.Select(m => m.IdempotencyKey).Should().Equal("newer", "older");
    }

    [Fact]
    public async Task GetPaymentsInbox_returns_seeded_messages_newest_first()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var payments = CreatePaymentsContext();
        var older = PaymentsApiTestData.Inbox("older", "BalanceUpdated", "acct-1");
        older.ReceivedAt = new DateTime(2026, 8, 30, 8, 0, 0, DateTimeKind.Utc);
        var newer = PaymentsApiTestData.Inbox("newer", "BalanceUpdated", "acct-2");
        newer.ReceivedAt = new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc);
        payments.InboxMessages.Add(older);
        payments.InboxMessages.Add(newer);
        await payments.SaveChangesAsync(cancellationToken);

        var client = _host!.GetTestClient();
        var messages = await client.GetFromJsonAsync<List<CoreBankDemo.PaymentsAPI.Inbox.InboxMessage>>(
            "/payments/inbox", cancellationToken);

        messages.Should().NotBeNull();
        messages!.Select(m => m.TransactionId).Should().Equal("newer", "older");
    }

    [Fact]
    public async Task GetPaymentsOutbox_returns_seeded_payment_requests_newest_first()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var payments = CreatePaymentsContext();
        var older = PaymentsApiTestData.Outbox("older");
        older.CreatedAt = new DateTime(2026, 8, 30, 7, 0, 0, DateTimeKind.Utc);
        var newer = PaymentsApiTestData.Outbox("newer");
        newer.CreatedAt = new DateTime(2026, 8, 30, 9, 0, 0, DateTimeKind.Utc);
        payments.OutboxMessages.Add(older);
        payments.OutboxMessages.Add(newer);
        await payments.SaveChangesAsync(cancellationToken);

        var client = _host!.GetTestClient();
        var messages = await client.GetFromJsonAsync<List<CoreBankDemo.PaymentsAPI.Outbox.OutboxMessage>>(
            "/payments/outbox", cancellationToken);

        messages.Should().NotBeNull();
        messages!.Select(m => m.TransactionId).Should().Equal("newer", "older");
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
