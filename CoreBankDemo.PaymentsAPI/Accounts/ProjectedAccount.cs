namespace CoreBankDemo.PaymentsAPI.Accounts;

/// <summary>
/// PaymentsAPI's local view of one account (spec: payments-account-projection,
/// ADR-028), built only from the payments it accepts and the
/// <c>transaction-events</c> CoreBank publishes -- never seeded, never read
/// from CoreBank. <see cref="SettledBalance"/> is the last <c>NewBalance</c>
/// CoreBank reported (<see langword="null"/> until the first
/// <c>balance.updated</c>); <see cref="Reserved"/> is the sum of accepted
/// debits whose outcome PaymentsAPI has not yet seen. Available funds are
/// <c>SettledBalance - Reserved</c>, defined only once the former is known.
/// </summary>
public class ProjectedAccount
{
    public required string AccountNumber { get; set; }

    public decimal? SettledBalance { get; set; }

    /// <summary>Never negative; a release that would overshoot clamps to zero.</summary>
    public decimal Reserved { get; set; }

    public string? Currency { get; set; }

    public DateTime UpdatedAt { get; set; }
}
