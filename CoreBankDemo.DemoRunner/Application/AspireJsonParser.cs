using System.Text.Json;

namespace CoreBankDemo.DemoRunner.Application;

public static class AspireJsonParser
{
    public static TopologySnapshot Parse(TopologyProfile profile, string json, DateTimeOffset capturedAt)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var candidates = new List<ResourceCandidate>();
            Visit(document.RootElement, candidates);
            var dashboardUrl = FindFirstUrl(document.RootElement, "dashboardUrl");

            var resources = KnownResources.RequiredFor(profile)
                .Concat(KnownResources.ResourceCommandAllowList)
                .Distinct(StringComparer.Ordinal)
                .Select(name => BuildResource(name, candidates))
                .Where(resource => resource is not null)
                .Cast<ResourceSnapshot>()
                .OrderBy(resource => resource.Name, StringComparer.Ordinal)
                .ToList();

            var required = KnownResources.RequiredFor(profile);
            var present = resources.Select(resource => resource.Name).ToHashSet(StringComparer.Ordinal);
            var missing = required.Where(name => !present.Contains(name)).ToList();
            var replicaMismatches = resources
                .Where(resource => required.Contains(resource.Name)
                    && resource.ReplicaCount != KnownResources.ExpectedReplicaCount(resource.Name))
                .Select(resource => $"{resource.Name} expected {KnownResources.ExpectedReplicaCount(resource.Name)}, found {resource.ReplicaCount}")
                .ToList();
            var fingerprintMatch = missing.Count == 0 && replicaMismatches.Count == 0;
            var fingerprint = string.Join(
                ",",
                resources.Select(resource => $"{resource.Name}:{resource.ReplicaCount}"));

            return new TopologySnapshot(
                profile,
                capturedAt,
                true,
                fingerprintMatch,
                fingerprint,
                resources,
                fingerprintMatch
                    ? null
                    : $"Fingerprint mismatch; missing: {string.Join(", ", missing)}; replicas: {string.Join(", ", replicaMismatches)}.",
                dashboardUrl);
        }
        catch (JsonException ex)
        {
            var unknown = KnownResources.RequiredFor(profile)
                .Select(name => new ResourceSnapshot(name, ResourceCondition.Unknown, "Unknown", [], Detail: "Aspire JSON was malformed."))
                .ToList();
            return new TopologySnapshot(
                profile,
                capturedAt,
                true,
                false,
                string.Empty,
                unknown,
                $"Aspire returned unparseable JSON: {ex.Message}");
        }
    }

    private static ResourceSnapshot? BuildResource(string knownName, IReadOnlyList<ResourceCandidate> candidates)
    {
        var matches = candidates
            .Where(candidate => MatchesKnownName(candidate, knownName))
            .ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        var conditions = matches.Select(candidate => MapCondition(candidate.State, candidate.Health)).ToList();
        var condition = conditions.Contains(ResourceCondition.Stopped)
            && conditions.Any(item => item is ResourceCondition.Healthy or ResourceCondition.Running)
                ? ResourceCondition.Degraded
                : conditions.Aggregate(BetterCondition);
        var health = string.Join(
            "/",
            matches.Select(candidate => candidate.Health).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
        var endpoints = matches.SelectMany(candidate => candidate.Endpoints).Distinct(StringComparer.Ordinal).ToList();
        var detail = string.Join(
            "; ",
            matches.Select(candidate => candidate.State).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
        return new ResourceSnapshot(
            knownName,
            condition,
            string.IsNullOrWhiteSpace(health) ? ConditionLabel(condition) : health,
            endpoints,
            Math.Max(1, matches.Count),
            detail,
            matches.Select(candidate => candidate.InstanceName).Distinct(StringComparer.Ordinal).ToList(),
            string.Join(",", matches.Select(candidate => candidate.ExecutionIdentity).Where(value => !string.IsNullOrWhiteSpace(value))),
            IntersectCommands(matches));
    }

    private static bool MatchesKnownName(ResourceCandidate candidate, string knownName)
    {
        var nameMatches = string.Equals(candidate.DisplayName, knownName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.InstanceName, knownName, StringComparison.OrdinalIgnoreCase)
            || candidate.InstanceName.StartsWith(knownName + "-", StringComparison.OrdinalIgnoreCase)
            || candidate.InstanceName.StartsWith(knownName + "_", StringComparison.OrdinalIgnoreCase);
        if (!nameMatches)
        {
            return false;
        }

        var isProject = knownName is KnownResources.PaymentsApi
            or KnownResources.CoreBankApi
            or KnownResources.LoadTestSupport
            or KnownResources.LoadTestInitializer;
        return !isProject
            ? !candidate.DisplayName.Contains("dapr", StringComparison.OrdinalIgnoreCase)
            : string.IsNullOrWhiteSpace(candidate.ResourceType)
              || candidate.ResourceType.Contains("project", StringComparison.OrdinalIgnoreCase);
    }

    private static ResourceCondition BetterCondition(ResourceCondition left, ResourceCondition right)
    {
        static int Rank(ResourceCondition condition) => condition switch
        {
            ResourceCondition.Failed => 9,
            ResourceCondition.Unreachable => 8,
            ResourceCondition.Unknown => 7,
            ResourceCondition.Degraded => 6,
            ResourceCondition.Starting => 5,
            ResourceCondition.Running => 4,
            ResourceCondition.Healthy => 3,
            ResourceCondition.Completed => 2,
            ResourceCondition.Stopped => 1,
            _ => 0,
        };

        return Rank(left) >= Rank(right) ? left : right;
    }

    private static ResourceCondition MapCondition(string state, string health)
    {
        var value = $"{state} {health}".ToLowerInvariant();
        if (value.Contains("failed", StringComparison.Ordinal)
            || value.Contains("error", StringComparison.Ordinal)
            || value.Contains("unhealthy", StringComparison.Ordinal))
        {
            return ResourceCondition.Failed;
        }

        if (value.Contains("degraded", StringComparison.Ordinal))
        {
            return ResourceCondition.Degraded;
        }

        if (value.Contains("starting", StringComparison.Ordinal)
            || value.Contains("waiting", StringComparison.Ordinal)
            || value.Contains("restarting", StringComparison.Ordinal))
        {
            return ResourceCondition.Starting;
        }

        if (value.Contains("finished", StringComparison.Ordinal)
            || value.Contains("completed", StringComparison.Ordinal)
            || value.Contains("exited", StringComparison.Ordinal))
        {
            return ResourceCondition.Completed;
        }

        if (value.Contains("stopped", StringComparison.Ordinal)
            || value.Contains("not started", StringComparison.Ordinal))
        {
            return ResourceCondition.Stopped;
        }

        if (value.Contains("healthy", StringComparison.Ordinal))
        {
            return ResourceCondition.Healthy;
        }

        if (value.Contains("running", StringComparison.Ordinal))
        {
            return ResourceCondition.Running;
        }

        return ResourceCondition.Unknown;
    }

    private static string ConditionLabel(ResourceCondition condition) => condition switch
    {
        ResourceCondition.Healthy => "Healthy",
        ResourceCondition.Running => "Running",
        ResourceCondition.Starting => "Starting",
        ResourceCondition.Stopped => "Stopped",
        ResourceCondition.Completed => "Completed",
        ResourceCondition.Degraded => "Degraded",
        ResourceCondition.Failed => "Failed",
        ResourceCondition.Unreachable => "Unreachable",
        _ => "Unknown",
    };

    private static void Visit(JsonElement element, ICollection<ResourceCandidate> candidates)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var instanceName = ReadString(element, "name", "resourceName");
            var displayName = ReadString(element, "displayName");
            var state = ReadString(element, "state", "resourceState", "status");
            var health = ReadString(element, "health", "healthStatus");
            var resourceType = ReadString(element, "resourceType", "type");
            var executionIdentity = ReadString(element, "startTimestamp", "creationTimestamp");
            var exitCode = ReadIntRecursive(element, "exitCode");
            if (exitCode is not null and not 0)
            {
                state = "Failed";
            }
            if (!string.IsNullOrWhiteSpace(instanceName)
                && (!string.IsNullOrWhiteSpace(state) || !string.IsNullOrWhiteSpace(health)))
            {
                candidates.Add(new ResourceCandidate(
                    instanceName,
                    string.IsNullOrWhiteSpace(displayName) ? instanceName : displayName,
                    resourceType,
                    state,
                    health,
                    ReadEndpoints(element),
                    executionIdentity,
                    ReadCommands(element)));
            }

            foreach (var property in element.EnumerateObject())
            {
                Visit(property.Value, candidates);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Visit(item, candidates);
            }
        }
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> ReadEndpoints(JsonElement element)
    {
        var endpoints = new List<string>();
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Contains("endpoint", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Contains("url", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Contains("address", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CollectUrls(property.Value, endpoints);
        }

        return endpoints;
    }

    private static int? ReadIntRecursive(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if ((string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                     || property.Name.EndsWith($".{name}", StringComparison.OrdinalIgnoreCase))
                    && property.Value.TryGetInt32(out var value))
                {
                    return value;
                }

                var nested = ReadIntRecursive(property.Value, name);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = ReadIntRecursive(item, name);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static IReadOnlySet<ResourceCommand> ReadCommands(JsonElement element)
    {
        var commands = new HashSet<ResourceCommand>();
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, "commands", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var command in property.Value.EnumerateObject())
            {
                if (Enum.TryParse<ResourceCommand>(command.Name, ignoreCase: true, out var parsed)
                    && !command.Value.ToString().Contains("Disabled", StringComparison.OrdinalIgnoreCase))
                {
                    commands.Add(parsed);
                }
            }
        }

        return commands;
    }

    private static IReadOnlySet<ResourceCommand> IntersectCommands(IReadOnlyList<ResourceCandidate> candidates)
    {
        var commands = Enum.GetValues<ResourceCommand>().ToHashSet();
        foreach (var candidate in candidates)
        {
            commands.IntersectWith(candidate.AllowedCommands);
        }

        return commands;
    }

    private static string? FindFirstUrl(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(property.Value.GetString(), UriKind.Absolute, out var uri))
                {
                    return uri.GetLeftPart(UriPartial.Authority);
                }

                var nested = FindFirstUrl(property.Value, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindFirstUrl(item, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static void CollectUrls(JsonElement element, ICollection<string> endpoints)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (Uri.TryCreate(value, UriKind.Absolute, out _))
                {
                    endpoints.Add(value!);
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectUrls(property.Value, endpoints);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectUrls(item, endpoints);
                }
                break;
        }
    }

    private sealed record ResourceCandidate(
        string InstanceName,
        string DisplayName,
        string ResourceType,
        string State,
        string Health,
        IReadOnlyList<string> Endpoints,
        string ExecutionIdentity,
        IReadOnlySet<ResourceCommand> AllowedCommands);
}
