namespace SmartBank.Core.DTOs
{
    public class MarketRateDto
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string NameEn { get; set; } = string.Empty;
        public decimal Buy { get; set; }
        public decimal Sell { get; set; }
        public decimal Change { get; set; }

        /// <summary>True when this price is a stand-in (the live feed was unavailable or returned nonsense). Never trade against it.</summary>
        public bool IsFallback { get; set; }
    }
}
