using System.Text.Json;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>
/// Stores the latest-known-good rehearsal proof pack as a single JSON file in the local
/// artifacts directory. A new proof pack only replaces the file after the caller has
/// already established the full rehearsal (every cue, all five invariants, cleanup)
/// passed — this store itself does not gate that decision (ADR-015).
/// </summary>
public sealed class FileProofPackStore(string artifactsDirectory) : IProofPackStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private string FilePath => Path.Combine(artifactsDirectory, "latest-known-good-proof-pack.json");

    public async Task SaveAsLatestKnownGoodAsync(ProofPack proofPack, CancellationToken ct)
    {
        Directory.CreateDirectory(artifactsDirectory);
        var json = JsonSerializer.Serialize(proofPack, Options);

        // Write-then-move keeps the previous known-good pack intact if the write is interrupted.
        var tempPath = FilePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, FilePath, overwrite: true);
    }

    public async Task<ProofPack?> TryGetLatestKnownGoodAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(FilePath, ct);
        return JsonSerializer.Deserialize<ProofPack>(json, Options);
    }
}
