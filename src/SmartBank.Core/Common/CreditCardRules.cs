using System;

namespace SmartBank.Core.Common
{
    /// <summary>
    /// The numbers behind the simulated credit card: limit, interest, minimum payment and statement dates. They used to
    /// be literals scattered through BankingService. The values are unchanged.
    /// </summary>
    public static class CreditCardRules
    {
        /// <summary>Limit of a newly issued card.</summary>
        public const decimal DefaultLimit = 10_000m;

        /// <summary>Interest per statement period on the part of the statement that is still unpaid (4.25 %).</summary>
        public const decimal MonthlyInterestRate = 0.0425m;

        /// <summary>Late fee on the part of the minimum payment that was not paid on time (5 %).</summary>
        public const decimal LateInterestRate = 0.05m;

        /// <summary>The minimum payment is this share of the statement debt (30 %).</summary>
        public const decimal MinimumPaymentRate = 0.30m;

        /// <summary>Days from card issue to the first statement cut-off.</summary>
        public const int FirstStatementDays = 30;

        /// <summary>Days between a statement cut-off and its due date.</summary>
        public const int DueDaysAfterCutoff = 10;

        public const int IssuedCardValidityYears = 8;
        public const int DefaultCardValidityYears = 5;

        /// <summary>How many statements one call returns, newest first.</summary>
        public const int MaxStatementsReturned = 24;

        /// <summary>Upper bound for the card transactions read for those statements.</summary>
        public const int MaxStatementTransactions = 2000;

        // Turkish month names, fixed here so the result never depends on the machine's culture data (ICU).
        private static readonly string[] TurkishMonths =
        {
            "Ocak", "Şubat", "Mart", "Nisan", "Mayıs", "Haziran", "Temmuz", "Ağustos", "Eylül", "Ekim", "Kasım", "Aralık"
        };

        /// <summary>
        /// "Ekim 2026" for the statement that closes on <paramref name="cutoff"/>: the month in which its period began.
        /// Computed from the date, so no month name has to be parsed back to find the next one.
        /// </summary>
        public static string PeriodName(DateTime cutoff)
        {
            var start = cutoff.AddMonths(-1);
            return TurkishMonths[start.Month - 1] + " " + start.Year;
        }

        public static decimal MinimumPayment(decimal debt) => Money.Round(debt * MinimumPaymentRate);

        /// <summary>
        /// Interest charged when a statement is closed with an unpaid rest: the normal rate on what is unpaid, plus a late
        /// fee on the unpaid part of the minimum payment (the minimum is already part of the unpaid amount).
        /// </summary>
        public static decimal Interest(decimal periodDebt, decimal paid, decimal minimumPayment)
        {
            var unpaid = periodDebt - paid;
            if (unpaid <= 0m) return 0m;

            if (paid >= minimumPayment)
            {
                return Money.Round(unpaid * MonthlyInterestRate);
            }

            var unpaidMinimum = minimumPayment - paid;
            return Money.Round(unpaidMinimum * LateInterestRate + (unpaid - unpaidMinimum) * MonthlyInterestRate);
        }
    }
}
