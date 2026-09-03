using System.Text.Json;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;

namespace CoreBankDemo.DemoRunner.Infrastructure;

public sealed class SessionEvidenceExporter(string repositoryRoot, TimeProvider time) : IEvidenceExporter
{
    public async Task<EvidenceExportResult> ExportAsync(
        IReadOnlyList<EvidenceRecord> records,
        CancellationToken ct)
    {
        var directory = Path.Combine(repositoryRoot, ".demo-runner-exports");
        var path = Path.Combine(directory, $"evidence-{time.GetUtcNow():yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json");
        try
        {
            Directory.CreateDirectory(directory);
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(
                stream,
                records,
                new JsonSerializerOptions { WriteIndented = true },
                ct);
            return new EvidenceExportResult(true, path, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EvidenceExportResult(false, path, ex.Message);
        }
    }
}
