using System;
using System.ComponentModel.DataAnnotations;

namespace SmartBank.Core.DTOs
{
    public class ExchangeDto
    {
        [Required]
        public string SourceAccountId { get; set; } = string.Empty;

        [Required]
        public string Asset { get; set; } = string.Empty;

        [Required]
        public string Action { get; set; } = string.Empty; // buy or sell

        [Required]
        [Range(0.0001, double.MaxValue, ErrorMessage = "Amount must be greater than zero.")]
        public decimal Amount { get; set; }
    }
}
