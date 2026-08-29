using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application.Scenarios;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application.Scenarios;

public class ScenarioValidatorTests
{
    private static TalkCueDefinition ValidCue(string id = "cue-1") => new()
    {
        Id = id,
        SlideAnchor = "1",
        Title = "Title",
        SpeakerNote = "Note",
        Actions = [new ScenarioActionDefinition { Kind = ActionKind.SpeakerPause, Note = "pause" }],
    };

    private static TalkScenarioDefinition ValidScenario(params TalkCueDefinition[] cues) => new()
    {
        SchemaVersion = ScenarioValidator.SupportedSchemaVersion,
        Name = "Scenario",
        ScenarioVersion = "v1",
        RequiredProfile = KnownTopologyProfiles.Regular,
        Cues = cues.Length == 0 ? [ValidCue()] : cues,
    };

    [Fact]
    public void Validate_MinimalValidScenario_Succeeds()
    {
        var result = ScenarioValidator.Validate(ValidScenario());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WrongSchemaVersion_Fails()
    {
        var scenario = ValidScenario() with { SchemaVersion = 999 };

        var result = ScenarioValidator.Validate(scenario);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("schema version"));
    }

    [Fact]
    public void Validate_UnknownTopologyProfile_Fails()
    {
        var scenario = ValidScenario() with { RequiredProfile = "NotARealProfile" };

        var result = ScenarioValidator.Validate(scenario);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("RequiredProfile"));
    }

    [Fact]
    public void Validate_NoCues_Fails()
    {
        var scenario = ValidScenario() with { Cues = [] };

        var result = ScenarioValidator.Validate(scenario);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("at least one cue"));
    }

    [Fact]
    public void Validate_DuplicateCueIds_Fails()
    {
        var scenario = ValidScenario(ValidCue("dup"), ValidCue("dup"));

        var result = ScenarioValidator.Validate(scenario);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Duplicate cue id"));
    }

    [Fact]
    public void Validate_CueWithNoActions_Fails()
    {
        var cue = ValidCue() with { Actions = [] };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("at least one action"));
    }

    [Fact]
    public void Validate_MutatingPreArmAction_Fails()
    {
        var cue = ValidCue() with
        {
            PreArmActions =
            [
                new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.PaymentsSubmit, Method = "POST" },
            ],
        };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("mutating") && e.Contains("pre-arm"));
    }

    [Fact]
    public void Validate_NonMutatingPreArmAction_Succeeds()
    {
        var cue = ValidCue() with
        {
            PreArmActions =
            [
                new ScenarioActionDefinition { Kind = ActionKind.WaitForHealth, ResourceName = KnownResources.PaymentsApi, TimeoutSeconds = 5 },
            ],
        };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("not-a-known-endpoint")]
    [InlineData("")]
    public void Validate_SendHttpWithUnknownEndpoint_Fails(string endpointId)
    {
        var cue = ValidCue() with
        {
            Actions = [new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = endpointId, Method = "POST" }],
        };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("EndpointId"));
    }

    [Fact]
    public void Validate_AssertHttpWithBothEndpointAndCaptureModes_Fails()
    {
        var cue = ValidCue() with
        {
            Actions =
            [
                new ScenarioActionDefinition
                {
                    Kind = ActionKind.AssertHttp,
                    EndpointId = KnownEndpoints.PaymentsInbox,
                    CaptureRefA = "A",
                    CaptureRefB = "B",
                },
            ],
        };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("exactly one of"));
    }

    [Fact]
    public void Validate_AssertHttpCaptureComparisonMode_Succeeds()
    {
        var cue = ValidCue() with
        {
            Actions = [new ScenarioActionDefinition { Kind = ActionKind.AssertHttp, CaptureRefA = "A", CaptureRefB = "B", ExpectEqual = true }],
        };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_RunAcceptedLoadWorkflowWithNonLoadTestProfile_Fails()
    {
        var cue = ValidCue() with
        {
            Actions = [new ScenarioActionDefinition { Kind = ActionKind.RunAcceptedLoadWorkflow, ProfileName = KnownTopologyProfiles.Regular }],
        };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains(KnownTopologyProfiles.LoadTest));
    }

    [Fact]
    public void Validate_OpenKnownUrlWithUnknownLink_Fails()
    {
        var cue = ValidCue() with
        {
            Actions = [new ScenarioActionDefinition { Kind = ActionKind.OpenKnownUrl, LinkId = "https://evil.example.com" }],
        };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("LinkId"));
    }

    [Fact]
    public void Validate_SpeakerPauseWithoutNote_Fails()
    {
        var cue = ValidCue() with
        {
            Actions = [new ScenarioActionDefinition { Kind = ActionKind.SpeakerPause }],
        };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Note"));
    }

    [Fact]
    public void Validate_EmptyScenarioName_Fails()
    {
        var scenario = ValidScenario() with { Name = " " };

        var result = ScenarioValidator.Validate(scenario);

        result.Errors.Should().Contain(e => e.Contains("Name is required"));
    }

    [Fact]
    public void Validate_EmptyScenarioVersion_Fails()
    {
        var scenario = ValidScenario() with { ScenarioVersion = " " };

        var result = ScenarioValidator.Validate(scenario);

        result.Errors.Should().Contain(e => e.Contains("ScenarioVersion is required"));
    }

    [Fact]
    public void Validate_CueMissingId_Fails()
    {
        var cue = ValidCue() with { Id = " " };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.Errors.Should().Contain(e => e.Contains("missing its Id"));
    }

    [Fact]
    public void Validate_CueMissingSlideAnchor_Fails()
    {
        var cue = ValidCue() with { SlideAnchor = " " };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.Errors.Should().Contain(e => e.Contains("missing a SlideAnchor"));
    }

    [Fact]
    public void Validate_CueMissingTitle_Fails()
    {
        var cue = ValidCue() with { Title = " " };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.Errors.Should().Contain(e => e.Contains("missing a Title"));
    }

    [Fact]
    public void Validate_SelectTopologyWithUnknownProfile_Fails()
    {
        var cue = ValidCue() with { Actions = [new ScenarioActionDefinition { Kind = ActionKind.SelectTopology, ProfileName = "Bogus" }] };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.Errors.Should().Contain(e => e.Contains("known topology profiles"));
    }

    [Fact]
    public void Validate_WaitForHealthWithUnknownResource_Fails()
    {
        var cue = ValidCue() with { Actions = [new ScenarioActionDefinition { Kind = ActionKind.WaitForHealth, ResourceName = "not-a-resource", TimeoutSeconds = 5 }] };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.Errors.Should().Contain(e => e.Contains("known resources"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_WaitForHealthWithInvalidTimeout_Fails(int? timeoutSeconds)
    {
        var cue = ValidCue() with { Actions = [new ScenarioActionDefinition { Kind = ActionKind.WaitForHealth, ResourceName = KnownResources.PaymentsApi, TimeoutSeconds = timeoutSeconds }] };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.Errors.Should().Contain(e => e.Contains("positive number of seconds"));
    }

    [Fact]
    public void Validate_SendHttpMissingMethod_Fails()
    {
        var cue = ValidCue() with { Actions = [new ScenarioActionDefinition { Kind = ActionKind.SendHttp, EndpointId = KnownEndpoints.PaymentsSubmit }] };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.Errors.Should().Contain(e => e.Contains("Method") && e.Contains("required"));
    }

    [Fact]
    public void Validate_AssertHttpEndpointModeWithUnknownEndpoint_Fails()
    {
        var cue = ValidCue() with { Actions = [new ScenarioActionDefinition { Kind = ActionKind.AssertHttp, EndpointId = "not-known", CaptureJsonPath = "$.a", ExpectedValue = "x" }] };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.Errors.Should().Contain(e => e.Contains("EndpointId") && e.Contains("known endpoints"));
    }

    [Fact]
    public void Validate_UnrecognizedActionKind_Fails()
    {
        var cue = ValidCue() with { Actions = [new ScenarioActionDefinition { Kind = (ActionKind)999 }] };
        var scenario = ValidScenario(cue);

        var result = ScenarioValidator.Validate(scenario);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("unrecognized action kind"));
    }
}
