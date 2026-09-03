namespace CoreBankDemo.PaymentsAPI.Models;

/// <summary>
/// The closed, additive `scheme` value set on <c>POST /api/payments</c>
/// (spec: add-instant-payment-rail). Absent or <see cref="Standard"/>
/// reproduces today's store-and-forward behaviour exactly; <see cref="Instant"/>
/// opts into the budgeted inline forward. No magic strings elsewhere --
/// every comparison against the wire value goes through these constants.
/// </summary>
public static class PaymentSchemes
{
    public const string Standard = "standard";
    public const string Instant = "instant";
}
