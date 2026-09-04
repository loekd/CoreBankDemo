using System.Collections.Concurrent;

namespace CoreBankDemo.LoadTestSupport.Services;

public sealed class LoadRunEvidenceState
{
    private readonly ConcurrentDictionary<string, byte> _inlineSettlements = new(StringComparer.Ordinal);

    public int InlineSettlementCount => _inlineSettlements.Count;

    public bool RecordInlineSettlement(string idempotencyKey) =>
        _inlineSettlements.TryAdd(idempotencyKey, 0);

    public void Reset() => _inlineSettlements.Clear();
}
