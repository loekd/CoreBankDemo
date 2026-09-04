namespace CoreBankDemo.DemoRunner.Application;

public sealed class TopologyObservationDebouncer
{
    private string? _candidateSignature;

    public TopologySnapshot Observe(TopologySnapshot current, TopologySnapshot observed)
    {
        if (!observed.IsReachable || !observed.IsFingerprintMatch)
        {
            _candidateSignature = null;
            return observed;
        }

        var currentSignature = Signature(current);
        var observedSignature = Signature(observed);
        if (string.Equals(currentSignature, observedSignature, StringComparison.Ordinal))
        {
            _candidateSignature = null;
            return observed;
        }

        if (string.Equals(_candidateSignature, observedSignature, StringComparison.Ordinal))
        {
            _candidateSignature = null;
            return observed;
        }

        _candidateSignature = observedSignature;
        return current with
        {
            CapturedAt = observed.CapturedAt,
            ErrorSummary = "Aspire reported a state change; waiting for one confirming snapshot.",
        };
    }

    public void Reset() => _candidateSignature = null;

    private static string Signature(TopologySnapshot snapshot) =>
        string.Join(
            "|",
            snapshot.Resources
                .OrderBy(resource => resource.Name, StringComparer.Ordinal)
                .Select(resource => $"{resource.Name}:{resource.Condition}:{resource.Health}:{resource.ReplicaCount}"));
}
