using System.ComponentModel.DataAnnotations;

namespace CoreBankDemo.CoreBankAPI.Models;

public record TransactionRequest(
    [Required(ErrorMessage = "FromAccount is required")]
    [StringLength(34, MinimumLength = 15, ErrorMessage = "FromAccount must be between 15 and 34 characters")]
    string FromAccount,

    [Required(ErrorMessage = "ToAccount is required")]
    [StringLength(34, MinimumLength = 15, ErrorMessage = "ToAccount must be between 15 and 34 characters")]
    string ToAccount,

    [Required(ErrorMessage = "Amount is required")]
    [Range(0.01, 1000000, ErrorMessage = "Amount must be between 0.01 and 1,000,000")]
    decimal Amount,

    [Required(ErrorMessage = "Currency is required")]
    [StringLength(3, MinimumLength = 3, ErrorMessage = "Currency must be exactly 3 characters")]
    [RegularExpression(@"^[A-Z]{3}$", ErrorMessage = "Currency must be 3 uppercase letters")]
    string Currency,

    [Required(ErrorMessage = "TransactionId is required")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "TransactionId must be between 1 and 100 characters")]
    string TransactionId
);
