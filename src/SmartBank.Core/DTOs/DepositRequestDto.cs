using System.ComponentModel.DataAnnotations;

namespace SmartBank.Core.DTOs
{
    public class DepositRequestDto
    {
        [Required(ErrorMessage = "Account number is required.")]
        public string AccountNumber { get; set; } = string.Empty;

        [Required(ErrorMessage = "Amount is required.")]
        [Range(0.01, 10000000.00, ErrorMessage = "Amount must be between 0.01 and 10,000,000.00.")]
        public decimal Amount { get; set; }
    }
}
