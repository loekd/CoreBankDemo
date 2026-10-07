namespace CoreBankDemo.Messaging;

/// <summary>
/// Maps a partition key to a partition id (AD-4, ADR-026): 32-bit FNV-1a over
/// the key's chars, run through MurmurHash3's <c>fmix32</c> finalizer, then
/// <c>% partitionCount</c> on the unsigned result. The finalizer exists
/// because bare FNV-1a mod 4 reads only the low two bits of each char, so
/// account-shaped keys collapsed into one or two partitions (ADR-026).
/// Existing rows depend on identical partition assignment, so the mapping is
/// pinned by known-vector tests and must not change again without an ADR.
/// </summary>
/// <remarks>
/// Which string is the key is the caller's decision per store: payment
/// commands (payments outbox, CoreBank inbox) partition on the debtor account
/// (<c>FromAccount</c>) so one account's debits never overtake each other;
/// the event stores partition on the transaction id or, for
/// <c>balance.updated</c>, the account number (ADR-026).
/// </remarks>
public static class PartitionHelper
{
    /// <summary>
    /// Computes a deterministic partition id for <paramref name="key"/>,
    /// in the range [0, <paramref name="partitionCount"/>).
    /// </summary>
    /// <param name="key">The partition key to hash. Casing is significant. Never throws for any non-null key.</param>
    /// <param name="partitionCount">Total number of partitions; must be positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="partitionCount"/> is not positive.</exception>
    public static int GetPartitionId(string key, int partitionCount)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);

        return (int)(Fmix32(ComputeFnv1aHash(key)) % (uint)partitionCount);
    }

    /// <summary>
    /// MurmurHash3's 32-bit finalizer (Austin Appleby, public domain): an
    /// avalanche step so every input bit influences every output bit, which
    /// FNV-1a on its own does not give for the low bits. Reference values:
    /// <c>fmix32(0) == 0</c>, <c>fmix32(1) == 0x514E28B7</c>.
    /// </summary>
    internal static uint Fmix32(uint h)
    {
        unchecked
        {
            h ^= h >> 16;
            h *= 0x85EBCA6B;
            h ^= h >> 13;
            h *= 0xC2B2AE35;
            h ^= h >> 16;
            return h;
        }
    }

    /// <summary>
    /// 32-bit FNV-1a over the chars of the string (not UTF-8 bytes). An empty
    /// key hashes to the offset basis, yielding a deterministic id.
    /// </summary>
    private static uint ComputeFnv1aHash(string key)
    {
        unchecked
        {
            const uint fnvPrime = 16777619;     // 0x01000193
            uint hash = 2166136261;             // FNV offset basis 0x811C9DC5

            foreach (char c in key)
            {
                hash ^= c;
                hash *= fnvPrime;
            }

            return hash;
        }
    }
}
