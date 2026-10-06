using System;
using System.ComponentModel.DataAnnotations;
using SmartBank.Core.Common;

namespace SmartBank.Core.DTOs
{
    public class StandingOrderDto
    {
        public Guid Id { get; set; }
        public string SourceAccountNumber { get; set; } = string.Empty;
        public string? DestinationAccountNumber { get; set; }
        public decimal? Amount { get; set; }
        public string Frequency { get; set; } = Frequencies.Monthly; // Daily, Weekly, Monthly
        public DateTime MaturityDate { get; set; }
        public DateTime NextExecutionDate { get; set; }
        public bool IsActive { get; set; }
        public string OrderType { get; set; } = OrderTypes.Transfer; // Transfer or CreditCardAutoPay
        public Guid? CreditCardId { get; set; }
        public string? CreditCardLast4 { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class CreateStandingOrderDto
    {
        [Required]
        [StringLength(30)]
        public string SourceAccountNumber { get; set; } = string.Empty;

        /// <summary>Required for a Transfer order; ignored for a credit-card auto-pay.</summary>
        [StringLength(30)]
        public string? DestinationAccountNumber { get; set; }

        /// <summary>Required for a Transfer order (at most 1,000,000); a credit-card auto-pay always pays the whole statement.</summary>
        [Range(0.01, 1000000.00, ErrorMessage = "Amount must be between 0.01 and 1,000,000.00.")]
        [MoneyScale]
        public decimal? Amount { get; set; }

        [Required]
        [StringLength(20)]
        public string Frequency { get; set; } = Frequencies.Monthly; // Daily, Weekly, Monthly

        [Required]
        [StringLength(20)]
        public string OrderType { get; set; } = OrderTypes.Transfer; // Transfer or CreditCardAutoPay

        public Guid? CreditCardId { get; set; }
    }
}
