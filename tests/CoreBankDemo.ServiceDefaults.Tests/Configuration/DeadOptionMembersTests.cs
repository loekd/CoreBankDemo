using System.Reflection;
using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults.Configuration;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.Configuration;

/// <summary>
/// Ruling A4: <c>LockRenewIntervalSeconds</c> was bound and DataAnnotations-
/// validated in the brownfield <c>ProcessingOptionsBase</c> but never read by
/// any consumer — locks are expiry-based with no renewal (AD-7). This class
/// guards against that defect (or any other unread member) recurring: every
/// public instance property on a rebuilt processing-options type must have an
/// entry in <see cref="KnownConsumers.Map"/> naming a real reader. Adding a
/// member without updating the map fails <see cref="No_member_exists_outside_the_known_consumers_list"/>.
/// <para>
/// Known limitation: <see cref="Every_known_consumer_path_exists_on_disk"/>
/// only proves the named path exists — it is a name-and-path-existence check,
/// not real usage verification. It closes the "made up a plausible-sounding
/// description" gaming vector (a free-text description could claim anything),
/// but proving the named file actually reads the member would require symbol
/// analysis, which is out of scope here.
/// </para>
/// </summary>
public class DeadOptionMembersTests
{
    [Theory]
    [InlineData(typeof(InboxProcessingOptions))]
    [InlineData(typeof(OutboxProcessingOptions))]
    [InlineData(typeof(MessagingOutboxProcessingOptions))]
    public void No_member_exists_outside_the_known_consumers_list(Type optionsType)
    {
        var actualMemberNames = optionsType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        KnownConsumers.Map.Should().ContainKey(optionsType,
            $"{optionsType.Name} must have a known-consumers entry before it can be reflected over");

        var knownMemberNames = KnownConsumers.Map[optionsType].Keys;

        actualMemberNames.Should().BeEquivalentTo(knownMemberNames,
            $"every public member of {optionsType.Name} must be named in the known-consumers list, " +
            "or it is a bound-but-dead option (ruling A4)");
    }

    [Fact]
    public void Every_known_consumer_path_exists_on_disk()
    {
        var repoRoot = FindRepoRoot();

        foreach (var (optionsType, members) in KnownConsumers.Map)
        {
            foreach (var (memberName, relativePath) in members)
            {
                var fullPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

                File.Exists(fullPath).Should().BeTrue(
                    $"{optionsType.Name}.{memberName}'s known-consumers entry ('{relativePath}') must name a " +
                    "file that actually exists in the repo, or it is an unverifiable claim");
            }
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CoreBankDemo.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"Could not locate repo root (CoreBankDemo.sln) walking up from {AppContext.BaseDirectory}");
        }

        return directory.FullName;
    }

    [Theory]
    [InlineData(typeof(ProcessingOptionsBase))]
    [InlineData(typeof(InboxProcessingOptions))]
    [InlineData(typeof(OutboxProcessingOptions))]
    [InlineData(typeof(MessagingOutboxProcessingOptions))]
    public void LockRenewIntervalSeconds_does_not_exist(Type optionsType)
    {
        optionsType.GetProperty("LockRenewIntervalSeconds", BindingFlags.Public | BindingFlags.Instance)
            .Should().BeNull($"{optionsType.Name} must never bind a renewal-interval member (AD-7, ruling A4)");
    }

    /// <summary>
    /// Hand-maintained record of who reads each processing-options member,
    /// keyed by repo-relative path to the real consumer file rather than a
    /// free-text description — a description string can't be checked for
    /// truthfulness, but a path can at least be checked for existence (see
    /// <see cref="Every_known_consumer_path_exists_on_disk"/>). Story 3.1
    /// introduces these types with no consumer yet inside
    /// <c>CoreBankDemo.Rebuild.slnf</c> (epic-3 context: "3.1's options rebuild
    /// has no consumer inside Messaging today"); the brownfield
    /// CoreBankAPI/PaymentsAPI processors below already bind and read the
    /// equivalently-named options today and are the future rebuild's direct
    /// pattern source, so they stand in as the named reader until epic 4/5
    /// wires the rebuilt processors up to these exact types.
    /// </summary>
    private static class KnownConsumers
    {
        public static readonly IReadOnlyDictionary<Type, IReadOnlyDictionary<string, string>> Map =
            new Dictionary<Type, IReadOnlyDictionary<string, string>>
            {
                [typeof(InboxProcessingOptions)] = new Dictionary<string, string>
                {
                    ["PartitionCount"] = "CoreBankDemo.PaymentsAPI/Inbox/InboxProcessor.cs",
                    ["LockExpirySeconds"] = "CoreBankDemo.PaymentsAPI/Inbox/InboxProcessor.cs",
                    ["PollingIntervalMs"] = "CoreBankDemo.PaymentsAPI/Inbox/InboxProcessor.cs",
                },
                [typeof(OutboxProcessingOptions)] = new Dictionary<string, string>
                {
                    ["PartitionCount"] = "CoreBankDemo.PaymentsAPI/Outbox/OutboxProcessor.cs",
                    ["LockExpirySeconds"] = "CoreBankDemo.PaymentsAPI/Outbox/OutboxProcessor.cs",
                    ["PollingIntervalMs"] = "CoreBankDemo.PaymentsAPI/Outbox/OutboxProcessor.cs",
                },
                [typeof(MessagingOutboxProcessingOptions)] = new Dictionary<string, string>
                {
                    ["PartitionCount"] = "CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxProcessor.cs",
                    ["LockExpirySeconds"] = "CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxProcessor.cs",
                    ["PollingIntervalMs"] = "CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxProcessor.cs",
                    ["PubSubName"] = "CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxProcessor.cs",
                    ["TopicName"] = "CoreBankDemo.CoreBankAPI/Outbox/MessagingOutboxProcessor.cs",
                },
            };
    }
}
