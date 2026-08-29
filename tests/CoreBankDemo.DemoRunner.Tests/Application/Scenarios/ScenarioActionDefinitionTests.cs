using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application.Scenarios;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application.Scenarios;

public class ScenarioActionDefinitionTests
{
    [Fact]
    public void IsMutating_SendHttpWithPost_IsTrue()
    {
        new ScenarioActionDefinition { Kind = ActionKind.SendHttp, Method = "POST" }.IsMutating.Should().BeTrue();
    }

    [Fact]
    public void IsMutating_SendHttpWithGet_IsFalse()
    {
        new ScenarioActionDefinition { Kind = ActionKind.SendHttp, Method = "GET" }.IsMutating.Should().BeFalse();
    }

    [Fact]
    public void IsMutating_RunAcceptedLoadWorkflow_IsTrue()
    {
        new ScenarioActionDefinition { Kind = ActionKind.RunAcceptedLoadWorkflow }.IsMutating.Should().BeTrue();
    }

    [Theory]
    [InlineData(ActionKind.SelectTopology)]
    [InlineData(ActionKind.WaitForHealth)]
    [InlineData(ActionKind.AssertHttp)]
    [InlineData(ActionKind.OpenKnownUrl)]
    [InlineData(ActionKind.SpeakerPause)]
    public void IsMutating_OtherKinds_IsFalse(ActionKind kind)
    {
        new ScenarioActionDefinition { Kind = kind }.IsMutating.Should().BeFalse();
    }
}
