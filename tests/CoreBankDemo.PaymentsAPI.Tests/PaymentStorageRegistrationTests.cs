using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

public class PaymentStorageRegistrationTests
{
    [Fact]
    public void Valid_configuration_registers_storage_services()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["OutboxProcessing:PartitionCount"] = "4",
            ["OutboxProcessing:LockExpirySeconds"] = "30",
            ["OutboxProcessing:PollingIntervalMs"] = "200"
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
        provider.GetRequiredService<IOptions<OutboxProcessingOptions>>().Value.PartitionCount.Should().Be(4);
        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOutboxRepository>().Should().BeOfType<OutboxRepository>();
        scope.ServiceProvider.GetRequiredService<IPaymentStorageHandler>().Should().BeOfType<PaymentStorageHandler>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("3")]
    [InlineData("5")]
    public void Missing_or_non_four_partition_configuration_fails_validation(string? partitionCount)
    {
        var values = new Dictionary<string, string?>
        {
            ["OutboxProcessing:LockExpirySeconds"] = "30",
            ["OutboxProcessing:PollingIntervalMs"] = "200"
        };
        if (partitionCount is not null)
        {
            values["OutboxProcessing:PartitionCount"] = partitionCount;
        }

        using var provider = BuildProvider(values);
        var startupValidator = provider.GetRequiredService<IStartupValidator>();
        var act = startupValidator.Validate;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Existing_time_provider_is_preserved()
    {
        var custom = new CustomTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(custom);
        services.AddPaymentStorage(Configuration([]));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(custom);
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddPaymentStorage(Configuration(values));
        services.AddDbContext<PaymentsDbContext>(
            options => options.UseSqlite("Data Source=:memory:"));
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration(IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class CustomTimeProvider : TimeProvider;
}
