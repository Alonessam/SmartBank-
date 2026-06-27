using System;

namespace SmartBank.Core.Entities
{
    public class CreditCardTransaction
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid CreditCardId { get; set; }
        public string Description { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation Properties
        public CreditCard? CreditCard { get; set; }
    }
}
