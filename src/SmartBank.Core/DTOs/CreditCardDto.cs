using System;

namespace SmartBank.Core.DTOs
{
    public class CreditCardDto
    {
        public Guid Id { get; set; }
        public string CardNumber { get; set; } = string.Empty;
        public string CardCvv { get; set; } = string.Empty;
        public string ExpiryDate { get; set; } = string.Empty;
        public decimal CardLimit { get; set; }
        public decimal CurrentDebt { get; set; }
        public decimal AvailableLimit { get; set; }
        public string CardTheme { get; set; } = string.Empty;
    }
}
