namespace CoreBankDemo.PaymentsAPI.Accounts;

/// <summary>
/// The inbox side of the account projection (spec: payments-account-projection).
/// Every member locks the account row, applies one change and saves it; the
/// caller owns the surrounding transaction (<c>ExecuteInTransactionAsync</c>),
/// so a save here is only durable once the inbox row's completion commits
/// with it. Narrow so <c>TransactionEventHandler</c> stays unit-testable
/// through a mock.
/// </summary>
internal interface IAccountProjectionStore
{
    /// <summary>Records CoreBank's reported balance for the account; never touches the reservation.</summary>
    Task SettleAsync(string accountNumber, decimal newBalance, string currency, CancellationToken cancellationToken);

    /// <summary>
    /// Releases <paramref name="amount"/> of the account's reservation, clamping
    /// at zero.
    /// </summary>
    /// <returns>
    /// The part of <paramref name="amount"/> that could not be released
    /// because the reservation was already smaller (<c>0</c> normally).
    /// </returns>
    Task<decimal> ReleaseAsync(string accountNumber, decimal amount, CancellationToken cancellationToken);
}
