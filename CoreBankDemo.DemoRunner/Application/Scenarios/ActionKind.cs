namespace CoreBankDemo.DemoRunner.Application.Scenarios;

/// <summary>
/// The closed set of scenario action kinds (ADR-015). A scenario file may only select
/// from these compiled kinds; it can never express an arbitrary process path, shell
/// text, database statement, or unrestricted URL.
/// </summary>
public enum ActionKind
{
    /// <summary>Start or attach to a known Aspire AppHost profile.</summary>
    SelectTopology,

    /// <summary>Poll a known resource's health endpoint until healthy or timeout.</summary>
    WaitForHealth,

    /// <summary>Send an HTTP request to a known, allow-listed local endpoint.</summary>
    SendHttp,

    /// <summary>Run the Story 7.3 accepted Run→Wait→Assert load workflow.</summary>
    RunAcceptedLoadWorkflow,

    /// <summary>Assert against a known endpoint's response, or compare two prior captures.</summary>
    AssertHttp,

    /// <summary>Open a known, allow-listed local URL in the default browser.</summary>
    OpenKnownUrl,

    /// <summary>A no-op narrative beat: the speaker paces the talk; nothing executes.</summary>
    SpeakerPause,
}
