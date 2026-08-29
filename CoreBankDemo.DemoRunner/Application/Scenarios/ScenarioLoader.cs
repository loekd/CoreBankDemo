using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreBankDemo.DemoRunner.Application.Scenarios;

/// <summary>
/// Loads a scenario file from disk, rejecting unknown JSON fields/action kinds at
/// deserialization time and then running <see cref="ScenarioValidator"/> before
/// returning. Never throws for malformed scenario content — callers get a
/// <see cref="ValidationResult"/> and must check <see cref="ScenarioLoadResult.IsValid"/>
/// before starting any process (ADR-015).
/// </summary>
public static class ScenarioLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static ScenarioLoadResult LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            return ScenarioLoadResult.Failure($"Scenario file not found: {path}");
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            return ScenarioLoadResult.Failure($"Could not read scenario file '{path}': {ex.Message}");
        }

        return LoadFromJson(json, path);
    }

    public static ScenarioLoadResult LoadFromJson(string json, string sourceDescription)
    {
        TalkScenarioDefinition? scenario;
        try
        {
            scenario = JsonSerializer.Deserialize<TalkScenarioDefinition>(json, Options);
        }
        catch (JsonException ex)
        {
            return ScenarioLoadResult.Failure($"Scenario '{sourceDescription}' failed to parse: {ex.Message}");
        }

        if (scenario is null)
        {
            return ScenarioLoadResult.Failure($"Scenario '{sourceDescription}' deserialized to null.");
        }

        var validation = ScenarioValidator.Validate(scenario);
        return validation.IsValid
            ? ScenarioLoadResult.Success(scenario)
            : new ScenarioLoadResult(false, scenario, validation.Errors);
    }
}

public sealed record ScenarioLoadResult(bool IsValid, TalkScenarioDefinition? Scenario, IReadOnlyList<string> Errors)
{
    public static ScenarioLoadResult Success(TalkScenarioDefinition scenario) => new(true, scenario, []);
    public static ScenarioLoadResult Failure(params string[] errors) => new(false, null, errors);
}
