using System.Runtime.InteropServices;
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

        // Armed before anything else can fail. A Terminal.Gui console puts the terminal into the
        // alternate screen and raw mode, and only disposing the IApplication instance undoes that
        // -- so any exit that misses it leaves the operator looking at a dead full-screen view in
        // a window that no longer echoes typing, with nothing on screen saying why.
        TerminalCrashGuard.Install(Path.Combine(repositoryRoot, ".demo-runner-artifacts"));
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

        // The `await using` above only runs on an orderly exit. A SIGTERM, a SIGHUP from a
        // terminal window closing, or an outer `timeout` kills the console outright and leaves
        // its daprd running -- and that orphan stays in this console's Redis consumer group,
        // where it is handed a share of every transaction-events delivery that nothing then
        // reads. The next run would report Listening while a portion of its payments never
        // resolved. SIGINT is deliberately absent: Console.CancelKeyPress already turns Ctrl+C
        // into an orderly shutdown, and tearing the feed down underneath that would break it.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => Terminate(outcomeFeed, "SIGTERM"));
        using var sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => Terminate(outcomeFeed, "SIGHUP"));
        using var sigquit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, _ => Terminate(outcomeFeed, "SIGQUIT"));

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

        var theme = CliOptions.ResolveTheme(
            options.Theme,
            Environment.GetEnvironmentVariable(CliOptions.ThemeEnvironmentVariable));

        return RunConsole(controller, theme);
    }

    private static int RunConsole(OperatorConsoleController controller, ThemeMode theme)
    {
        Exception? crash = null;
        var app = AppTerminal.Create().Init();
        TerminalCrashGuard.AttachApplication(app);

        // Terminal.Gui's own clipboard shells out to xclip, which exists in the sandbox
        // but has no display to hand the text to, so Ctrl+C silently copied nothing.
        // OSC 52 asks the terminal emulator itself instead; see Osc52Clipboard.
        var clipboard = new Osc52Clipboard(
            Console.Out,
            Environment.GetEnvironmentVariable("TERM"),
            Environment.GetEnvironmentVariable("TMUX"));
        if (app.Driver is { } driver)
        {
            driver.Clipboard = clipboard;
        }

        var window = new MainWindow(app, controller, async () =>
        {
            await controller.ShutdownAsync(CancellationToken.None);
            app.RequestStop();
        }, theme);
        clipboard.Copied += window.ShowClipboardResult;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            window.RequestExitAsync().GetAwaiter().GetResult();
        };
        Console.CancelKeyPress += cancelHandler;
        var reportedElsewhere = false;
        try
        {
            // Preflight probes ports and the Aspire CLI; running it before the first paint
            // left the operator staring at an empty terminal for several seconds.
            window.BeginInitialRefresh();
            // Without an error handler, Run rethrows -- unwinding past the run loop and leaving
            // the runtime to dump a stack trace over a terminal still in its alternate screen.
            // Returning true keeps the loop's own teardown intact, and RequestStop then ends it
            // at the next iteration, so the finally below performs an ordinary disposal. The
            // report is deliberately deferred until after that: it restores the terminal itself,
            // which must not happen underneath a live run loop.
            app.Run(window, errorHandler: exception =>
            {
                crash ??= exception;

                // Stopped rather than resumed: a console that keeps running after an unhandled
                // fault is a console that may now be showing something untrue.
                app.RequestStop();
                return true;
            });
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            if (TerminalCrashGuard.TakeApplication() is { } application)
            {
                try
                {
                    controller.ShutdownAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine($"Could not stop the owned AppHost cleanly: {ex.Message}");
                }
                application.Dispose();
            }
            else
            {
                // The crash guard took the instance first: a fault on a thread the console does
                // not own, or a termination signal, is being reported right now, and disposing
                // the instance is what ended the run loop above. The report restores the terminal
                // and names the fault itself; the owned AppHost is deliberately left alone, as
                // the report tells the operator it is.
                reportedElsewhere = true;
            }
        }

        if (reportedElsewhere)
        {
            // Returning before the report has finished would end the process with exit code 0
            // underneath it: no banner, no crash file, and a status that says nothing went wrong.
            TerminalCrashGuard.WaitForReport(TimeSpan.FromSeconds(10));
            return 70;
        }

        if (crash is null)
        {
            return 0;
        }

        TerminalCrashGuard.Report(crash, "The console's UI thread raised an unhandled exception");

        // Non-zero so a wrapper script or an outer `aspire`/CI step can tell a crash from a quit.
        return 70;
    }

    /// <summary>
    /// Best-effort, bounded teardown from a signal handler. The process is on its way out, so
    /// the only failure that matters is hanging: a sidecar that outlives this console poisons
    /// the next run, but a console that will not die poisons the demo happening right now.
    /// </summary>
    private static void Terminate(DaprOutcomeFeed feed, string signal)
    {
        // Order matters: hand the terminal back first, because the sidecar teardown below can
        // take seconds and the operator would spend them looking at a frozen full-screen view.
        TerminalCrashGuard.Report(null, $"The console was stopped by {signal}");
        StopFeed(feed);
    }

    private static void StopFeed(DaprOutcomeFeed feed)
    {
        try
        {
            feed.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is AggregateException or ObjectDisposedException or InvalidOperationException)
        {
            // Nothing useful can be reported from here; the terminal is already going away.
        }
    }

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
