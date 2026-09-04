using System.Text.Json;

namespace CoreBankDemo.DemoRunner.Infrastructure;

public sealed record RunningAppHost(string Path, int ProcessId);

public static class AspireProcessJsonParser
{
    public static IReadOnlyList<RunningAppHost> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("aspire ps returned JSON whose root is not an array.");
            }

            var results = new List<RunningAppHost>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("appHostPath", out var path)
                    || path.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("appHostPid", out var pid)
                    || !pid.TryGetInt32(out var processId)
                    || processId <= 0
                    || string.IsNullOrWhiteSpace(path.GetString()))
                {
                    throw new InvalidOperationException("aspire ps returned an AppHost entry without a valid path and PID.");
                }

                results.Add(new RunningAppHost(path.GetString()!, processId));
            }

            return results;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("aspire ps returned malformed JSON.", ex);
        }
    }

    public static IReadOnlyList<string> ParsePaths(string json) => Parse(json).Select(item => item.Path).ToList();
}
