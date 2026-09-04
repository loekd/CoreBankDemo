using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using CoreBankDemo.Messaging;
using CoreBankDemo.PaymentsAPI;
using CoreBankDemo.PaymentsAPI.Models;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.Persistence.IntegrationTests.Infrastructure;
using CoreBankDemo.ServiceDefaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreBankDemo.Persistence.IntegrationTests.PaymentsApi;

[Collection(nameof(PaymentsEntryPointCollection))]
public class PaymentIntakeWiringTests(PostgresContainerFixture fixture)
    : PaymentsPostgresTestBase(fixture)
{
    [Fact]
    public async Task Real_entry_point_stores_payment_and_uses_the_manual_validation_envelope()
    {
        await using var environment = PaymentsEntryPointEnvironment.Apply(ConnectionString);
        await using var factory = new PaymentsApiFactory();
        using var client = factory.CreateClient();
        const string idempotencyKey = "payment-intake-wiring";
        using var validRequest = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = JsonContent.Create(new PaymentRequest(
                "NL91ABNA0417164300",
                "NL20INGB0001234567",
                12.34m,
                "EUR"))
        };
        validRequest.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey)
            .Should().BeTrue();

        var validResponse = await client.SendAsync(
            validRequest,
            TestContext.Current.CancellationToken);

        validResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        validResponse.Headers.Location!.ToString().Should().Be($"/api/payments/{idempotencyKey}");
        var acknowledgementJson = await validResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var acknowledgementDocument = JsonDocument.Parse(acknowledgementJson);
        acknowledgementDocument.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().Equal(
                "paymentId",
                "transactionId",
                "status",
                "amount",
                "currency",
                "processedAt");
        var acknowledgement = JsonSerializer.Deserialize<PaymentResponse>(
            acknowledgementJson,
            JsonSerializerOptions.Web);
        acknowledgement.Should().NotBeNull();
        acknowledgement!.PaymentId.Should().Be(idempotencyKey);
        acknowledgement.TransactionId.Should().Be(idempotencyKey);
        acknowledgement.Status.Should().Be(MessageConstants.Status.Pending);
        acknowledgement.Amount.Should().Be(12.34m);
        acknowledgement.Currency.Should().Be("EUR");

        await using (var afterValidRequest = CreateContext())
        {
            var stored = await afterValidRequest.OutboxMessages
                .AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
            stored.IdempotencyKey.Should().Be(idempotencyKey);
            stored.TransactionId.Should().Be(idempotencyKey);
            stored.Amount.Should().Be(12.34m);
            stored.Currency.Should().Be("EUR");
            stored.Status.Should().Be(MessageConstants.Status.Pending);
            acknowledgement.ProcessedAt.Should().BeCloseTo(
                new DateTimeOffset(DateTime.SpecifyKind(stored.CreatedAt, DateTimeKind.Utc)),
                TimeSpan.FromMicroseconds(1));
        }

        using var duplicateRequest = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = JsonContent.Create(new PaymentRequest(
                "NL91ABNA0417164300",
                "NL20INGB0001234567",
                999.99m,
                "USD"))
        };
        duplicateRequest.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey)
            .Should().BeTrue();

        var duplicateResponse = await client.SendAsync(
            duplicateRequest,
            TestContext.Current.CancellationToken);

        duplicateResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        duplicateResponse.Headers.Location!.ToString().Should().Be($"/api/payments/{idempotencyKey}");
        var duplicateAcknowledgement = await duplicateResponse.Content.ReadFromJsonAsync<PaymentResponse>(
            TestContext.Current.CancellationToken);
        duplicateAcknowledgement.Should().BeEquivalentTo(
            acknowledgement,
            options => options.Excluding(response => response.ProcessedAt));
        duplicateAcknowledgement!.ProcessedAt.Should().BeCloseTo(
            acknowledgement.ProcessedAt,
            TimeSpan.FromMicroseconds(1));

        var invalidResponse = await client.PostAsJsonAsync(
            "/api/payments",
            new PaymentRequest("short", "short", 0m, "eur"),
            TestContext.Current.CancellationToken);

        invalidResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var validation = await invalidResponse.Content.ReadFromJsonAsync<ValidationErrorResponse>(
            TestContext.Current.CancellationToken);
        validation.Should().NotBeNull();
        validation!.Errors.Should().BeEquivalentTo(
        [
            "FromAccount must be between 15 and 34 characters",
            "ToAccount must be between 15 and 34 characters",
            "Amount must be between 0.01 and 1,000,000",
            "Currency must be 3 uppercase letters"
        ]);

        await using var afterInvalidRequest = CreateContext();
        (await afterInvalidRequest.OutboxMessages.CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(1);
    }

    // The integration project references two APIs that both export a global
    // Program type. An application type from PaymentsAPI selects that same
    // entry assembly without introducing an ambiguous Program reference.
    private sealed class PaymentsApiFactory : WebApplicationFactory<PaymentsDbContext>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDistributedLockService>();
                services.AddSingleton<IDistributedLockService, NonAcquiringLockService>();

                var outboxProcessor = services.SingleOrDefault(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService) &&
                    descriptor.ImplementationType == typeof(PaymentsOutboxProcessor));
                if (outboxProcessor is not null)
                {
                    services.Remove(outboxProcessor);
                }
            });
        }
    }

    private sealed record ValidationErrorResponse(string[] Errors);

    private sealed class NonAcquiringLockService : IDistributedLockService
    {
        public Task<bool> ExecuteWithLockAsync(
            string lockName,
            int lockExpirySeconds,
            Func<CancellationToken, Task> workload,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
