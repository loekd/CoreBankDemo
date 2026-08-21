using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.Messaging.Tests;

public class PartitionHelperTests
{
    /// <summary>
    /// Known vectors captured by executing the legacy PartitionHelper
    /// (FNV-1a over chars, prime 16777619, offset basis 2166136261,
    /// Math.Abs % count) at partitionCount = 4 before the epic-2 demolition.
    /// Any change to these ids breaks ordering compatibility with existing rows.
    /// </summary>
    public static TheoryData<string, int> LegacyKnownVectors => new()
    {
        // GUID-string keys
        { "3f2504e0-4f89-11d3-9a0c-0305e82c3301", 0 },
        { "a1b2c3d4-e5f6-7890-abcd-ef1234567890", 1 },
        { "00000000-0000-0000-0000-000000000000", 3 },
        { "D9428888-122B-11E1-B85C-61CD3CBB3210", 1 },
        // IBAN keys
        { "NL91ABNA0417164300", 3 },
        { "DE89370400440532013000", 1 },
        { "GB29NWBK60161331926819", 3 },
        // Plain keys — casing is significant and preserved
        { "payment-key-001", 1 },
        { "PAYMENT-KEY-001", 3 },
        // Unicode keys (char-based hashing, incl. surrogate pairs)
        { "héllo wörld ünïcode-Ω", 2 },
        { "支付-注文-😀-1234", 3 },
    };

    [Theory]
    [MemberData(nameof(LegacyKnownVectors))]
    public void Known_legacy_vectors_produce_identical_partition_ids(string key, int expectedPartitionId)
    {
        PartitionHelper.GetPartitionId(key, 4).Should().Be(expectedPartitionId);
    }

    [Fact]
    public void Very_long_key_matches_legacy_vector()
    {
        // Legacy-computed: new string('a', 1024) at partitionCount 4 => 3
        PartitionHelper.GetPartitionId(new string('a', 1024), 4).Should().Be(3);
    }

    [Theory]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("NL91ABNA0417164300")]
    [InlineData("PAYMENT-KEY-001")]
    [InlineData("支付-注文-😀-1234")]
    public void Same_key_returns_same_partition_id_on_repeated_calls(string key)
    {
        var first = PartitionHelper.GetPartitionId(key, 4);

        for (var i = 0; i < 100; i++)
        {
            PartitionHelper.GetPartitionId(key, 4).Should().Be(first);
        }
    }

    [Theory]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef1234567890")]
    [InlineData("NL91ABNA0417164300")]
    [InlineData("DE89370400440532013000")]
    [InlineData("payment-key-001")]
    [InlineData("héllo wörld ünïcode-Ω")]
    [InlineData("")]
    public void Partition_id_is_always_within_range_for_count_4(string key)
    {
        var id = PartitionHelper.GetPartitionId(key, 4);

        id.Should().BeInRange(0, 3);
    }

    [Fact]
    public void Empty_key_is_deterministic_in_range_and_does_not_throw()
    {
        // Spec I/O matrix: degenerate keys yield a deterministic id, no throw.
        // FNV-1a of "" is the offset basis: Math.Abs((int)2166136261) % 4 == 3.
        var act = () => PartitionHelper.GetPartitionId(string.Empty, 4);

        act.Should().NotThrow();
        PartitionHelper.GetPartitionId(string.Empty, 4).Should().Be(3);
    }

    [Fact]
    public void Very_long_key_is_deterministic_and_in_range()
    {
        var key = new string('Ω', 10_000) + new string('z', 10_000);

        var first = PartitionHelper.GetPartitionId(key, 4);

        first.Should().BeInRange(0, 3);
        PartitionHelper.GetPartitionId(key, 4).Should().Be(first);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Non_positive_partition_count_throws_argument_out_of_range(int partitionCount)
    {
        var act = () => PartitionHelper.GetPartitionId("any-key", partitionCount);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("partitionCount");
    }

    [Fact]
    public void Null_key_throws_argument_null()
    {
        var act = () => PartitionHelper.GetPartitionId(null!, 4);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("key");
    }

    [Fact]
    public void Partition_count_one_maps_every_key_to_partition_zero()
    {
        PartitionHelper.GetPartitionId("3f2504e0-4f89-11d3-9a0c-0305e82c3301", 1).Should().Be(0);
        PartitionHelper.GetPartitionId("NL91ABNA0417164300", 1).Should().Be(0);
    }
}
