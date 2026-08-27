using System.ComponentModel.DataAnnotations;

namespace CoreBankDemo.CoreBankAPI.Models;

public record AccountValidationRequest(
    [Required(ErrorMessage = "AccountNumber is required")]
    [StringLength(34, MinimumLength = 15, ErrorMessage = "AccountNumber must be between 15 and 34 characters")]
    string AccountNumber
);
