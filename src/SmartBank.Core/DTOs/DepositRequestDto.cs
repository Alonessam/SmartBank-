using System.ComponentModel.DataAnnotations;
using SmartBank.Core.Common;

namespace SmartBank.Core.DTOs
{
    public class DepositRequestDto
    {
        [Required(ErrorMessage = "Account number is required.")]
        [StringLength(30, ErrorMessage = "Account number cannot exceed 30 characters.")]
        public string AccountNumber { get; set; } = string.Empty;

        [Required(ErrorMessage = "Amount is required.")]
        [Range(0.01, 10000000.00, ErrorMessage = "Amount must be between 0.01 and 10,000,000.00.")]
        [MoneyScale]
        public decimal Amount { get; set; }
    }
}
