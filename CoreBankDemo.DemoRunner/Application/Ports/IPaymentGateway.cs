using CoreBankDemo.DemoRunner.Application;

namespace CoreBankDemo.DemoRunner.Application.Ports;

public interface IPaymentGateway
{
    Task<PaymentResult> SubmitAsync(
        TopologyProfile profile,
        PaymentSubmission submission,
        CancellationToken ct);

    /// <summary>
    /// Asks CoreBank to withdraw a payment before it executes, through its own
    /// <c>POST /api/transactions/cancel</c>. The body carries the business meaning, never the
    /// status code alone.
    /// </summary>
    Task<PaymentCancellationResult> CancelAsync(
        TopologyProfile profile,
        PaymentCancellation cancellation,
        CancellationToken ct);

    Task<InspectionResult> QueryOutcomeAsync(
        TopologyProfile profile,
        string transactionIdOrKey,
        CancellationToken ct);

    /// <summary>
    /// Reads a payment's status from PaymentsAPI (ADR-027). Read-only; the answer is never proof
    /// of an outcome on the card.
    /// </summary>
    Task<InspectionResult> FetchPaymentStatusAsync(
        TopologyProfile profile,
        string transactionId,
        CancellationToken ct);

    Task<InspectionResult> InspectAsync(
        TopologyProfile profile,
        string endpointId,
        CancellationToken ct);
}
