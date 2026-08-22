using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

using static CoreBankDemo.ServiceDefaults.Tests.Configuration.ProcessingOptionsBindingTestSupport;

namespace CoreBankDemo.ServiceDefaults.Tests.Configuration;

public class OutboxProcessingOptionsTests
{
    [Fact]
    public void SectionName_is_OutboxProcessing()
    {
        OutboxProcessingOptions.SectionName.Should().Be("OutboxProcessing");
    }

    [Fact]
    public void Valid_configuration_binds_successfully()
    {
        var options = Bind<OutboxProcessingOptions>(OutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "4",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
        });

        options.PartitionCount.Should().Be(4);
        options.LockExpirySeconds.Should().Be(30);
        options.PollingIntervalMs.Should().Be(5000);
    }

    [Fact]
    public void Multiple_violations_are_all_reported_together_not_just_the_first()
    {
        var act = () => Bind<OutboxProcessingOptions>(OutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "0",
            ["LockExpirySeconds"] = "500",
            ["PollingIntervalMs"] = "5000",
        });

        var exception = act.Should().Throw<OptionsValidationException>().Which;

        exception.Failures.Should().Contain(f => f.Contains("PartitionCount"));
        exception.Failures.Should().Contain(f => f.Contains("LockExpirySeconds"));
    }

    [Fact]
    public void Omitted_PartitionCount_defaults_to_four()
    {
        var options = Bind<OutboxProcessingOptions>(OutboxProcessingOptions.SectionName, new()
        {
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
        });

        options.PartitionCount.Should().Be(4);
    }

    [Fact]
    public void LockExpirySeconds_out_of_range_fails_validation()
    {
        var act = () => Bind<OutboxProcessingOptions>(OutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "4",
            ["LockExpirySeconds"] = "0",
            ["PollingIntervalMs"] = "5000",
        });

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("LockExpirySeconds"));
    }

    [Fact]
    public void PartitionCount_out_of_range_fails_validation()
    {
        var act = () => Bind<OutboxProcessingOptions>(OutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "101",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
        });

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("PartitionCount"));
    }

    [Fact]
    public void PollingIntervalMs_below_minimum_fails_validation()
    {
        var act = () => Bind<OutboxProcessingOptions>(OutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "4",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "50",
        });

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("PollingIntervalMs"));
    }

    [Fact]
    public void Omitted_PollingIntervalMs_defaults_to_5000()
    {
        var options = Bind<OutboxProcessingOptions>(OutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "4",
            ["LockExpirySeconds"] = "30",
        });

        options.PollingIntervalMs.Should().Be(5_000);
    }

    [Fact]
    public void Upper_bound_values_bind_successfully()
    {
        var options = Bind<OutboxProcessingOptions>(OutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "100",
            ["LockExpirySeconds"] = "300",
            ["PollingIntervalMs"] = "300000",
        });

        options.PartitionCount.Should().Be(100);
        options.LockExpirySeconds.Should().Be(300);
        options.PollingIntervalMs.Should().Be(300_000);
    }

    [Fact]
    public void PartitionCount_still_defaults_to_four_when_the_whole_section_is_absent()
    {
        var options = BindSectionAbsent<OutboxProcessingOptions>(OutboxProcessingOptions.SectionName);

        options.PartitionCount.Should().Be(4);
    }
}
