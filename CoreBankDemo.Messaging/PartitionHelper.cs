namespace CoreBankDemo.Messaging;

public static class PartitionHelper
{
    /// <summary>
    /// Computes a consistent partition ID for a given key using FNV-1a hash algorithm.
    /// </summary>
    /// <param name="key">The key to hash (e.g., MessageId or IdempotencyKey)</param>
    /// <param name="partitionCount">Total number of partitions</param>
    /// <returns>Partition ID between 0 and partitionCount-1</returns>
    public static int GetPartitionId(string key, int partitionCount)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key cannot be null or empty", nameof(key));

        if (partitionCount <= 0)
            throw new ArgumentException("Partition count must be greater than 0", nameof(partitionCount));

        var hash = ComputeFnv1aHash(key);
        return Math.Abs(hash) % partitionCount;
    }

    /// <summary>
    /// FNV-1a (Fowler-Noll-Vo) hash algorithm for consistent, uniform distribution.
    /// </summary>
    private static int ComputeFnv1aHash(string key)
    {
        unchecked
        {
            const int fnvPrime = 16777619;
            int hash = (int)2166136261;

            foreach (char c in key)
            {
                hash ^= c;
                hash *= fnvPrime;
            }

            return hash;
        }
    }
}
