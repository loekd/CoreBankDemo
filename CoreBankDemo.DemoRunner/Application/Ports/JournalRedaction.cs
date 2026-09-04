using System.Text.RegularExpressions;

namespace CoreBankDemo.DemoRunner.Application.Ports;

/// <summary>
/// Bounds and redacts session evidence before display or explicit export.
/// </summary>
public static partial class JournalRedaction
{
    public const int MaxLength = 8192;

    public static string Apply(string text)
    {
        var bounded = text.Length > MaxLength ? text[..MaxLength] + "…" : text;
        return SecretLikePattern().Replace(bounded, "[redacted]");
    }

    [GeneratedRegex(@"(?i)(authorization|bearer|idempotency-key|password|secret|token)\s*[:=]\s*[^;\r\n,}]+")]
    private static partial Regex SecretLikePattern();
}
