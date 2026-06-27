using System;

namespace SmartBank.Core.DTOs
{
    public class AccountDto
    {
        public Guid Id { get; set; }
        public string AccountNumber { get; set; } = string.Empty;
        public string AccountCode { get; set; } = string.Empty;
        public decimal Balance { get; set; }
        public string Currency { get; set; } = "TRY";
        public DateTime CreatedAt { get; set; }

        public string CardNumber { get; set; } = string.Empty;
        public string CardCvv { get; set; } = string.Empty;
        public string CardTheme { get; set; } = "theme-neon-blue";
        public string ExpiryDate { get; set; } = string.Empty;
        public string AccountType { get; set; } = "DemandDeposit";
        public decimal? InterestRate { get; set; }
        public DateTime? MaturityDate { get; set; }
    }
}
