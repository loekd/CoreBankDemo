namespace CoreBankDemo.Messaging;

/// <summary>
/// Maps an idempotency key to a partition id (AD-4): FNV-1a over the key's
/// chars, then <c>Math.Abs(hash) % partitionCount</c>. The algorithm is
/// behavior-identical to the legacy kernel — existing rows depend on identical
/// partition assignment, so it must never change (pinned by known-vector tests).
/// </summary>
public static class PartitionHelper
{
    /// <summary>
    /// Computes a deterministic partition id for <paramref name="key"/>,
    /// in the range [0, <paramref name="partitionCount"/>).
    /// </summary>
    /// <param name="key">The idempotency key to hash. Casing is significant. Never throws for any non-null key.</param>
    /// <param name="partitionCount">Total number of partitions; must be positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="partitionCount"/> is not positive.</exception>
    public static int GetPartitionId(string key, int partitionCount)
    {
        // Legacy threw ArgumentException for null; ArgumentNullException is an
        // intentional, spec-sanctioned refinement (no compatible caller relies
        // on the legacy type — legacy rejected such keys before storing rows).
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);

        return MapHashToPartition(ComputeFnv1aHash(key), partitionCount);
    }

    /// <summary>
    /// <c>Math.Abs(hash) % partitionCount</c> — exact legacy mapping — with one
    /// repair: <see cref="int.MinValue"/> (where Math.Abs overflows) maps to 0.
    /// Legacy would have crashed on such a key, so no stored row can depend on
    /// a different mapping for it.
    /// </summary>
    internal static int MapHashToPartition(int hash, int partitionCount) =>
        hash == int.MinValue ? 0 : Math.Abs(hash) % partitionCount;

    /// <summary>
    /// 32-bit FNV-1a over the chars of the string (not UTF-8 bytes), in
    /// unchecked int arithmetic — exact legacy algorithm. An empty key hashes
    /// to the offset basis, yielding a deterministic id (spec I/O matrix;
    /// the legacy helper rejected empty keys instead).
    /// </summary>
    private static int ComputeFnv1aHash(string key)
    {
        unchecked
        {
            const int fnvPrime = 16777619;      // 0x01000193
            int hash = (int)2166136261;         // FNV offset basis 0x811C9DC5

            foreach (char c in key)
            {
                hash ^= c;
                hash *= fnvPrime;
            }

            return hash;
        }
    }
}
