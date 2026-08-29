using System.Text.Json;

namespace CoreBankDemo.DemoRunner.Application.StateMachine;

/// <summary>Reads a small dotted JSON path such as <c>$.transactionId</c> or <c>$.data.id</c> out of a JSON document.</summary>
public static class JsonPathReader
{
    public static string? TryRead(string? json, string? path)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var element = document.RootElement;
            var segments = path.TrimStart('$', '.').Split('.', StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                if (element.ValueKind != JsonValueKind.Object || !TryGetPropertyIgnoreCase(element, segment, out var next))
                {
                    return null;
                }

                element = next;
            }

            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
                _ => element.GetRawText(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
