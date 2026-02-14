namespace CoreBankDemo.CoreBankAPI;

public class Account
{
    public required string AccountNumber { get; set; }
    public required string AccountHolderName { get; set; }
    public decimal Balance { get; set; }
    public required string Currency { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
