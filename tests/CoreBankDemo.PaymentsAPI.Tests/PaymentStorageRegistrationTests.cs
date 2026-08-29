using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Controllers;
using CoreBankDemo.PaymentsAPI.Handlers;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
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

    [Fact]
    public void Production_appsettings_passes_startup_validation_with_four_partitions()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(
                Path.Combine(FindRepoRoot(), "CoreBankDemo.PaymentsAPI", "appsettings.json"),
                optional: false)
            .Build();
        var services = new ServiceCollection();
        services.AddPaymentStorage(configuration);
        services.AddDbContext<PaymentsDbContext>(
            options => options.UseSqlite("Data Source=:memory:"));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
        provider.GetRequiredService<IOptions<OutboxProcessingOptions>>().Value.PartitionCount.Should().Be(4);
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
    public void Deployed_mvc_options_suppress_the_automatic_model_state_invalid_filter()
    {
        var services = new ServiceCollection();
        services.AddPaymentIntake();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<ApiBehaviorOptions>>()
            .Value.SuppressModelStateInvalidFilter.Should().BeTrue();
    }

    [Fact]
    public async Task Deployed_endpoint_mapping_exposes_post_payments_route()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddPaymentIntake();
        // The in-process test host's entry assembly is the test project itself, so MVC's
        // default ApplicationPartManager discovery never finds CoreBankDemo.PaymentsAPI's
        // controllers here (unlike the real app, where PaymentsAPI is the entry assembly).
        // Registering the part explicitly restores real controller discovery for this test.
        builder.Services.AddControllers().AddApplicationPart(typeof(PaymentsController).Assembly);
        await using var app = builder.Build();

        app.MapPaymentIntake();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == "api/Payments");
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
            .Should().Equal(HttpMethods.Post);
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

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CoreBankDemo.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed class CustomTimeProvider : TimeProvider;
}
