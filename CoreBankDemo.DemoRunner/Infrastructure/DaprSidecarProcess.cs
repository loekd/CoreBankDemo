using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <param name="AppId">
/// The sidecar's app-id. It must differ from every banking service's, because Dapr derives the
/// Redis consumer group from it and a shared id would divert deliveries away from PaymentsAPI.
/// </param>
/// <param name="ResourcesPath">
/// The profile's Dapr components directory. Regular and LoadTests point at different Redis
/// ports, so a mismatched directory connects to nothing and the feed is silently empty.
/// </param>
public sealed record DaprSidecarLaunch(
    string AppId,
    string ResourcesPath,
    int GrpcPort,
    int HttpPort,
    int MetricsPort,
    // daprd defaults this to 50002, which the AppHost's own sidecars are the likeliest holders
    // of. "Every port passed explicitly" has to mean every port.
    int InternalGrpcPort);

public sealed record DaprSidecarHandle(int ProcessId, int GrpcPort, int HttpPort, string Command);

public sealed record DaprSidecarStartResult(bool Succeeded, DaprSidecarHandle? Handle, string Detail);

/// <summary>
/// Runs the console's own long-lived <c>daprd</c> child process.
/// </summary>
/// <remarks>
/// <see cref="CommandRunner"/> deliberately runs to completion and cannot serve this: a sidecar
/// outlives the call that started it. The ownership discipline is
/// <see cref="AspireProcessAdapter"/>'s — verify the PID after start, never kill by name or
/// port, and terminate only the exact PID this session spawned, reusing
/// <see cref="OwnedProcessTerminator"/>.
/// </remarks>
public interface IDaprSidecar : IAsyncDisposable
{
    /// <summary>True only while the exact PID this session spawned is still alive.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Bounded, redacted tail of the sidecar's own output. Read into the feed's failure detail,
    /// so a sidecar that refused to come up explains itself rather than leaving the operator
    /// with a bare "unavailable".
    /// </summary>
    string RecentOutput { get; }

    Task<DaprSidecarStartResult> StartAsync(DaprSidecarLaunch launch, CancellationToken ct);

    Task StopAsync(CancellationToken ct);
}

public sealed class DaprSidecarProcess : IDaprSidecar
{
    internal const string DefaultExecutable = "daprd";
    private const int MaximumOutputLength = 16 * 1024;
    private static readonly TimeSpan DefaultReadinessTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan GracefulWait = TimeSpan.FromSeconds(5);

    private readonly IOwnedProcessTerminator _terminator;
    private readonly TimeProvider _time;
    private readonly HttpClient _health;
    private readonly string _executable;
    private readonly TimeSpan _readinessTimeout;
    private readonly object _sync = new();
    private readonly StringBuilder _output = new();

    private Process? _process;
    private DaprSidecarHandle? _handle;

    public DaprSidecarProcess()
        : this(new OwnedProcessTerminator(), TimeProvider.System, null)
    {
    }

    /// <param name="executable">
    /// The binary to launch. Overridden only by tests, which need the ownership discipline —
    /// a process that exits at once, one that never becomes ready — provable without a Dapr
    /// installation on the machine running them.
    /// </param>
    /// <param name="readinessTimeout">
    /// How long the sidecar may take to report healthy. Shortened by tests for the same reason.
    /// </param>
    public DaprSidecarProcess(
        IOwnedProcessTerminator terminator,
        TimeProvider time,
        HttpClient? health,
        string? executable = null,
        TimeSpan? readinessTimeout = null)
    {
        _terminator = terminator;
        _time = time;
        _executable = executable ?? ResolveExecutable();
        _readinessTimeout = readinessTimeout ?? DefaultReadinessTimeout;
        // Strictly loopback, and explicitly proxy-free: a machine with HTTP_PROXY set would
        // otherwise send the sidecar's own readiness probe out through the proxy.
        _health = health ?? new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
    }

    /// <summary>
    /// Where <c>daprd</c> is looked for, in order: the bare name so <c>PATH</c> wins when the
    /// binary is on it, then the location <c>dapr init</c> actually installs to.
    /// </summary>
    /// <remarks>
    /// <c>dapr init</c> puts the <c>dapr</c> CLI on <c>PATH</c> but installs the runtime into
    /// <c>~/.dapr/bin</c>, which it deliberately does not add. Looking only on <c>PATH</c>
    /// therefore reports "no feed" on an ordinary, correctly installed Dapr machine — the
    /// common case rather than the broken one. The bare name stays first so an explicitly
    /// installed or shimmed <c>daprd</c> still takes precedence over the default install.
    /// </remarks>
    internal static IReadOnlyList<string> CandidateExecutables()
    {
        var name = OperatingSystem.IsWindows() ? "daprd.exe" : DefaultExecutable;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home)
            ? [name]
            : [name, Path.Combine(home, ".dapr", "bin", name)];
    }

    /// <summary>
    /// The first candidate that exists on disk, falling back to the bare name so the failure
    /// is reported by the start attempt — with its own message — rather than guessed at here.
    /// </summary>
    internal static string ResolveExecutable()
    {
        var candidates = CandidateExecutables();
        foreach (var candidate in candidates)
        {
            // The bare name is not a path to probe: leave it to the process start, which
            // searches PATH properly on every platform.
            if (Path.IsPathRooted(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public string RecentOutput
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    public async Task<DaprSidecarStartResult> StartAsync(DaprSidecarLaunch launch, CancellationToken ct)
    {
        await StopAsync(ct);

        // Every port is passed explicitly. --metrics-port defaults to 9090, which the AppHost's
        // own sidecars and plenty else already hold; letting it default would collide.
        var arguments = new List<string>
        {
            "--app-id", launch.AppId,
            "--resources-path", launch.ResourcesPath,
            "--dapr-grpc-port", launch.GrpcPort.ToString(),
            "--dapr-http-port", launch.HttpPort.ToString(),
            "--dapr-internal-grpc-port", launch.InternalGrpcPort.ToString(),
            "--metrics-port", launch.MetricsPort.ToString(),

            // Nothing here schedules jobs or uses actors, and both hosts are otherwise dialled
            // on their defaults, which are not running for this console.
            "--scheduler-host-address", string.Empty,
            "--placement-host-address", string.Empty,
            "--log-level", "warn",
        };

        // Deliberately no --app-port: a streaming subscription is outbound-dialled, so the
        // console hosts no inbound listener and opens no port of its own.
        var command = $"{_executable} {string.Join(' ', arguments.Select(Quote))}";
        var startInfo = new ProcessStartInfo(_executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);

        lock (_output)
        {
            _output.Clear();
        }

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return new DaprSidecarStartResult(false, null, $"{_executable} did not start.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            return new DaprSidecarStartResult(
                false,
                null,
                $"{_executable} could not be started ({ex.Message}). Looked on PATH and in "
                    + $"{string.Join(", ", CandidateExecutables().Where(Path.IsPathRooted))}. "
                    + "Run 'dapr init' if the runtime is not installed.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Verify the PID before claiming ownership, exactly as the AppHost adapter does: a
        // process that exited immediately is not a sidecar, and terminating a recycled PID
        // later would kill something this console never started.
        if (process.HasExited)
        {
            var detail = $"{_executable} exited immediately with code {process.ExitCode}. {RecentOutput}";
            process.Dispose();
            return new DaprSidecarStartResult(false, null, JournalRedaction.Apply(detail));
        }

        var handle = new DaprSidecarHandle(process.Id, launch.GrpcPort, launch.HttpPort, command);
        lock (_sync)
        {
            _process = process;
            _handle = handle;
        }

        DaprSidecarStartResult ready;
        try
        {
            ready = await WaitForReadyAsync(process, launch.HttpPort, ct);
        }
        catch (Exception)
        {
            // A cancelled or faulted readiness wait must not leave a live daprd behind: the
            // PID is already owned above, so tearing down here is what makes that true.
            await StopAsync(CancellationToken.None);
            throw;
        }

        if (!ready.Succeeded)
        {
            await StopAsync(CancellationToken.None);
            return ready;
        }

        return new DaprSidecarStartResult(true, handle, $"{_executable} PID {handle.ProcessId} is serving gRPC on {handle.GrpcPort}.");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Process? process;
        DaprSidecarHandle? handle;
        lock (_sync)
        {
            process = _process;
            handle = _handle;
            _process = null;
            _handle = null;
        }

        if (process is null || handle is null)
        {
            return;
        }

        using (process)
        {
            if (!process.HasExited)
            {
                try
                {
                    // The exact PID tree this session spawned -- never a process-name or
                    // port-based sweep, which is the rule the AppHost adapter follows too.
                    // daprd has no in-process shutdown signal reachable from .NET, so this is
                    // a direct terminate rather than a graceful request; the terminator below
                    // is what confirms the process is actually gone.
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Already gone between the check and the call; the terminator confirms below.
                }
            }

            await _terminator.EnsureExitedAsync(handle.ProcessId, GracefulWait, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _health.Dispose();
    }

    /// <summary>
    /// Waits for the sidecar's own health endpoint rather than sleeping a fixed interval: a
    /// gRPC subscription dialled before the sidecar is listening fails, and a fixed sleep is
    /// the same "elapsed time as proof" mistake this console refuses everywhere else.
    /// </summary>
    private async Task<DaprSidecarStartResult> WaitForReadyAsync(Process process, int httpPort, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + _readinessTimeout;
        var probe = new Uri($"http://127.0.0.1:{httpPort}/v1.0/healthz");
        while (_time.GetUtcNow() < deadline)
        {
            if (process.HasExited)
            {
                return new DaprSidecarStartResult(
                    false,
                    null,
                    JournalRedaction.Apply($"{_executable} exited with code {process.ExitCode} before it became ready. {RecentOutput}"));
            }

            try
            {
                using var response = await _health.GetAsync(probe, ct);
                if ((int)response.StatusCode is >= 200 and < 300)
                {
                    return new DaprSidecarStartResult(true, null, "ready");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Not listening yet.
            }

            await Task.Delay(ReadinessPollInterval, _time, ct);
        }

        return new DaprSidecarStartResult(
            false,
            null,
            $"{_executable} did not report healthy on 127.0.0.1:{httpPort} within {_readinessTimeout.TotalSeconds:F0}s.");
    }

    private void Capture(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        lock (_output)
        {
            _output.AppendLine(JournalRedaction.Apply(line));
            if (_output.Length > MaximumOutputLength)
            {
                _output.Remove(0, _output.Length - MaximumOutputLength);
            }
        }
    }

    private static string Quote(string argument) => argument.Length == 0 ? "\"\"" : argument;
}
