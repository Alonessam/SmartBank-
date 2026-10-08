using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SmartBank.Core.Common
{
    /// <summary>
    /// Reads what the AI model asked the server to do. The model's text is untrusted input: this class only extracts
    /// values from it; the server decides what to do with them (a transfer is merely proposed as a card the customer
    /// must confirm, and still goes through every normal check).
    /// </summary>
    public static class AiActions
    {
        public sealed record TransferProposal(string Source, string Destination, decimal Amount, string Description);

        private static readonly Regex SourcePattern = new(@"source\s*:\s*([^,\]]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex DestinationPattern = new(@"destination\s*:\s*([^,\]]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex AmountPattern = new(@"amount\s*:\s*([^,\]]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex DescriptionPattern = new(@"description\s*:\s*([^,\]]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public const int MaxDescriptionLength = 200;
        public const string DefaultDescription = "AI Support Transfer";

        public static bool WantsBalances(string? text) => text?.Contains("ACTION:GET_BALANCES", StringComparison.OrdinalIgnoreCase) == true;

        public static bool WantsTransfer(string? text) => text?.Contains("ACTION:TRANSFER", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>The transfer the model describes, or null when source, destination or amount are missing or not valid.</summary>
        public static TransferProposal? ParseTransfer(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var source = CleanValue(Group(SourcePattern, text));
            var destination = CleanValue(Group(DestinationPattern, text));
            var description = CleanValue(Group(DescriptionPattern, text));
            if (string.IsNullOrEmpty(description)) description = DefaultDescription;
            if (description.Length > MaxDescriptionLength) description = description[..MaxDescriptionLength];

            // The values end up inside the bracketed card the web app parses: no marker characters, and account-number sized.
            if (!IsPlainAccountText(source) || !IsPlainAccountText(destination)) return null;
            if (!TryParseAmount(CleanValue(Group(AmountPattern, text)), out var amount)) return null;

            return new TransferProposal(source, destination, amount, ChatMarkers.Neutralize(description));
        }

        /// <summary>
        /// A positive amount with at most two decimals, whatever way the number is written: "1500", "1500.50", "1,5", "1.500,50",
        /// "1,500.50", "TRY 250". One separator followed by exactly three digits counts as a thousands separator ("1,000" is a
        /// thousand). Always parsed with the invariant culture: the machine's own culture must never change a sum of money.
        /// </summary>
        public static bool TryParseAmount(string? text, out decimal amount)
        {
            amount = 0m;
            if (string.IsNullOrWhiteSpace(text)) return false;

            // A minus sign before a digit makes the amount negative: it must never be read as a positive one.
            if (Regex.IsMatch(text, @"-\s*\d")) return false;

            var s = Regex.Replace(text, @"[^\d.,]", string.Empty);
            if (s.Length == 0 || !char.IsDigit(s[0]) || !char.IsDigit(s[^1])) return false;

            var lastDot = s.LastIndexOf('.');
            var lastComma = s.LastIndexOf(',');

            if (lastDot >= 0 && lastComma >= 0)
            {
                // Both appear: the one that comes last is the decimal separator, the other groups thousands.
                var decimalSeparator = lastDot > lastComma ? '.' : ',';
                var groupSeparator = decimalSeparator == '.' ? ',' : '.';
                s = s.Replace(groupSeparator.ToString(), string.Empty).Replace(decimalSeparator, '.');
            }
            else if (lastDot >= 0 || lastComma >= 0)
            {
                var separator = lastDot >= 0 ? '.' : ',';
                var count = s.Count(c => c == separator);
                var digitsAfter = s.Length - s.LastIndexOf(separator) - 1;

                // A zero in front ("0,500") is half a lira written with three decimals, not five hundred.
                if (count > 1 || (digitsAfter == 3 && s[0] != '0'))
                {
                    s = s.Replace(separator.ToString(), string.Empty); // grouping
                }
                else
                {
                    s = s.Replace(separator, '.');
                }
            }

            if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return false;
            if (value <= 0m || value > Money.MaxAmount || !Money.HasValidScale(value)) return false;

            amount = value;
            return true;
        }

        private static readonly Regex PlainAccountText = new(@"^[^\[\]=:,;\r\n]{1,30}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static bool IsPlainAccountText(string value) => PlainAccountText.IsMatch(value);

        private static string Group(Regex pattern, string text)
        {
            var match = pattern.Match(text);
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private static string CleanValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            value = value.Trim().TrimEnd(',', ';', ']', '}');
            value = value.Trim('"', '\'', '{', '}');
            return value.Trim();
        }
    }
}
