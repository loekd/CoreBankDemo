namespace CoreBankDemo.DemoRunner.Application;

public sealed class TopologyObservationDebouncer
{
    /// <summary>
    /// The hold this debouncer stamps onto <see cref="TopologySnapshot.ErrorSummary"/> while it
    /// waits for a second, confirming observation. Named because the console has to tell it
    /// apart from the parser's own shape-mismatch text: a shape that changed is no longer a
    /// reason to refuse a resource command, but "I have not confirmed what I am looking at yet"
    /// still is.
    /// </summary>
    public const string AwaitingConfirmationSummary =
        "Aspire reported a state change; waiting for one confirming snapshot.";

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
            ErrorSummary = AwaitingConfirmationSummary,
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
