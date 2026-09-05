namespace CoreBankDemo.DemoRunner.Application.Ports;

/// <summary>
/// Outcome of an open attempt. <see cref="Url"/> is populated whenever a URL was
/// resolved at all, even when <see cref="Succeeded"/> is false, so a caller can
/// still show or copy the link after a failed OS-level browser launch -- the
/// expected outcome wherever no default browser is reachable (e.g. this sandbox).
/// </summary>
public sealed record LinkOpenResult(bool Succeeded, string? Url);

/// <summary>Opens an allow-listed local dashboard URL in the OS default browser.</summary>
public interface IBrowserLauncher
{
    Task<LinkOpenResult> OpenAsync(string linkId, string? verifiedUrl, CancellationToken ct);
}
