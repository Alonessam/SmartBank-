using System;
using System.Collections.Generic;

namespace SmartBank.Core.Entities
{
    public class CreditCard
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public string EncryptedCardNumber { get; set; } = string.Empty;
        public string EncryptedCardCvv { get; set; } = string.Empty;
        public string ExpiryDate { get; set; } = DateTime.UtcNow.AddYears(5).ToString("MM/yy");
        public decimal CardLimit { get; set; }
        public decimal CurrentDebt { get; set; }
        public string CardTheme { get; set; } = "theme-metallic-dark";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation Properties
        public User? User { get; set; }
        public ICollection<CreditCardStatement> Statements { get; set; } = new List<CreditCardStatement>();
        public ICollection<CreditCardTransaction> Transactions { get; set; } = new List<CreditCardTransaction>();
    }
}
