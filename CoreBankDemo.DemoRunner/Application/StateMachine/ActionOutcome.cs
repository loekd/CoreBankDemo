namespace CoreBankDemo.DemoRunner.Application.StateMachine;

/// <summary>Outcome of executing one scenario action.</summary>
public sealed record ActionOutcome(bool Success, bool IsAmbiguous, string Summary, bool IsGating = true)
{
    public static ActionOutcome Passed(string summary, bool isGating = true) => new(true, false, summary, isGating);
    public static ActionOutcome FailedResult(string summary, bool isGating = true) => new(false, false, summary, isGating);
    public static ActionOutcome Ambiguous(string summary) => new(false, true, summary);
}
