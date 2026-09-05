using System.Text.Json;
using System.Text.Json.Nodes;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>
/// Writes the generated Dev Proxy session configuration each AppHost prefers over its
/// checked-in profile, and reads back the levels a topology is actually running under.
/// <para>
/// Four rules make this safe to point at a live proxy. Every write is seeded from the
/// checked-in profile and <b>mutates</b> it in place, so plugin order, <c>pluginPath</c>,
/// <c>port</c>, <c>urlsToWatch</c> and any property a section declares that this console
/// does not model are all preserved (a plugin the seed does not declare at all is appended
/// after the ones it does). Every write lands in a gitignored <c>generated/</c> directory
/// that is a <i>sibling</i> of the checked-in file, because <c>errorsFile</c> resolves
/// relative to the rc file. A zero knob disables its plugin instead of deleting it, so the
/// file always describes all three knobs and a later read is never ambiguous — except that at
/// least one plugin is <b>always</b> left enabled, because Dev Proxy refuses to start
/// otherwise (see <see cref="EnsureAtLeastOneEnabledPlugin"/>). And the final file appears via
/// a temp-then-move.
/// </para>
/// <para>
/// <b>The temp-then-move is load-bearing — do not "fix" it back to an in-place write.</b>
/// Replacing the inode deliberately does <i>not</i> fire Dev Proxy's config-file watcher, and
/// that is the point. Dev Proxy 3.2.0's restart-on-config-change is broken: an in-place write
/// makes it log "Configuration file changed. Restarting proxy..." and then leave a proxy that
/// accepts TCP connections and immediately closes them, serving nothing until it is killed. A
/// brand-new process with the byte-identical config works perfectly, so the console restarts
/// the <c>devproxy</c> resource itself after each write (ADR-019, and
/// <c>OperatorConsoleController.RestartDevProxyAsync</c>). Both AppHosts additionally pass
/// <c>--no-watch</c>. An in-place write here would resurrect the dead-proxy bug.
/// </para>
/// </summary>
public sealed class DevProxySessionConfigWriter(string repositoryRoot) : IFaultInjector
{
    private const string LatencyPlugin = "LatencyPlugin";
    private const string RateLimitingPlugin = "RateLimitingPlugin";
    private const string ErrorPlugin = "GenericRandomErrorPlugin";
    private const string DefaultLatencySection = "latency";
    private const string DefaultRateLimitingSection = "rateLimiting";
    private const string DefaultErrorSection = "errorsCoreBank";
    private const string DefaultPluginPath = "~appFolder/plugins/DevProxy.Plugins.dll";
    private const string FallbackWatchedUrl = "http://127.0.0.1:5032/api/*";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public Task<FaultConfigReadResult> ReadAsync(TopologyProfile profile, CancellationToken ct)
    {
        var fallback = FaultLevels.CheckedInDefaults(profile);
        if (profile == TopologyProfile.None)
        {
            return Task.FromResult(new FaultConfigReadResult(true, FaultLevels.AllZero, false, string.Empty, null));
        }

        var generatedPath = ProfileRegistry.GeneratedConfigPath(repositoryRoot, profile);
        if (File.Exists(generatedPath))
        {
            var generated = TryReadLevels(generatedPath);
            if (generated.Levels is { } sessionLevels)
            {
                return Task.FromResult(new FaultConfigReadResult(true, sessionLevels, true, generatedPath, null));
            }

            // A generated config that will not parse must not silently become "no faults":
            // fall through to the checked-in profile and name the failed read.
            var checkedInAfterFailure = TryReadLevels(ProfileRegistry.CheckedInConfigPath(repositoryRoot, profile));
            return Task.FromResult(new FaultConfigReadResult(
                false,
                checkedInAfterFailure.Levels ?? fallback,
                false,
                generatedPath,
                generated.Error));
        }

        var checkedInPath = ProfileRegistry.CheckedInConfigPath(repositoryRoot, profile);
        var checkedIn = TryReadLevels(checkedInPath);
        return Task.FromResult(checkedIn.Levels is { } levels
            ? new FaultConfigReadResult(true, levels, false, checkedInPath, null)
            : new FaultConfigReadResult(false, fallback, false, checkedInPath, checkedIn.Error));
    }

    public Task<FaultConfigWriteResult> WriteAsync(
        TopologyProfile profile,
        FaultLevels levels,
        CancellationToken ct) =>
        Task.FromResult(Write(profile, levels.Normalized()));

    public Task<FaultConfigWriteResult> ResetAsync(TopologyProfile profile, CancellationToken ct) =>
        Task.FromResult(Write(profile, FaultLevels.AllZero));

    public Task<FaultConfigWriteResult> DeleteAsync(TopologyProfile profile, CancellationToken ct)
    {
        if (profile == TopologyProfile.None)
        {
            return Task.FromResult(new FaultConfigWriteResult(true, string.Empty, null));
        }

        var path = ProfileRegistry.GeneratedConfigPath(repositoryRoot, profile);
        var directory = ProfileRegistry.GeneratedConfigDirectory(repositoryRoot, profile);
        if (!Directory.Exists(directory))
        {
            // Nothing was ever generated for this profile, so there is nothing to shadow the
            // checked-in one. Deleting is idempotent on purpose: it runs on every Stop.
            return Task.FromResult(new FaultConfigWriteResult(true, path, null));
        }

        try
        {
            File.Delete(path);
            File.Delete(ProfileRegistry.GeneratedErrorsPath(repositoryRoot, profile));
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }

            return Task.FromResult(new FaultConfigWriteResult(true, path, null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new FaultConfigWriteResult(false, path, ex.Message));
        }
    }

    private FaultConfigWriteResult Write(TopologyProfile profile, FaultLevels levels)
    {
        if (profile == TopologyProfile.None)
        {
            return new FaultConfigWriteResult(false, string.Empty, "No topology is active, so there is no Dev Proxy profile to write.");
        }

        var targetPath = ProfileRegistry.GeneratedConfigPath(repositoryRoot, profile);
        var seedPath = ProfileRegistry.CheckedInConfigPath(repositoryRoot, profile);
        try
        {
            if (JsonNode.Parse(File.ReadAllText(seedPath)) is not JsonObject root)
            {
                return new FaultConfigWriteResult(false, targetPath, $"{seedPath} is not a Dev Proxy configuration object.");
            }

            var watched = WatchedUrls(root);
            var pluginPath = SeedPluginPath(root);
            if (root["plugins"] is not JsonArray plugins)
            {
                plugins = [];
                root["plugins"] = plugins;
            }

            var latencySection = EnsurePlugin(plugins, LatencyPlugin, DefaultLatencySection, pluginPath, watched, levels.InjectsLatency);
            var rateLimitSection = EnsurePlugin(plugins, RateLimitingPlugin, DefaultRateLimitingSection, pluginPath, watched, levels.InjectsThrottling);
            var errorSection = EnsurePlugin(plugins, ErrorPlugin, DefaultErrorSection, pluginPath, watched, levels.InjectsErrors);
            EnsureAtLeastOneEnabledPlugin(plugins);

            // Mutated, never replaced: a section may declare properties this console does not
            // model, and rewriting the object wholesale would silently drop them.
            var latency = Section(root, latencySection);
            latency["minMs"] = levels.LatencyFloorMs;
            latency["maxMs"] = levels.LatencyCeilingMs;

            var rateLimiting = Section(root, rateLimitSection);
            rateLimiting["rateLimit"] = levels.ThrottleRequestsPerWindow;
            rateLimiting["costPerRequest"] ??= 1;
            rateLimiting["resetTimeWindowSeconds"] ??= FaultLevels.ThrottleWindowSeconds;

            var errors = Section(root, errorSection);
            // Deliberately a bare sibling file name: Dev Proxy resolves errorsFile relative
            // to the rc file it loaded, which is the generated one.
            errors["errorsFile"] = ProfileRegistry.GeneratedErrorsFileName;
            errors["rate"] = levels.ErrorRatePercent;

            Directory.CreateDirectory(ProfileRegistry.GeneratedConfigDirectory(repositoryRoot, profile));
            WriteAtomically(ProfileRegistry.GeneratedErrorsPath(repositoryRoot, profile), BuildErrorsFile(profile, watched));
            WriteAtomically(targetPath, root.ToJsonString(WriteOptions));
            return new FaultConfigWriteResult(true, targetPath, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FaultConfigWriteResult(false, targetPath, ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return new FaultConfigWriteResult(false, targetPath, $"{seedPath} could not be parsed: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes via a sibling temp file and one <see cref="File.Move(string, string, bool)"/>.
    /// This is not only about torn reads: replacing the inode is what keeps Dev Proxy 3.2.0's
    /// broken config watcher from firing, leaving the console's controlled resource restart as
    /// the single mechanism by which a new config takes effect. See the type remarks.
    /// </summary>
    private static void WriteAtomically(string path, string content)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The write already failed; a leftover temp file must not mask that error.
            }

            throw;
        }
    }

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        root[name] = created;
        return created;
    }

    /// <summary>
    /// Finds the plugin entries for a knob, adding one when the seed profile declares none
    /// (the LoadTests profile ships latency only), and sets the enabled flag on <b>every</b>
    /// match — a seed may legitimately declare the same plugin twice against different URL
    /// surfaces, and leaving one enabled would keep injecting after the knob reached zero.
    /// Returns the config section name the plugin reads, so the caller writes to the section
    /// the seed actually named rather than assuming the default.
    /// </summary>
    /// <summary>
    /// Guarantees the generated config always has at least one enabled plugin, whatever the
    /// knobs read.
    /// <para>
    /// Dev Proxy will not start with every plugin disabled — it throws
    /// <c>InvalidOperationException: No plugins configured or enabled. Please add a plugin to
    /// the configuration file.</c> from <c>PluginServiceExtensions.AddPlugins</c>. Without this
    /// guarantee, panic-off would be the console's most destructive control: it writes
    /// all-zero, the restart brings up a proxy that exits immediately, and every call routed
    /// through <c>HTTP_PROXY</c> fails outright. Arming would break the same way, because
    /// <see cref="ResetAsync"/> writes all-zero before the AppHost starts.
    /// </para>
    /// <para>
    /// The keep-alive is <c>LatencyPlugin</c> at <c>minMs: 0, maxMs: 0</c>, which is verified
    /// to start normally and inject nothing (measured pass-through). "Enabled" here therefore
    /// never means "injecting", and the readback is unchanged: a zero latency section still
    /// reads as a zero latency knob, so all-zero still round-trips to
    /// <see cref="FaultLevels.AllZero"/> and the chip still reads <c>Armed</c>.
    /// </para>
    /// </summary>
    private static void EnsureAtLeastOneEnabledPlugin(JsonArray plugins)
    {
        if (plugins.OfType<JsonObject>().Any(plugin => AsBool(plugin["enabled"]) == true))
        {
            return;
        }

        foreach (var plugin in plugins
            .OfType<JsonObject>()
            .Where(plugin => string.Equals(AsString(plugin["name"]), LatencyPlugin, StringComparison.Ordinal)))
        {
            plugin["enabled"] = true;
        }
    }

    private static string EnsurePlugin(
        JsonArray plugins,
        string pluginName,
        string defaultSection,
        string pluginPath,
        JsonArray watched,
        bool enabled)
    {
        var matches = plugins
            .OfType<JsonObject>()
            .Where(plugin => string.Equals(AsString(plugin["name"]), pluginName, StringComparison.Ordinal))
            .ToList();
        if (matches.Count > 0)
        {
            foreach (var plugin in matches)
            {
                plugin["enabled"] = enabled;
            }

            return AsString(matches[0]["configSection"]) ?? defaultSection;
        }

        plugins.Add(new JsonObject
        {
            ["name"] = pluginName,
            ["enabled"] = enabled,
            ["pluginPath"] = pluginPath,
            ["configSection"] = defaultSection,
            ["urlsToWatch"] = watched.DeepClone(),
        });
        return defaultSection;
    }

    /// <summary>
    /// Builds the errors file covering <b>the same URL surface the profile's other plugins
    /// watch</b>. The checked-in errors file is a read-only preset source for the response
    /// bodies only: copying it verbatim would scope injection to
    /// <c>POST /api/accounts/validate</c>, which no request the console makes passes through,
    /// so the error knob could be raised but never observed.
    /// </summary>
    private string BuildErrorsFile(TopologyProfile profile, JsonArray watched)
    {
        var responses = SeedErrorResponses(profile) ?? new JsonArray(
            ErrorResponse(503, "Service temporarily unavailable"),
            ErrorResponse(429, "Too many requests"),
            ErrorResponse(500, "Internal server error"));

        var entries = new JsonArray();
        foreach (var url in watched.Select(AsString).Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.Ordinal))
        {
            entries.Add(new JsonObject
            {
                // No "method": every verb the watched surface serves is eligible, so an
                // injected error can actually reach a call the console makes.
                ["request"] = new JsonObject { ["url"] = url },
                ["responses"] = responses.DeepClone(),
            });
        }

        if (entries.Count == 0)
        {
            entries.Add(new JsonObject
            {
                ["request"] = new JsonObject { ["url"] = FallbackWatchedUrl },
                ["responses"] = responses.DeepClone(),
            });
        }

        return new JsonObject { ["errors"] = entries }.ToJsonString(WriteOptions);
    }

    private JsonArray? SeedErrorResponses(TopologyProfile profile)
    {
        var checkedInErrors = ProfileRegistry.CheckedInErrorsPath(repositoryRoot, profile);
        if (!File.Exists(checkedInErrors))
        {
            return null;
        }

        try
        {
            return (JsonNode.Parse(File.ReadAllText(checkedInErrors)) as JsonObject)?["errors"] is JsonArray errors
                && errors.OfType<JsonObject>().FirstOrDefault()?["responses"] is JsonArray responses
                && responses.Count > 0
                    ? (JsonArray)responses.DeepClone()
                    : null;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException
            or InvalidOperationException)
        {
            // The shipped bodies are a nicety; the built-in 503/429/500 set is the contract.
            return null;
        }
    }

    private static JsonObject ErrorResponse(int statusCode, string message) => new()
    {
        ["statusCode"] = statusCode,
        ["headers"] = new JsonArray
        {
            new JsonObject { ["name"] = "Content-Type", ["value"] = "application/json" },
        },
        ["body"] = new JsonObject { ["error"] = message },
    };

    private static JsonArray WatchedUrls(JsonObject root)
    {
        if (root["urlsToWatch"] is JsonArray rootUrls && rootUrls.Any(url => AsString(url) is not null))
        {
            return (JsonArray)rootUrls.DeepClone();
        }

        var fromPlugin = (root["plugins"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(plugin => plugin["urlsToWatch"] as JsonArray)
            .FirstOrDefault(urls => urls is not null && urls.Any(url => AsString(url) is not null));
        return fromPlugin is null
            ? new JsonArray(FallbackWatchedUrl)
            : (JsonArray)fromPlugin.DeepClone();
    }

    private static string SeedPluginPath(JsonObject root) =>
        (root["plugins"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(plugin => AsString(plugin["pluginPath"]))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
        ?? DefaultPluginPath;

    private static (FaultLevels? Levels, string? Error) TryReadLevels(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                return (null, $"{path} is not a Dev Proxy configuration object.");
            }

            var latency = ReadSection(root, LatencyPlugin, DefaultLatencySection);
            var rateLimit = ReadSection(root, RateLimitingPlugin, DefaultRateLimitingSection);
            var errors = ReadSection(root, ErrorPlugin, DefaultErrorSection);
            return (new FaultLevels(
                ReadInt(errors, "rate"),
                ReadInt(latency, "minMs"),
                ReadInt(latency, "maxMs"),
                ReadInt(rateLimit, "rateLimit")).Normalized(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"{path} could not be read: {ex.Message}");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return (null, $"{path} could not be parsed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the config section a plugin reads, or <c>null</c> when every declaration of it
    /// is absent or disabled — which is exactly how a zero knob is written, so absent,
    /// disabled and zero all read back identically.
    /// </summary>
    private static JsonObject? ReadSection(JsonObject root, string pluginName, string defaultSection)
    {
        var enabled = (root["plugins"] as JsonArray)?
            .OfType<JsonObject>()
            .Where(item => string.Equals(AsString(item["name"]), pluginName, StringComparison.Ordinal))
            .FirstOrDefault(item => AsBool(item["enabled"]) != false);
        if (enabled is null)
        {
            return null;
        }

        return root[AsString(enabled["configSection"]) ?? defaultSection] as JsonObject;
    }

    private static int ReadInt(JsonObject? section, string property) =>
        section?[property] is JsonValue value
        && value.GetValueKind() == JsonValueKind.Number
        && value.TryGetValue<int>(out var number)
            ? number
            : 0;

    /// <summary>
    /// Reads a JSON property as a string without throwing on a wrong-typed node. Seed configs
    /// are hand-editable files outside this console's control, so a number or object where a
    /// string belongs must degrade to "not present", never escape as an
    /// <see cref="InvalidOperationException"/> that no caller catches.
    /// </summary>
    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static bool? AsBool(JsonNode? node) =>
        node is JsonValue value
        && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False
        && value.TryGetValue<bool>(out var flag)
            ? flag
            : null;
}
