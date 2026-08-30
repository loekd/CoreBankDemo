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
/// Scope covers every <c>CoreBankDemo.*</c> project directory, executable
/// test/demo scripts, and live repository configuration. <c>docs/</c> (ADRs, specs, epics,
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
    /// This test's exact path -- excluded from the scan because it must
    /// name every forbidden signal literally in order to guard against it.
    /// </summary>
    private const string GuardRelativePath =
        "tests/CoreBankDemo.PaymentsAPI.Tests/NoDaprServiceInvocationArchitectureTests.cs";

    private static readonly (string Description, Regex Pattern)[] ForbiddenSignals =
    [
        ("Dapr service-invocation feature flag or configuration key",
            new Regex(@"\bUseDapr\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("Dapr CoreBank client (alternate production client name)",
            new Regex(@"DaprCoreBankApiClient", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("Dapr invocation SDK API",
            new Regex(@"InvokeMethodWithResponseAsync|InvokeMethodAsync|CreateInvokeMethodRequest|" +
                       @"CreateInvokeHttpClient|InvokeMethodGrpcAsync|CreateInvocationInvoker|" +
                       @"DaprInvokeHandler|InvocationHandler|InvocationInterceptor",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("Dapr service-invocation HTTP route",
            new Regex(@"/v1\.0/invoke/", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("Dapr service-invocation header",
            new Regex(@"dapr-app-id", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    ];

    /// <summary>Extensions that can hold executable source, live configuration, or scripts.</summary>
    private static readonly string[] ScannedExtensions =
    [
        ".cs", ".json", ".yaml", ".yml", ".http", ".sh", ".ps1", ".js",
        ".csproj", ".props", ".targets", ".config", ".toml", ".env", ".cmd", ".bat"
    ];

    private static readonly string[] ExcludedDirectorySegments = ["bin", "obj", ".git", "node_modules"];
    private static readonly string[] AdditionalScopedRootNames =
        ["tests", "scripts", "dapr", ".config", ".devcontainer", "k6"];

    [Fact]
    public void No_forbidden_dapr_service_invocation_signal_exists_in_scanned_files()
    {
        var repoRoot = FindRepoRoot();
        var violations = new List<string>();
        var scannedFiles = EnumerateScannedFiles(repoRoot).ToList();

        scannedFiles.Should().NotBeEmpty("the architecture guard must never pass without scanning the repository");
        scannedFiles.Should().Contain(
            file => Path.GetRelativePath(repoRoot, file)
                .StartsWith($"CoreBankDemo.AppHost{Path.DirectorySeparatorChar}", StringComparison.Ordinal),
            "the AppHost is a required part of the service-invocation boundary");
        scannedFiles.Should().Contain(
            file => Path.GetRelativePath(repoRoot, file)
                .StartsWith($"CoreBankDemo.PaymentsAPI{Path.DirectorySeparatorChar}", StringComparison.Ordinal),
            "PaymentsAPI is a required part of the service-invocation boundary");

        foreach (var file in scannedFiles)
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

    [Fact]
    public void AppHost_keeps_the_corebank_reference_outside_the_devproxy_branch()
    {
        var repoRoot = FindRepoRoot();
        var appHostPath = Path.Combine(repoRoot, "CoreBankDemo.AppHost", "AppHost.cs");
        var source = File.ReadAllText(appHostPath);
        const string coreBankReference = "paymentsApi.WithReference(coreBankApi);";
        const string devProxyBranch = "if (devProxy is not null)";

        source.Split(coreBankReference, StringSplitOptions.None)
            .Should().HaveCount(2, "PaymentsAPI must have exactly one CoreBankAPI reference");
        source.IndexOf(coreBankReference, StringComparison.Ordinal)
            .Should().BeLessThan(source.IndexOf(devProxyBranch, StringComparison.Ordinal),
                "the CoreBankAPI reference must apply to both normal and DevProxy orchestration");
    }

    private static IEnumerable<string> EnumerateScannedFiles(string repoRoot)
    {
        var scopedRoots = Directory
            .GetDirectories(repoRoot, "CoreBankDemo.*", SearchOption.TopDirectoryOnly)
            .Concat(AdditionalScopedRootNames.Select(name => Path.Combine(repoRoot, name)))
            .Where(Directory.Exists);

        foreach (var file in Directory.EnumerateFiles(repoRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (ShouldScanFile(repoRoot, file))
            {
                yield return file;
            }
        }

        foreach (var root in scopedRoots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (ShouldScanFile(repoRoot, file))
                {
                    yield return file;
                }
            }
        }
    }

    private static bool ShouldScanFile(string repoRoot, string file)
    {
        var relativePath = Path.GetRelativePath(repoRoot, file);
        var normalizedRelativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        if (string.Equals(normalizedRelativePath, GuardRelativePath, StringComparison.Ordinal))
        {
            return false;
        }

        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment =>
                ExcludedDirectorySegments.Contains(segment, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        return ScannedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
               || string.Equals(Path.GetFileName(file), "Dockerfile", StringComparison.OrdinalIgnoreCase);
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
