using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.Extensions;

/// <summary>
/// Story 3.4: proves each of the three <c>Add*ProcessingOptions</c> helpers
/// wires the exact pipeline the story-3.1 options types are documented to
/// require — <c>AddOptions&lt;T&gt;().BindConfiguration(...)
/// .ValidateDataAnnotations().ValidateOnStart()</c> — rather than just
/// re-testing the options types themselves (that's
/// <c>Configuration/*ProcessingOptionsTests.cs</c>'s job).
/// <para>
/// <c>BindConfiguration</c> is proven by resolving <see cref="IOptions{TOptions}"/>
/// and checking the bound values came from configuration.
/// <c>ValidateDataAnnotations</c> alone would already make an invalid
/// <c>.Value</c> access throw, so it can't distinguish itself from
/// <c>ValidateOnStart</c> being wired too. <c>ValidateOnStart</c> is proven
/// definitively by resolving the framework's own
/// <see cref="IStartupValidator"/> (registered once per options type by
/// <c>.ValidateOnStart()</c>) and calling its public <c>Validate()</c>
/// method directly — the same call a real host's startup path makes — without
/// needing to run an actual <see cref="IHost"/>.
/// </para>
/// </summary>
public class ProcessingOptionsRegistrationTests
{
    private static WebApplicationBuilder CreateBuilder(string sectionName, Dictionary<string, string?> values)
    {
        var builder = WebApplication.CreateSlimBuilder();
        var prefixed = values.ToDictionary(kv => $"{sectionName}:{kv.Key}", kv => kv.Value);
        builder.Configuration.AddInMemoryCollection(prefixed);
        return builder;
    }

    // ---- AddInboxProcessingOptions ----

    [Fact]
    public void AddInboxProcessingOptions_binds_configuration_into_IOptions()
    {
        var builder = CreateBuilder(InboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "4",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
        });

        builder.AddInboxProcessingOptions();
        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<InboxProcessingOptions>>().Value;

        options.PartitionCount.Should().Be(4);
        options.LockExpirySeconds.Should().Be(30);
        options.PollingIntervalMs.Should().Be(5000);
    }

    [Fact]
    public void AddInboxProcessingOptions_wires_ValidateOnStart_so_invalid_configuration_fails_the_startup_validator()
    {
        var builder = CreateBuilder(InboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "0",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
        });

        builder.AddInboxProcessingOptions();
        using var provider = builder.Services.BuildServiceProvider();
        var startupValidator = provider.GetRequiredService<IStartupValidator>();

        var act = startupValidator.Validate;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("PartitionCount"));
    }

    [Fact]
    public void AddInboxProcessingOptions_rejects_a_partition_count_other_than_four()
    {
        var builder = CreateBuilder(InboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "2",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
        });

        builder.AddInboxProcessingOptions();
        using var provider = builder.Services.BuildServiceProvider();
        var act = provider.GetRequiredService<IStartupValidator>().Validate;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("exactly 4"));
    }

    // ---- AddOutboxProcessingOptions ----

    [Fact]
    public void AddOutboxProcessingOptions_binds_configuration_into_IOptions()
    {
        var builder = CreateBuilder(OutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "4",
            ["LockExpirySeconds"] = "45",
            ["PollingIntervalMs"] = "6000",
        });

        builder.AddOutboxProcessingOptions();
        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OutboxProcessingOptions>>().Value;

        options.PartitionCount.Should().Be(4);
        options.LockExpirySeconds.Should().Be(45);
        options.PollingIntervalMs.Should().Be(6000);
    }

    [Fact]
    public void AddOutboxProcessingOptions_wires_ValidateOnStart_so_invalid_configuration_fails_the_startup_validator()
    {
        var builder = CreateBuilder(OutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "4",
            ["LockExpirySeconds"] = "500",
            ["PollingIntervalMs"] = "6000",
        });

        builder.AddOutboxProcessingOptions();
        using var provider = builder.Services.BuildServiceProvider();
        var startupValidator = provider.GetRequiredService<IStartupValidator>();

        var act = startupValidator.Validate;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("LockExpirySeconds"));
    }

    [Fact]
    public void AddOutboxProcessingOptions_rejects_a_partition_count_other_than_four()
    {
        var builder = CreateBuilder(OutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "3",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
        });

        builder.AddOutboxProcessingOptions();
        using var provider = builder.Services.BuildServiceProvider();
        var act = provider.GetRequiredService<IStartupValidator>().Validate;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("exactly 4"));
    }

    // ---- AddMessagingOutboxProcessingOptions ----

    [Fact]
    public void AddMessagingOutboxProcessingOptions_binds_configuration_into_IOptions()
    {
        var builder = CreateBuilder(MessagingOutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "4",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
            ["PubSubName"] = "custom-pubsub",
            ["TopicName"] = "custom-topic",
        });

        builder.AddMessagingOutboxProcessingOptions();
        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MessagingOutboxProcessingOptions>>().Value;

        options.PartitionCount.Should().Be(4);
        options.PubSubName.Should().Be("custom-pubsub");
        options.TopicName.Should().Be("custom-topic");
    }

    [Fact]
    public void AddMessagingOutboxProcessingOptions_wires_ValidateOnStart_so_invalid_configuration_fails_the_startup_validator()
    {
        var builder = CreateBuilder(MessagingOutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "4",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
            ["PubSubName"] = "   ",
            ["TopicName"] = "custom-topic",
        });

        builder.AddMessagingOutboxProcessingOptions();
        using var provider = builder.Services.BuildServiceProvider();
        var startupValidator = provider.GetRequiredService<IStartupValidator>();

        var act = startupValidator.Validate;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("PubSubName"));
    }

    [Fact]
    public void AddMessagingOutboxProcessingOptions_rejects_a_partition_count_other_than_four()
    {
        var builder = CreateBuilder(MessagingOutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "5",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
            ["PubSubName"] = "pubsub",
            ["TopicName"] = "events",
        });

        builder.AddMessagingOutboxProcessingOptions();
        using var provider = builder.Services.BuildServiceProvider();
        var act = provider.GetRequiredService<IStartupValidator>().Validate;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("exactly 4"));
    }
}
