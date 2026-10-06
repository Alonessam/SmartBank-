using System;
using System.ComponentModel.DataAnnotations;
using SmartBank.Core.Common;

namespace SmartBank.Core.DTOs
{
    public class ExchangeDto
    {
        [Required]
        [StringLength(40)]
        public string SourceAccountId { get; set; } = string.Empty;

        /// <summary>USD, EUR, XAU or XAG (checked by the service against the allow-list).</summary>
        [Required]
        [StringLength(10)]
        public string Asset { get; set; } = string.Empty;

        /// <summary>"buy" or "sell" (checked by the service).</summary>
        [Required]
        [StringLength(10)]
        public string Action { get; set; } = string.Empty;

        [Range(0.01, 10000000.00, ErrorMessage = "Amount must be between 0.01 and 10,000,000.00.")]
        [MoneyScale]
        public decimal Amount { get; set; }
    }
}
