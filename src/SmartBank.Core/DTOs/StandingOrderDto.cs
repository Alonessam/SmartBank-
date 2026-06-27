using System;
using System.ComponentModel.DataAnnotations;

namespace SmartBank.Core.DTOs
{
    public class StandingOrderDto
    {
        public Guid Id { get; set; }
        public string SourceAccountNumber { get; set; } = string.Empty;
        public string? DestinationAccountNumber { get; set; }
        public decimal? Amount { get; set; }
        public string Frequency { get; set; } = "Monthly"; // Daily, Weekly, Monthly
        public DateTime MaturityDate { get; set; }
        public DateTime NextExecutionDate { get; set; }
        public bool IsActive { get; set; }
        public string OrderType { get; set; } = "Transfer"; // Transfer or CreditCardAutoPay
        public Guid? CreditCardId { get; set; }
        public string? CreditCardNumber { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class CreateStandingOrderDto
    {
        [Required]
        public string SourceAccountNumber { get; set; } = string.Empty;

        public string? DestinationAccountNumber { get; set; }

        [Range(0.01, double.MaxValue, ErrorMessage = "Amount must be positive.")]
        public decimal? Amount { get; set; }

        [Required]
        public string Frequency { get; set; } = "Monthly"; // Daily, Weekly, Monthly

        [Required]
        public string OrderType { get; set; } = "Transfer"; // Transfer or CreditCardAutoPay

        public Guid? CreditCardId { get; set; }
    }
}
