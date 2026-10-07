using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.Messaging.Tests;

public class PartitionHelperTests
{
    /// <summary>
    /// Known vectors for <c>fmix32(fnv1a(key)) % 4</c> (ADR-026): 32-bit FNV-1a
    /// over chars (prime 16777619, offset basis 2166136261), run through
    /// MurmurHash3's fmix32 finalizer, unsigned modulus. Any change to these
    /// ids breaks ordering compatibility with existing rows.
    /// </summary>
    public static TheoryData<string, int> KnownVectors => new()
    {
        // GUID-string keys
        { "3f2504e0-4f89-11d3-9a0c-0305e82c3301", 1 },
        { "a1b2c3d4-e5f6-7890-abcd-ef1234567890", 2 },
        { "00000000-0000-0000-0000-000000000000", 0 },
        { "D9428888-122B-11E1-B85C-61CD3CBB3210", 0 },
        // IBAN keys
        { "NL91ABNA0417164300", 1 },
        { "DE89370400440532013000", 0 },
        { "GB29NWBK60161331926819", 3 },
        // Plain keys — casing is significant and preserved
        { "payment-key-001", 3 },
        { "PAYMENT-KEY-001", 2 },
        // Unicode keys (char-based hashing, incl. surrogate pairs)
        { "héllo wörld ünïcode-Ω", 0 },
        { "支付-注文-😀-1234", 1 },
    };

    [Theory]
    [MemberData(nameof(KnownVectors))]
    public void Known_vectors_produce_the_pinned_partition_ids(string key, int expectedPartitionId)
    {
        PartitionHelper.GetPartitionId(key, 4).Should().Be(expectedPartitionId);
    }

    [Fact]
    public void Very_long_key_matches_known_vector()
    {
        PartitionHelper.GetPartitionId(new string('a', 1024), 4).Should().Be(0);
    }

    /// <summary>
    /// ADR-026's motivating defect: bare FNV-1a mod 4 put all three Regular
    /// demo accounts in partition 3 and the ten load-test accounts in
    /// partitions 1 and 3 only, because the low two bits of the hash depend
    /// only on the low two bits of each char. The finalizer spreads them.
    /// </summary>
    [Theory]
    [InlineData("NL91ABNA0417164300", 1)]
    [InlineData("NL20INGB0001234567", 3)]
    [InlineData("NL39RABO0300065264", 2)]
    [InlineData("NL01LOAD0000000001", 0)]
    [InlineData("NL02LOAD0000000002", 1)]
    [InlineData("NL03LOAD0000000003", 0)]
    [InlineData("NL04LOAD0000000004", 1)]
    [InlineData("NL05LOAD0000000005", 0)]
    [InlineData("NL06LOAD0000000006", 1)]
    [InlineData("NL07LOAD0000000007", 2)]
    [InlineData("NL08LOAD0000000008", 0)]
    [InlineData("NL09LOAD0000000009", 1)]
    [InlineData("NL10LOAD0000000010", 2)]
    public void Account_numbers_map_to_the_partitions_adr_026_records(string accountNumber, int expectedPartitionId)
    {
        PartitionHelper.GetPartitionId(accountNumber, 4).Should().Be(expectedPartitionId);
    }

    [Fact]
    public void Account_shaped_keys_are_spread_over_all_four_partitions()
    {
        var accounts = new[] { "NL91ABNA0417164300", "NL20INGB0001234567", "NL39RABO0300065264" }
            .Concat(Enumerable.Range(1, 10).Select(i => $"NL{i:D2}LOAD{i:D10}"));

        accounts.Select(account => PartitionHelper.GetPartitionId(account, 4))
            .Distinct()
            .Should().BeEquivalentTo([0, 1, 2, 3]);
    }

    /// <summary>
    /// Pins the finalizer to MurmurHash3's published fmix32, not a homegrown
    /// mix: fmix32(0) = 0 and fmix32(1) = 0x514E28B7 are the reference values.
    /// </summary>
    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 0x514E28B7u)]
    [InlineData(0xFFFFFFFFu, 0x81F16F39u)]
    [InlineData(2166136261u, 0xAB3E7C0Bu)]
    public void Finalizer_is_murmurhash3_fmix32(uint input, uint expected)
    {
        PartitionHelper.Fmix32(input).Should().Be(expected);
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
        // FNV-1a of "" is the offset basis 0x811C9DC5; fmix32 of that is
        // 0xAB3E7C0B, and 0xAB3E7C0B % 4 == 3.
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

    [Fact]
    public void Partition_count_larger_than_the_hash_range_of_a_signed_int_is_handled_unsigned()
    {
        // The modulus is taken on the unsigned hash, so a count above
        // int.MaxValue / 2 still yields a non-negative id below the count
        // (the old Math.Abs mapping had an int.MinValue hole here).
        var id = PartitionHelper.GetPartitionId("NL91ABNA0417164300", int.MaxValue);

        id.Should().BeInRange(0, int.MaxValue - 1);
    }
}
