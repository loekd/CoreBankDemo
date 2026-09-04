using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Application.Ports;

public interface ILoadWorkflowRunner
{
    Task<LoadWorkflowResult> RunAsync(
        int? expectedUniqueCount,
        IProgress<LoadWorkflowProgress> progress,
        CancellationToken ct);
}
