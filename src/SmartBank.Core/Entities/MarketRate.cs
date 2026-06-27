using System;

namespace SmartBank.Core.Entities
{
    public class MarketRate
    {
        public int Id { get; set; }
        public string Code { get; set; } = string.Empty; // e.g., "USD", "GOLD"
        public decimal Buy { get; set; }
        public decimal Sell { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
