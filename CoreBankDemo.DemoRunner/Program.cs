using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Doctor;
using CoreBankDemo.DemoRunner.Infrastructure;
using CoreBankDemo.DemoRunner.Terminal;
using AppTerminal = Terminal.Gui.App.Application;

namespace CoreBankDemo.DemoRunner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (options.Help)
        {
            Console.WriteLine(CliOptions.HelpText);
            return 0;
        }

        if (!options.IsValid)
        {
            foreach (var error in options.Errors)
            {
                Console.Error.WriteLine(error);
            }

            Console.Error.WriteLine(CliOptions.HelpText);
            return 2;
        }

        var repositoryRoot = FindRepositoryRoot();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        var aspire = new AspireCliAdapter(repositoryRoot, TimeProvider.System);
        var doctor = new DoctorRunner(
            new EnvironmentProbe(),
            new HealthMonitor(httpClient, TimeProvider.System),
            aspire,
            BuildPortRequirements());

        if (options.Doctor)
        {
            // Arming is off by default, so --doctor reports a missing Dev Proxy as "not
            // required" rather than failing: the binary is only a prerequisite once the
            // operator turns arming on in the Resources workspace.
            var report = await doctor.RunAsync(faultArmingRequested: false, CancellationToken.None);
            foreach (var check in report.Checks)
            {
                Console.WriteLine($"[{(check.Passed ? "OK  " : "FAIL")}] {check.Name}{(string.IsNullOrEmpty(check.Remediation) ? string.Empty : $" — {check.Remediation}")}");
            }

            return report.AllPassed ? 0 : 1;
        }

        // The console's own transaction-events listener. It spawns and owns a daprd sidecar
        // of its own, so its app-id -- and therefore its Redis consumer group -- is distinct
        // from every banking service's and PaymentsAPI keeps receiving every event.
        await using var outcomeFeed = new DaprOutcomeFeed(repositoryRoot, new EnvironmentProbe(), TimeProvider.System);

        var controller = new OperatorConsoleController(
            aspire,
            new AspireProcessAdapter(repositoryRoot),
            new HttpPaymentGateway(httpClient),
            new LoadWorkflowRunner(httpClient, aspire, TimeProvider.System),
            new SessionEvidenceExporter(repositoryRoot, TimeProvider.System),
            new DevProxySessionConfigWriter(repositoryRoot),
            new BrowserLauncher(),
            doctor,
            outcomeFeed,
            TimeProvider.System);

        return RunConsole(controller);
    }

#pragma warning disable CS0618
    private static int RunConsole(OperatorConsoleController controller)
    {
        AppTerminal.Init();

        // Terminal.Gui's own clipboard shells out to xclip, which exists in the sandbox
        // but has no display to hand the text to, so Ctrl+C silently copied nothing.
        // OSC 52 asks the terminal emulator itself instead; see Osc52Clipboard.
        var clipboard = new Osc52Clipboard(
            Console.Out,
            Environment.GetEnvironmentVariable("TERM"),
            Environment.GetEnvironmentVariable("TMUX"));
        if (AppTerminal.Driver is { } driver)
        {
            driver.Clipboard = clipboard;
        }

        var window = new MainWindow(controller, async () =>
        {
            await controller.ShutdownAsync(CancellationToken.None);
            AppTerminal.RequestStop();
        });
        clipboard.Copied += window.ShowClipboardResult;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            window.RequestExitAsync().GetAwaiter().GetResult();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            // Preflight probes ports and the Aspire CLI; running it before the first paint
            // left the operator staring at an empty terminal for several seconds.
            window.BeginInitialRefresh();
            AppTerminal.Run(window);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            try
            {
                controller.ShutdownAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"Could not stop the owned AppHost cleanly: {ex.Message}");
            }
            AppTerminal.Shutdown();
        }

        return 0;
    }
#pragma warning restore CS0618

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CoreBankDemo.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static IReadOnlyList<DoctorPortRequirement> BuildPortRequirements()
    {
        return
        [
            .. EndpointResolver.RegularProfilePorts.Select(pair =>
                new DoctorPortRequirement(TopologyProfile.Regular, pair.Key, pair.Value)),
            .. EndpointResolver.LoadTestProfilePorts.Select(pair =>
                new DoctorPortRequirement(TopologyProfile.LoadTests, pair.Key, pair.Value)),
        ];
    }
}
