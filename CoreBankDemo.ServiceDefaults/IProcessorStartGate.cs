namespace CoreBankDemo.ServiceDefaults;

public interface IProcessorStartGate
{
    Task WaitAsync(CancellationToken cancellationToken = default);
}

public interface IProcessorStartGatePublisher
{
    Task<bool> HasReleaseGenerationAsync(CancellationToken cancellationToken = default);

    Task ReleaseAsync(CancellationToken cancellationToken = default);
}
