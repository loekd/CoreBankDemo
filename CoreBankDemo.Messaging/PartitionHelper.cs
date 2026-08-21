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
    /// <param name="key">The idempotency key to hash. Casing is significant.</param>
    /// <param name="partitionCount">Total number of partitions; must be positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="partitionCount"/> is not positive.</exception>
    public static int GetPartitionId(string key, int partitionCount)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);

        return Math.Abs(ComputeFnv1aHash(key)) % partitionCount;
    }

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
