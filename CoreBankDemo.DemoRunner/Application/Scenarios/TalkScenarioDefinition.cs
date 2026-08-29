namespace CoreBankDemo.DemoRunner.Application.Scenarios;

/// <summary>One talk cue: a slide-anchored unit of narrative with pre-arm and fire actions.</summary>
public sealed record TalkCueDefinition
{
    public required string Id { get; init; }
    public required string SlideAnchor { get; init; }
    public required string Title { get; init; }
    public required string SpeakerNote { get; init; }

    /// <summary>Executed in Show mode before the cue fires. Must never contain a mutating action.</summary>
    public IReadOnlyList<ScenarioActionDefinition> PreArmActions { get; init; } = [];

    /// <summary>Executed when the speaker runs the cue.</summary>
    public required IReadOnlyList<ScenarioActionDefinition> Actions { get; init; }

    /// <summary>Optional actions offered only when the cue's Assert phase fails (the "Investigate" step).</summary>
    public IReadOnlyList<ScenarioActionDefinition> InvestigateActions { get; init; } = [];
}

/// <summary>The versioned, checked-in talk scenario (e.g. MissionCriticalTalk-v7).</summary>
public sealed record TalkScenarioDefinition
{
    /// <summary>Schema version this file was authored against; the loader rejects any other value.</summary>
    public required int SchemaVersion { get; init; }

    public required string Name { get; init; }
    public required string ScenarioVersion { get; init; }

    /// <summary>The known Aspire profile this scenario requires ("Regular" or "LoadTest").</summary>
    public required string RequiredProfile { get; init; }

    public required IReadOnlyList<TalkCueDefinition> Cues { get; init; }
}
