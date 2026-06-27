using System;

namespace SmartBank.Core.DTOs
{
    public class CreditCardTransactionDto
    {
        public Guid Id { get; set; }
        public string Description { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
