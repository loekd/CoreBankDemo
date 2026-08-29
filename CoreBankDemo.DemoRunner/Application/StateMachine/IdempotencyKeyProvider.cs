using System.Security.Cryptography;
using System.Text;

namespace CoreBankDemo.DemoRunner.Application.StateMachine;

/// <summary>
/// Derives a deterministic, run-scoped idempotency key from (runId, cueId, keyRef).
/// Retrying an action reuses the identical key by construction (same inputs); two
/// actions sharing a <c>keyRef</c> within the same cue deliberately resolve to the same
/// key (e.g. the slide-42 Inbox proof submits it twice on purpose).
/// </summary>
public static class IdempotencyKeyProvider
{
    public static string ForCueAction(string runId, string cueId, string keyRef)
    {
        var raw = $"{runId}:{cueId}:{keyRef}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return new Guid(hash[..16]).ToString();
    }
}
