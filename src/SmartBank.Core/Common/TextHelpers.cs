using System;
using System.Globalization;
using System.Linq;

namespace SmartBank.Core.Common
{
    /// <summary>How card expiry dates are written. "/" in a custom format is the *culture's* date separator ("." in tr-TR), so it is escaped.</summary>
    public static class CardFormat
    {
        public static string Expiry(DateTime date) => date.ToString("MM'/'yy", CultureInfo.InvariantCulture);

        public static string ExpiryIn(DateTime now, int years) => Expiry(now.AddYears(years));
    }

    /// <summary>Guesses whether a message is Turkish, to pick the language of a canned answer. One list, used everywhere.</summary>
    public static class TurkishText
    {
        private static readonly string[] Hints =
        {
            "merhaba", "selam", "nasıl", "nasil", "yardım", "yardim", "kredi", "hesap", "hesab", "para", "cek", "çek", "kart",
            "bakiye", "gönder", "gonder", "işlem", "islem", "destek", "bağla", "bagla", "istiyorum"
        };

        // Lower-case and drop the Turkish letters' marks, so "BAKİYE", "bakiye" and "bakıye" are the same word. (A capital dotted İ
        // does not lower-case to a plain i under the invariant culture.)
        private static string Fold(string text) =>
            text.ToLowerInvariant().Replace('İ', 'i').Replace("i̇", "i").Replace('ı', 'i').Replace('ç', 'c').Replace('ğ', 'g')
                .Replace('ö', 'o').Replace('ş', 's').Replace('ü', 'u');

        public static bool LooksTurkish(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var folded = Fold(text);
            return Hints.Any(h => folded.Contains(Fold(h), StringComparison.Ordinal));
        }
    }
}
