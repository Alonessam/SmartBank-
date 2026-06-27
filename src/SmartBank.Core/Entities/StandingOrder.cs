using System;

namespace SmartBank.Core.Entities
{
    public class StandingOrder
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public string SourceAccountNumber { get; set; } = string.Empty;
        public string? DestinationAccountNumber { get; set; } // Nullable for credit card auto-payments
        public decimal? Amount { get; set; } // Null for full credit card debt auto-payments
        public string Frequency { get; set; } = "Monthly"; // Daily, Weekly, Monthly
        public DateTime MaturityDate { get; set; } = DateTime.UtcNow.AddYears(1); // Valid up to 1 year
        public DateTime NextExecutionDate { get; set; } = DateTime.UtcNow;
        public bool IsActive { get; set; } = true;
        public string OrderType { get; set; } = "Transfer"; // Transfer or CreditCardAutoPay
        public Guid? CreditCardId { get; set; } // Nullable, if auto-payment is for a credit card
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        public User? User { get; set; }
        public CreditCard? CreditCard { get; set; }
    }
}
