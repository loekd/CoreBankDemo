namespace CoreBankDemo.DemoRunner.Application.Scenarios;

/// <summary>
/// One scenario-data action. Every field is inert data; the compiled action executor
/// (Infrastructure) is the only place that turns <see cref="Kind"/> plus these fields
/// into an actual HTTP call, health probe, process action, or browser launch. Fields
/// unused by a given <see cref="Kind"/> must be left null — <see cref="ScenarioValidator"/>
/// rejects a mismatch.
/// </summary>
public sealed record ScenarioActionDefinition
{
    public required ActionKind Kind { get; init; }

    // selectTopology / runAcceptedLoadWorkflow
    public string? ProfileName { get; init; }

    // waitForHealth
    public string? ResourceName { get; init; }
    public int? TimeoutSeconds { get; init; }

    // sendHttp
    public string? EndpointId { get; init; }
    public string? Method { get; init; }
    public string? BodyJson { get; init; }

    /// <summary>
    /// Groups actions that must share one deterministic idempotency key. Two sendHttp
    /// actions in the same cue with the same <see cref="IdempotencyKeyRef"/> deliberately
    /// send the identical key twice (e.g. the slide-42 Inbox proof); Retry of a single
    /// action reuses the same ref automatically.
    /// </summary>
    public string? IdempotencyKeyRef { get; init; }

    /// <summary>Stores a value extracted from this action's response under this name for later comparison.</summary>
    public string? CaptureAs { get; init; }

    /// <summary>JSONPath-like dotted/bracket path (e.g. "$.transactionId") used with <see cref="CaptureAs"/> or assertHttp.</summary>
    public string? CaptureJsonPath { get; init; }

    /// <summary>
    /// References a value captured by an earlier action (<see cref="CaptureAs"/>) to use
    /// as the single path parameter of a parameterized known endpoint (e.g.
    /// <c>corebank.transactions.status</c>'s <c>{idempotencyKey}</c> segment). The scenario
    /// never supplies a URL directly — only this compiled-endpoint parameter (ADR-015).
    /// </summary>
    public string? PathParamRef { get; init; }

    // assertHttp — mode A: call EndpointId and compare an extracted field
    public string? ExpectedValue { get; init; }

    // assertHttp — mode B: compare two previously captured values without a new HTTP call
    public string? CaptureRefA { get; init; }
    public string? CaptureRefB { get; init; }
    public bool? ExpectEqual { get; init; }

    // runAcceptedLoadWorkflow
    public int? ExpectedUniqueCount { get; init; }

    // openKnownUrl
    public string? LinkId { get; init; }

    // speakerPause
    public string? Note { get; init; }

    /// <summary>
    /// True for actions that would mutate banking/demo state. Pre-arm may never
    /// execute a mutating action (I/O matrix: "Show mode ... no payment/command has
    /// been sent").
    /// </summary>
    public bool IsMutating => Kind switch
    {
        ActionKind.SendHttp => !string.Equals(Method, "GET", StringComparison.OrdinalIgnoreCase),
        ActionKind.RunAcceptedLoadWorkflow => true,
        _ => false,
    };
}
