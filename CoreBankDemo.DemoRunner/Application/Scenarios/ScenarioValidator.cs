namespace CoreBankDemo.DemoRunner.Application.Scenarios;

/// <summary>Outcome of validating a scenario file or a single action within it. Never throws for expected shape problems.</summary>
public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ValidationResult Success() => new(true, []);
}

/// <summary>
/// Validates a deserialized <see cref="TalkScenarioDefinition"/> before any process is
/// started. This is the single gate implementing "validate the entire talk scenario
/// before starting a process" and "reject unknown actions and fields" (ADR-015).
/// </summary>
public static class ScenarioValidator
{
    public const int SupportedSchemaVersion = 1;

    public static ValidationResult Validate(TalkScenarioDefinition scenario)
    {
        var errors = new List<string>();

        if (scenario.SchemaVersion != SupportedSchemaVersion)
        {
            errors.Add($"Unsupported schema version {scenario.SchemaVersion}; expected {SupportedSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(scenario.Name))
        {
            errors.Add("Scenario Name is required.");
        }

        if (string.IsNullOrWhiteSpace(scenario.ScenarioVersion))
        {
            errors.Add("Scenario ScenarioVersion is required.");
        }

        if (!KnownTopologyProfiles.All.Contains(scenario.RequiredProfile))
        {
            errors.Add($"Scenario RequiredProfile '{scenario.RequiredProfile}' is not a known topology profile.");
        }

        if (scenario.Cues.Count == 0)
        {
            errors.Add("Scenario must declare at least one cue.");
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cue in scenario.Cues)
        {
            if (string.IsNullOrWhiteSpace(cue.Id))
            {
                errors.Add("A cue is missing its Id.");
            }
            else if (!seenIds.Add(cue.Id))
            {
                errors.Add($"Duplicate cue id '{cue.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(cue.SlideAnchor))
            {
                errors.Add($"Cue '{cue.Id}' is missing a SlideAnchor.");
            }

            if (string.IsNullOrWhiteSpace(cue.Title))
            {
                errors.Add($"Cue '{cue.Id}' is missing a Title.");
            }

            if (cue.Actions.Count == 0)
            {
                errors.Add($"Cue '{cue.Id}' must declare at least one action.");
            }

            foreach (var action in cue.PreArmActions)
            {
                errors.AddRange(ValidateAction(cue.Id, "PreArmActions", action));
                if (action.IsMutating)
                {
                    errors.Add($"Cue '{cue.Id}': pre-arm action '{action.Kind}' is mutating and is never allowed to pre-arm.");
                }
            }

            foreach (var action in cue.Actions)
            {
                errors.AddRange(ValidateAction(cue.Id, "Actions", action));
            }

            foreach (var action in cue.InvestigateActions)
            {
                errors.AddRange(ValidateAction(cue.Id, "InvestigateActions", action));
            }
        }

        return errors.Count == 0 ? ValidationResult.Success() : new ValidationResult(false, errors);
    }

    private static IEnumerable<string> ValidateAction(string cueId, string list, ScenarioActionDefinition action)
    {
        string Prefix(string field) => $"Cue '{cueId}' {list} [{action.Kind}] {field}";

        switch (action.Kind)
        {
            case ActionKind.SelectTopology:
                if (string.IsNullOrWhiteSpace(action.ProfileName) || !KnownTopologyProfiles.All.Contains(action.ProfileName))
                {
                    yield return $"{Prefix("ProfileName")} must be one of the known topology profiles.";
                }
                break;

            case ActionKind.WaitForHealth:
                if (string.IsNullOrWhiteSpace(action.ResourceName) || !KnownResources.All.Contains(action.ResourceName))
                {
                    yield return $"{Prefix("ResourceName")} must be one of the known resources.";
                }
                if (action.TimeoutSeconds is null or <= 0)
                {
                    yield return $"{Prefix("TimeoutSeconds")} must be a positive number of seconds.";
                }
                break;

            case ActionKind.SendHttp:
                if (string.IsNullOrWhiteSpace(action.EndpointId) || !KnownEndpoints.All.Contains(action.EndpointId))
                {
                    yield return $"{Prefix("EndpointId")} must be one of the known endpoints.";
                }
                if (string.IsNullOrWhiteSpace(action.Method))
                {
                    yield return $"{Prefix("Method")} is required.";
                }
                break;

            case ActionKind.AssertHttp:
                var hasEndpointMode = !string.IsNullOrWhiteSpace(action.EndpointId);
                var hasCaptureMode = !string.IsNullOrWhiteSpace(action.CaptureRefA) && !string.IsNullOrWhiteSpace(action.CaptureRefB);
                if (hasEndpointMode == hasCaptureMode)
                {
                    yield return $"{Prefix("EndpointId/CaptureRefA+B")} assertHttp must use exactly one of endpoint mode (EndpointId+CaptureJsonPath) or capture-comparison mode (CaptureRefA+CaptureRefB).";
                }
                if (hasEndpointMode && !KnownEndpoints.All.Contains(action.EndpointId!))
                {
                    yield return $"{Prefix("EndpointId")} must be one of the known endpoints.";
                }
                break;

            case ActionKind.RunAcceptedLoadWorkflow:
                if (string.IsNullOrWhiteSpace(action.ProfileName) || action.ProfileName != KnownTopologyProfiles.LoadTest)
                {
                    yield return $"{Prefix("ProfileName")} must be '{KnownTopologyProfiles.LoadTest}'.";
                }
                break;

            case ActionKind.OpenKnownUrl:
                if (string.IsNullOrWhiteSpace(action.LinkId) || !KnownLinks.All.Contains(action.LinkId))
                {
                    yield return $"{Prefix("LinkId")} must be one of the known links.";
                }
                break;

            case ActionKind.SpeakerPause:
                if (string.IsNullOrWhiteSpace(action.Note))
                {
                    yield return $"{Prefix("Note")} is required.";
                }
                break;

            default:
                yield return $"Cue '{cueId}' {list} declares an unrecognized action kind '{action.Kind}'.";
                break;
        }
    }
}
