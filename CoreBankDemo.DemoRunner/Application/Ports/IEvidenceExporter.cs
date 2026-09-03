using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Application.Ports;

public sealed record EvidenceExportResult(bool Succeeded, string Path, string? ErrorSummary);

public interface IEvidenceExporter
{
    Task<EvidenceExportResult> ExportAsync(
        IReadOnlyList<EvidenceRecord> records,
        CancellationToken ct);
}
