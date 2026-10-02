using System;

namespace SmartBank.Core.DTOs
{
    public class CreditCardDto
    {
        public Guid Id { get; set; }
        public string CardNumber { get; set; } = string.Empty;
        /// <summary>Only filled in the response that issues the card. The CVV is never stored, so later reads leave it empty.</summary>
        public string CardCvv { get; set; } = string.Empty;
        public string ExpiryDate { get; set; } = string.Empty;
        public decimal CardLimit { get; set; }
        public decimal CurrentDebt { get; set; }
        public decimal AvailableLimit { get; set; }
        public string CardTheme { get; set; } = string.Empty;
    }
}
