using System;
using System.ComponentModel.DataAnnotations;

namespace SmartBank.Core.DTOs
{
    public class PayCreditCardDebtDto
    {
        [Required]
        public string SourceAccountNumber { get; set; } = string.Empty;

        [Required]
        [Range(0.01, double.MaxValue, ErrorMessage = "Amount must be greater than zero.")]
        public decimal Amount { get; set; }
    }
}
