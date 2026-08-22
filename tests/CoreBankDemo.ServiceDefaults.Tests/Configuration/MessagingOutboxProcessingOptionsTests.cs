using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

using static CoreBankDemo.ServiceDefaults.Tests.Configuration.ProcessingOptionsBindingTestSupport;

namespace CoreBankDemo.ServiceDefaults.Tests.Configuration;

public class MessagingOutboxProcessingOptionsTests
{
    [Fact]
    public void SectionName_is_MessagingOutboxProcessing()
    {
        MessagingOutboxProcessingOptions.SectionName.Should().Be("MessagingOutboxProcessing");
    }

    [Fact]
    public void Valid_configuration_binds_successfully()
    {
        var options = Bind<MessagingOutboxProcessingOptions>(MessagingOutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "4",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
            ["PubSubName"] = "pubsub",
            ["TopicName"] = "transaction-events",
        });

        options.PartitionCount.Should().Be(4);
        options.LockExpirySeconds.Should().Be(30);
        options.PollingIntervalMs.Should().Be(5000);
        options.PubSubName.Should().Be("pubsub");
        options.TopicName.Should().Be("transaction-events");
    }

    [Fact]
    public void Multiple_violations_are_all_reported_together_not_just_the_first()
    {
        var act = () => Bind<MessagingOutboxProcessingOptions>(MessagingOutboxProcessingOptions.SectionName, new()
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
        var options = Bind<MessagingOutboxProcessingOptions>(MessagingOutboxProcessingOptions.SectionName, new()
        {
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
        });

        options.PartitionCount.Should().Be(4);
    }

    [Fact]
    public void Omitted_PubSubName_defaults_to_pubsub()
    {
        var options = Bind<MessagingOutboxProcessingOptions>(MessagingOutboxProcessingOptions.SectionName, new()
        {
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
        });

        options.PubSubName.Should().Be("pubsub");
    }

    [Fact]
    public void Omitted_TopicName_defaults_to_transaction_events()
    {
        var options = Bind<MessagingOutboxProcessingOptions>(MessagingOutboxProcessingOptions.SectionName, new()
        {
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
        });

        options.TopicName.Should().Be("transaction-events");
    }

    [Fact]
    public void Blank_TopicName_fails_validation_and_names_the_field()
    {
        // TopicName defaults to "transaction-events", so *omitting* the key from
        // config binds to that default and passes validation (see
        // Omitted_TopicName_defaults_to_transaction_events above). The I/O matrix's
        // "missing required field" row is exercised here with an explicit blank
        // value, since that is what actually trips [Required]/[MinLength(1)] and
        // names TopicName in the resulting exception — a truly absent key can never
        // fail validation while a non-empty default exists.
        var act = () => Bind<MessagingOutboxProcessingOptions>(MessagingOutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "4",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
            ["PubSubName"] = "pubsub",
            ["TopicName"] = "",
        });

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("TopicName"));
    }

    [Fact]
    public void Blank_PubSubName_fails_validation_and_names_the_field()
    {
        var act = () => Bind<MessagingOutboxProcessingOptions>(MessagingOutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "4",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
            ["PubSubName"] = "",
            ["TopicName"] = "transaction-events",
        });

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("PubSubName"));
    }

    [Fact]
    public void Whitespace_only_PubSubName_fails_validation_and_names_the_field()
    {
        var act = () => Bind<MessagingOutboxProcessingOptions>(MessagingOutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "4",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
            ["PubSubName"] = " ",
            ["TopicName"] = "transaction-events",
        });

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("PubSubName"));
    }

    [Fact]
    public void Whitespace_only_TopicName_fails_validation_and_names_the_field()
    {
        var act = () => Bind<MessagingOutboxProcessingOptions>(MessagingOutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "4",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
            ["PubSubName"] = "pubsub",
            ["TopicName"] = "   ",
        });

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("TopicName"));
    }

    [Fact]
    public void Upper_bound_values_bind_successfully()
    {
        var options = Bind<MessagingOutboxProcessingOptions>(MessagingOutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "100",
            ["LockExpirySeconds"] = "300",
            ["PollingIntervalMs"] = "300000",
            ["PubSubName"] = "pubsub",
            ["TopicName"] = "transaction-events",
        });

        options.PartitionCount.Should().Be(100);
        options.LockExpirySeconds.Should().Be(300);
        options.PollingIntervalMs.Should().Be(300_000);
    }

    [Fact]
    public void PartitionCount_still_defaults_to_four_when_the_whole_section_is_absent()
    {
        var options = BindSectionAbsent<MessagingOutboxProcessingOptions>(MessagingOutboxProcessingOptions.SectionName);

        options.PartitionCount.Should().Be(4);
    }

    [Fact]
    public void Blank_TopicName_and_out_of_range_PartitionCount_are_both_reported_together()
    {
        // The existing multi-violation coverage
        // (Multiple_violations_are_all_reported_together_not_just_the_first) only
        // exercises two base-class fields together. This proves a
        // MessagingOutboxProcessingOptions-only field violation (TopicName) and a
        // base-class violation (PartitionCount) land in the same
        // OptionsValidationException rather than one masking the other.
        var act = () => Bind<MessagingOutboxProcessingOptions>(MessagingOutboxProcessingOptions.SectionName, new()
        {
            ["PartitionCount"] = "0",
            ["LockExpirySeconds"] = "30",
            ["PollingIntervalMs"] = "5000",
            ["PubSubName"] = "pubsub",
            ["TopicName"] = "",
        });

        var exception = act.Should().Throw<OptionsValidationException>().Which;

        exception.Failures.Should().Contain(f => f.Contains("PartitionCount"));
        exception.Failures.Should().Contain(f => f.Contains("TopicName"));
    }
}
