using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application.Scenarios;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application.Scenarios;

public class ScenarioLoaderTests
{
    private const string MinimalValidJson = """
        {
          "schemaVersion": 1,
          "name": "Test",
          "scenarioVersion": "v1",
          "requiredProfile": "Regular",
          "cues": [
            {
              "id": "cue-1",
              "slideAnchor": "1",
              "title": "Title",
              "speakerNote": "Note",
              "actions": [ { "kind": "speakerPause", "note": "pause" } ]
            }
          ]
        }
        """;

    [Fact]
    public void LoadFromJson_MinimalValidScenario_Succeeds()
    {
        var result = ScenarioLoader.LoadFromJson(MinimalValidJson, "test");

        result.IsValid.Should().BeTrue();
        result.Scenario.Should().NotBeNull();
        result.Scenario!.Cues.Should().ContainSingle();
    }

    [Fact]
    public void LoadFromJson_UnknownTopLevelField_IsRejected()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "name": "Test",
              "scenarioVersion": "v1",
              "requiredProfile": "Regular",
              "cues": [],
              "someUnknownField": true
            }
            """;

        var result = ScenarioLoader.LoadFromJson(json, "test");

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("failed to parse"));
    }

    [Fact]
    public void LoadFromJson_UnknownActionKind_IsRejected()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "name": "Test",
              "scenarioVersion": "v1",
              "requiredProfile": "Regular",
              "cues": [
                {
                  "id": "cue-1",
                  "slideAnchor": "1",
                  "title": "Title",
                  "speakerNote": "Note",
                  "actions": [ { "kind": "runShellCommand", "note": "not allowed" } ]
                }
              ]
            }
            """;

        var result = ScenarioLoader.LoadFromJson(json, "test");

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void LoadFromJson_InvalidJson_IsRejected()
    {
        var result = ScenarioLoader.LoadFromJson("{ not json", "test");

        result.IsValid.Should().BeFalse();
        result.Scenario.Should().BeNull();
    }

    [Fact]
    public void LoadFromFile_MissingFile_IsRejected()
    {
        var result = ScenarioLoader.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "does-not-exist.json"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("not found"));
    }

    [Fact]
    public void LoadFromFile_FileLockedByAnotherProcess_ReportsReadError()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"locked-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, MinimalValidJson);
        using var exclusiveLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = ScenarioLoader.LoadFromFile(path);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("Could not read scenario file"));
    }

    [Fact]
    public void LoadFromJson_NullLiteral_IsRejectedAsDeserializedToNull()
    {
        var result = ScenarioLoader.LoadFromJson("null", "test");

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("deserialized to null"));
    }

    [Fact]
    public void LoadFromFile_CheckedInMissionCriticalTalkV7_ValidatesSuccessfully()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Scenarios", "mission-critical-talk-v7.json");

        var result = ScenarioLoader.LoadFromFile(path);

        result.Errors.Should().BeEmpty();
        result.IsValid.Should().BeTrue();
        result.Scenario!.Name.Should().Be("MissionCriticalTalk-v7");
        result.Scenario.Cues.Select(c => c.SlideAnchor).Should().Contain(["42", "45-52", "53"]);
    }
}
