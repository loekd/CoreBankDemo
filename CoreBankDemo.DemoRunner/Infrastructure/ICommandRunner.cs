namespace CoreBankDemo.DemoRunner.Infrastructure;

public sealed record CommandOutput(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool ProcessStarted,
    bool TimedOut,
    bool StartFailed)
{
    public bool Succeeded => ProcessStarted && !TimedOut && !StartFailed && ExitCode == 0;

    public static CommandOutput Success(string standardOutput = "", string standardError = "") =>
        new(0, standardOutput, standardError, true, false, false);

    public static CommandOutput Failure(int exitCode, string standardError, string standardOutput = "") =>
        new(exitCode, standardOutput, standardError, true, false, false);

    public static CommandOutput Timeout(string standardOutput = "", string standardError = "") =>
        new(-1, standardOutput, standardError, true, true, false);

    public static CommandOutput Missing(string error) =>
        new(-1, "", error, false, false, true);
}

public interface ICommandRunner
{
    Task<CommandOutput> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct);
}
