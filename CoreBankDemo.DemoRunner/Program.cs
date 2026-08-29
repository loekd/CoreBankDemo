using System.Diagnostics;
using CoreBankDemo.DemoRunner.Application.Doctor;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Application.Scenarios;
using CoreBankDemo.DemoRunner.Application.StateMachine;
using CoreBankDemo.DemoRunner.Infrastructure;
using CoreBankDemo.DemoRunner.Terminal;
using AppTerminal = Terminal.Gui.App.Application;

namespace CoreBankDemo.DemoRunner;

/// <summary>
/// Composition root. Binds only <c>--doctor</c>, <c>--show</c>, <c>--rehearse</c>,
/// <c>--scenario</c>, and <c>--resume</c>; no other argument or scenario/process logic
/// lives here (ADR-015 code map).
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        var repositoryRoot = FindRepositoryRoot();
        var artifactsDirectory = Path.Combine(repositoryRoot, ".demo-runner-artifacts");
        var scenarioPath = Path.Combine(AppContext.BaseDirectory, "Scenarios", $"{options.ScenarioName}.json");
        var sourceCommit = TryGetSourceCommit(repositoryRoot);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var environmentProbe = new EnvironmentProbe();
        var healthMonitor = new HealthMonitor(httpClient, TimeProvider.System);

        if (options.Doctor)
        {
            var doctor = new DoctorRunner(environmentProbe, healthMonitor);
            var ports = MergePorts(EndpointResolver.RegularProfilePorts, EndpointResolver.LoadTestProfilePorts);
            var report = await doctor.RunAsync(scenarioPath, ports, CancellationToken.None);
            foreach (var check in report.Checks)
            {
                Console.WriteLine($"[{(check.Passed ? "OK  " : "FAIL")}] {check.Name}{(string.IsNullOrEmpty(check.Remediation) ? string.Empty : $" — {check.Remediation}")}");
            }

            return report.AllPassed ? 0 : 1;
        }

        var loadResult = ScenarioLoader.LoadFromFile(scenarioPath);
        if (!loadResult.IsValid || loadResult.Scenario is null)
        {
            Console.WriteLine($"Scenario '{options.ScenarioName}' failed validation; no process was started:");
            foreach (var error in loadResult.Errors)
            {
                Console.WriteLine($"  - {error}");
            }

            return 1;
        }

        var scenario = loadResult.Scenario;
        var mode = options.Rehearse ? SessionMode.Rehearsal : SessionMode.Show;
        var runId = $"{scenario.Name}-{scenario.ScenarioVersion}-{mode}";

        var processAdapter = new AspireProcessAdapter(httpClient, repositoryRoot);
        var httpExecutor = new HttpActionExecutor(httpClient);
        var browserLauncher = new BrowserLauncher();
        var loadWorkflowRunner = new LoadWorkflowRunner(httpExecutor, TimeProvider.System);
        var journal = new FileJournal(artifactsDirectory);
        var proofPackStore = new FileProofPackStore(artifactsDirectory);

        var controller = new SessionController(
            scenario,
            mode,
            runId,
            sourceCommit,
            processAdapter,
            httpExecutor,
            healthMonitor,
            browserLauncher,
            loadWorkflowRunner,
            journal,
            TimeProvider.System);

        if (options.Resume)
        {
            await controller.ResumeAsync(CancellationToken.None);
        }

        return options.Rehearse
            ? await RehearsalRunner.RunAsync(controller, proofPackStore, CancellationToken.None)
            : RunShow(controller, healthMonitor);
    }

    // Version pinned centrally (ADR-015): the legacy static Application API is stable
    // for 2.4.17 even though the package is migrating to an instance-based model.
#pragma warning disable CS0618
    private static int RunShow(SessionController controller, IHealthMonitor healthMonitor)
    {
        AppTerminal.Init();
        try
        {
            var window = new MainWindow(controller, healthMonitor, async () =>
            {
                await controller.ShutdownAsync(CancellationToken.None);
                AppTerminal.RequestStop();
            });

            _ = window.RefreshAsync();
            AppTerminal.Run(window);
        }
        finally
        {
            AppTerminal.Shutdown();
        }

        return 0;
    }
#pragma warning restore CS0618

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CoreBankDemo.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static string TryGetSourceCommit(string repositoryRoot)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git", "rev-parse HEAD")
                {
                    WorkingDirectory = repositoryRoot,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                },
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length > 0 ? output : "unknown";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unknown";
        }
    }

    private static Dictionary<string, int> MergePorts(params IReadOnlyDictionary<string, int>[] sources)
    {
        var merged = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (var (key, value) in source)
            {
                merged[key] = value;
            }
        }

        return merged;
    }
}
