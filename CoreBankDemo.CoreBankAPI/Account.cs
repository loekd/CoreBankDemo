namespace CoreBankDemo.CoreBankAPI;

/// <summary>
/// Plain ledger account entity (no kernel interface — accounts are not a
/// messaging concept). Row-level locking (<c>FOR UPDATE</c>) and the
/// repository that owns it belong to story 4.3, not this one.
/// </summary>
public class Account
{
    public required string AccountNumber { get; set; }
    public required string AccountHolderName { get; set; }
    public decimal Balance { get; set; }
    public required string Currency { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
