using System.Text.RegularExpressions;

namespace CoreBankDemo.DemoRunner.Application.Ports;

/// <summary>
/// Bounds and redacts journal/evidence text before it is persisted. Kept in Application
/// so it is unit-testable independent of the file-based journal implementation
/// (ADR-015: journals hold facts and bounded evidence, never secrets or unbounded raw output).
/// </summary>
public static partial class JournalRedaction
{
    public const int MaxLength = 500;

    public static string Apply(string text)
    {
        var bounded = text.Length > MaxLength ? text[..MaxLength] + "…" : text;
        return SecretLikePattern().Replace(bounded, "[redacted]");
    }

    [GeneratedRegex(@"(?i)(authorization|bearer|idempotency-key)\s*[:=]\s*[^;]+")]
    private static partial Regex SecretLikePattern();
}
