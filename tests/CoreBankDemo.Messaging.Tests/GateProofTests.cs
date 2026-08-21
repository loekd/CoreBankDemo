using System.Xml.Linq;
using AwesomeAssertions;
using Xunit;

namespace CoreBankDemo.Messaging.Tests;

/// <summary>
/// Permanent self-check for the coverage gate (story 1.3, FR-28).
/// The coverlet <c>Include</c> filter in this project's csproj must name the real
/// target assembly. A typo'd filter would match nothing and the 90% threshold
/// would pass vacuously forever — this test fails on any filter/assembly drift.
/// </summary>
public class GateProofTests
{
    private const string CsprojFileName = "CoreBankDemo.Messaging.Tests.csproj";

    [Fact]
    public void Coverage_include_filter_matches_target_assembly_name()
    {
        var csprojPath = FindCsprojUpwardsFrom(AppContext.BaseDirectory);
        var includeFilter = XDocument.Load(csprojPath)
            .Descendants("Include")
            .Select(e => e.Value.Trim())
            .SingleOrDefault();

        includeFilter.Should().NotBeNull(
            $"the coverage gate relies on a single <Include> filter in {CsprojFileName}");

        var targetAssemblyName = typeof(MessageConstants).Assembly.GetName().Name;
        includeFilter.Should().Be($"[{targetAssemblyName}]*",
            "a filter that does not name the target assembly makes the 90% gate pass vacuously");
    }

    private static string FindCsprojUpwardsFrom(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, CsprojFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not locate {CsprojFileName} walking up from {startDirectory}.");
    }
}
