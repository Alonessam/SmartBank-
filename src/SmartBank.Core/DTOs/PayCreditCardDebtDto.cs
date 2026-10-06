using System;
using System.ComponentModel.DataAnnotations;
using SmartBank.Core.Common;

namespace SmartBank.Core.DTOs
{
    public class PayCreditCardDebtDto
    {
        [Required]
        [StringLength(30)]
        public string SourceAccountNumber { get; set; } = string.Empty;

        [Range(0.01, 10000000.00, ErrorMessage = "Amount must be between 0.01 and 10,000,000.00.")]
        [MoneyScale]
        public decimal Amount { get; set; }
    }
}
