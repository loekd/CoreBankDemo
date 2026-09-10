using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Application.Ports;

/// <summary>
/// Bounds session evidence before display or explicit export.
/// <para>
/// This class used to redact as well, replacing anything that looked like a secret with
/// <c>[redacted]</c>. That substitution is gone: the console's whole point is to show the bytes
/// it sent and received, and the one line the audience is asked to read — the
/// <c>Idempotency-Key</c> — was the line it blanked. What is left is a different concern that
/// matters more now that requests, responses and events are all retained: no single payload,
/// and no single header list, may be large enough to take the pane apart.
/// </para>
/// </summary>
public static class JournalText
{
    public const int MaxLength = 8192;

    /// <summary>
    /// How many headers one side of an exchange, or one CloudEvent's extensions, may record.
    /// A remote that answers with hundreds of them is not allowed to make the pane unreadable.
    /// </summary>
    public const int MaxHeaders = 64;

    /// <summary>
    /// How long one header value may be. Far shorter than <see cref="MaxLength"/>: a header is
    /// a line the eye reads across, and a body is a block it reads down.
    /// </summary>
    public const int MaxHeaderValueLength = 512;

    public static string Bound(string text) =>
        text.Length > MaxLength ? text[..MaxLength] + "…" : text;

    /// <summary>
    /// Caps a header list in both directions — how many, and how long each value is — and says
    /// so when it drops any, exactly as a truncated body ends in an ellipsis. A silently short
    /// list would be this console omitting evidence without stating that it did.
    /// </summary>
    public static IReadOnlyList<EvidenceHeader> Bound(IEnumerable<EvidenceHeader> headers)
    {
        var bounded = new List<EvidenceHeader>();
        var dropped = 0;
        foreach (var header in headers)
        {
            if (bounded.Count == MaxHeaders)
            {
                dropped++;
                continue;
            }

            bounded.Add(header.Value.Length > MaxHeaderValueLength
                ? header with { Value = header.Value[..MaxHeaderValueLength] + "…" }
                : header);
        }

        if (dropped > 0)
        {
            bounded.Add(new EvidenceHeader("…", $"{dropped} further header(s) were not recorded"));
        }

        return bounded;
    }
}
