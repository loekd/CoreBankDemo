using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Application.Ports;

public interface IPaymentGateway
{
    Task<PaymentResult> SubmitAsync(
        TopologyProfile profile,
        PaymentSubmission submission,
        CancellationToken ct);

    Task<InspectionResult> QueryOutcomeAsync(
        TopologyProfile profile,
        string transactionIdOrKey,
        CancellationToken ct);

    Task<InspectionResult> InspectAsync(
        TopologyProfile profile,
        string endpointId,
        CancellationToken ct);
}
