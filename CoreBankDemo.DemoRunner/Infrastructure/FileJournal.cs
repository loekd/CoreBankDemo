using System.Text.Json;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Application.StateMachine;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>
/// Append-only, gitignored JSON-lines journal. Every entry is bounded and redacted
/// (<see cref="JournalRedaction"/>) before it touches disk — never secrets, never
/// unbounded raw response bodies (ADR-015).
/// </summary>
public sealed class FileJournal(string artifactsDirectory) : IJournal
{
    private string FilePath => Path.Combine(artifactsDirectory, "session-journal.jsonl");

    public async Task AppendAsync(JournalEntry entry, CancellationToken ct)
    {
        Directory.CreateDirectory(artifactsDirectory);
        var redacted = entry with { EvidenceSummary = JournalRedaction.Apply(entry.EvidenceSummary) };
        var json = JsonSerializer.Serialize(redacted);
        await File.AppendAllTextAsync(FilePath, json + Environment.NewLine, ct);
    }

    public async Task<JournalEntry?> TryReadLastCheckpointAsync(string session, CancellationToken ct)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        JournalEntry? lastPassed = null;
        JournalEntry? last = null;
        foreach (var line in await File.ReadAllLinesAsync(FilePath, ct))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<JournalEntry>(line);
            if (entry is null || entry.Session != session)
            {
                continue;
            }

            last = entry;
            if (entry.State == CueStatus.Passed)
            {
                lastPassed = entry;
            }
        }

        // An interrupted Running entry recovers as Ambiguous, never as Passed from the
        // journal alone; otherwise surface the most recent proven checkpoint.
        return last is { State: CueStatus.Running } ? last : lastPassed ?? last;
    }
}
