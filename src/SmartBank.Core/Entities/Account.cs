using System;
using System.Collections.Generic;

namespace SmartBank.Core.Entities
{
    public class Account
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public string AccountNumber { get; set; } = string.Empty;
        public string AccountCode { get; set; } = string.Empty;
        public decimal Balance { get; set; } = 0.00m;
        public string Currency { get; set; } = "TRY"; // Default TRY
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Debit Card encrypted details (Phase 3 AES)
        public string EncryptedCardNumber { get; set; } = string.Empty;
        public string EncryptedCardCvv { get; set; } = string.Empty;
        public string CardTheme { get; set; } = "theme-neon-blue";
        public string ExpiryDate { get; set; } = DateTime.UtcNow.AddYears(5).ToString("MM/yy");

        // Account Type and Vadeli details
        public string AccountType { get; set; } = "DemandDeposit"; // DemandDeposit or TimeDeposit
        public decimal? InterestRate { get; set; }
        public DateTime? MaturityDate { get; set; }

        // Navigation Properties
        public User? User { get; set; }
        public ICollection<Transaction> SentTransactions { get; set; } = new List<Transaction>();
        public ICollection<Transaction> ReceivedTransactions { get; set; } = new List<Transaction>();
    }
}
