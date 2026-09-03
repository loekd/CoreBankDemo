using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace CoreBankDemo.PaymentsAPI.Models;

public record PaymentRequest(
    [Required(ErrorMessage = "FromAccount is required")]
    [StringLength(34, MinimumLength = 15, ErrorMessage = "FromAccount must be between 15 and 34 characters")]
    [DefaultValue("NL91ABNA0417164300")]
    string FromAccount,

    [Required(ErrorMessage = "ToAccount is required")]
    [StringLength(34, MinimumLength = 15, ErrorMessage = "ToAccount must be between 15 and 34 characters")]
    [DefaultValue("NL20INGB0001234567")]
    string ToAccount,

    [Required(ErrorMessage = "Amount is required")]
    [Range(0.01, 1000000, ErrorMessage = "Amount must be between 0.01 and 1,000,000")]
    [DefaultValue(1.00)]
    decimal Amount,

    [Required(ErrorMessage = "Currency is required")]
    [StringLength(3, MinimumLength = 3, ErrorMessage = "Currency must be exactly 3 characters")]
    [RegularExpression(@"^[A-Z]{3}$", ErrorMessage = "Currency must be 3 uppercase letters")]
    [DefaultValue("EUR")]
    string Currency,

    /// <summary>
    /// Payment rail (spec: add-instant-payment-rail). Optional, closed set of
    /// <see cref="PaymentSchemes.Standard"/> (default) and
    /// <see cref="PaymentSchemes.Instant"/>. Absent or <c>standard</c>
    /// reproduces today's store-and-forward behaviour byte-identically; an
    /// unrecognized value fails validation with a <c>400</c>, never silently
    /// falling back to <c>standard</c>.
    /// </summary>
    [AllowedValues(PaymentSchemes.Standard, PaymentSchemes.Instant, ErrorMessage = "Scheme must be 'standard' or 'instant'")]
    [DefaultValue(PaymentSchemes.Standard)]
    string Scheme = PaymentSchemes.Standard
);
