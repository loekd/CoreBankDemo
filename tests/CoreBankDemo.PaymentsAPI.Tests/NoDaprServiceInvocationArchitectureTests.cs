using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// Story 6.7 (ADR-008/ADR-013): completes the Kiota-only decision with an
/// executable guard so a Dapr service-invocation transport cannot silently
/// return. This scans <em>semantic danger signals</em> -- the specific
/// invocation APIs, routes, headers, feature flags, and alternate-client
/// name the spec forbids -- never the bare word "Dapr", so retained pub/sub
/// code (<c>DaprClient.PublishEventAsync</c>, Dapr ASP.NET subscription
/// wiring, sidecars, pubsub components) keeps passing untouched.
/// <para>
/// Scope mirrors the spec's own verification command: every
/// <c>CoreBankDemo.*</c> project directory plus <c>tests/</c> and (if it
/// ever exists) <c>scripts/</c> -- i.e. production source, live
/// configuration, scripts, and AppHosts. <c>docs/</c> (ADRs, specs, epics,
/// planning artifacts) is deliberately out of scope: those are historical
/// or currently-accurate <em>records</em> of the decision, not executable
/// or configuration surface, and are audited separately by the story's
/// documentation-cleanup task rather than by a zero-text-match guard.
/// </para>
/// <para>
/// Generated/build output (<c>bin/</c>, <c>obj/</c> -- where Kiota's
/// intermediate client actually lives) is excluded, matching ADR-013's
/// "generated sources are never committed" boundary: this guard polices
/// checked-in source, not generator output.
/// </para>
/// </summary>
public class NoDaprServiceInvocationArchitectureTests
{
    /// <summary>
    /// This test's own file name -- excluded from the scan because it must
    /// name every forbidden signal literally in order to guard against it.
    /// </summary>
    private const string GuardFileName = "NoDaprServiceInvocationArchitectureTests.cs";

    private static readonly (string Description, Regex Pattern)[] ForbiddenSignals =
    [
        ("Dapr service-invocation feature flag (config-bound form)",
            new Regex(@"Features:UseDapr", RegexOptions.Compiled)),
        ("Dapr service-invocation feature flag (environment-variable form)",
            new Regex(@"Features__UseDapr", RegexOptions.Compiled)),
        ("Dapr CoreBank client (alternate production client name)",
            new Regex(@"DaprCoreBankApiClient", RegexOptions.Compiled)),
        ("Dapr invocation SDK API",
            new Regex(@"InvokeMethodWithResponseAsync|InvokeMethodAsync|CreateInvokeMethodRequest|" +
                       @"CreateInvokeHttpClient|DaprInvokeHandler", RegexOptions.Compiled)),
        ("Dapr service-invocation HTTP route",
            new Regex(@"/v1\.0/invoke/", RegexOptions.Compiled)),
        ("Dapr service-invocation header",
            new Regex(@"dapr-app-id", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    ];

    /// <summary>Extensions that can hold executable source, live configuration, or scripts.</summary>
    private static readonly string[] ScannedExtensions =
        [".cs", ".json", ".yaml", ".yml", ".http", ".sh", ".ps1", ".csproj", ".props"];

    private static readonly string[] ExcludedDirectorySegments = ["bin", "obj", ".git", "node_modules"];

    [Fact]
    public void No_forbidden_dapr_service_invocation_signal_exists_in_scanned_files()
    {
        var repoRoot = FindRepoRoot();
        var violations = new List<string>();

        foreach (var file in EnumerateScannedFiles(repoRoot))
        {
            var text = File.ReadAllText(file);
            var relativePath = Path.GetRelativePath(repoRoot, file);

            foreach (var (description, pattern) in ForbiddenSignals)
            {
                foreach (Match match in pattern.Matches(text))
                {
                    violations.Add($"{relativePath}: '{match.Value}' ({description})");
                }
            }
        }

        violations.Should().BeEmpty(
            "production source, live configuration, scripts, and AppHosts must never reintroduce a Dapr " +
            "service-invocation API, route, header, feature flag, or alternate CoreBank client (ADR-008/" +
            "ADR-013, story 6.7)");
    }

    private static IEnumerable<string> EnumerateScannedFiles(string repoRoot)
    {
        var scopedRoots = Directory
            .GetDirectories(repoRoot, "CoreBankDemo.*", SearchOption.TopDirectoryOnly)
            .Concat([Path.Combine(repoRoot, "tests"), Path.Combine(repoRoot, "scripts")])
            .Where(Directory.Exists);

        foreach (var root in scopedRoots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == GuardFileName)
                {
                    continue;
                }

                if (!ScannedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var segments = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Any(s => ExcludedDirectorySegments.Contains(s, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CoreBankDemo.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"Could not locate repo root (CoreBankDemo.sln) walking up from {AppContext.BaseDirectory}");
        }

        return directory.FullName;
    }
}
