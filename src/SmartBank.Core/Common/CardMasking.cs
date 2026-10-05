using System.Linq;

namespace SmartBank.Core.Common
{
    public static class CardMasking
    {
        /// <summary>Last four digits of a card number, or "****" when there are fewer than four.</summary>
        public static string LastFour(string? cardNumber)
        {
            var digits = new string((cardNumber ?? string.Empty).Where(char.IsDigit).ToArray());
            return digits.Length >= 4 ? digits[^4..] : "****";
        }
    }
}
