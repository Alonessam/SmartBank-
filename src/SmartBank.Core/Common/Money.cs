using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace SmartBank.Core.Common
{
    /// <summary>
    /// The one place that decides how money amounts are validated and rounded. Balances are decimal(18,2), so an amount
    /// with more than two decimals would be silently rounded by the database and could debit and credit different values.
    /// Amounts are therefore rejected, and computed values (exchange cost, conversion, interest) are rounded explicitly.
    /// </summary>
    public static class Money
    {
        public const int Decimals = 2;

        /// <summary>Largest single transfer, deposit or exchange. Matches the [Range] on the request DTOs.</summary>
        public const decimal MaxAmount = 10_000_000m;

        /// <summary>Largest amount of one standing order execution.</summary>
        public const decimal MaxStandingOrderAmount = 1_000_000m;

        public const string ScaleErrorKey = "InvalidAmountScale";
        public const string ScaleMessage = "Amount can have at most 2 decimal places.";

        /// <summary>True when the value has at most two decimals (1, 1.5 and 1.50 are fine; 1.505 is not).</summary>
        public static bool HasValidScale(decimal amount) => decimal.Round(amount, Decimals, MidpointRounding.AwayFromZero) == amount;

        /// <summary>The rounding used for every computed amount.</summary>
        public static decimal Round(decimal amount) => decimal.Round(amount, Decimals, MidpointRounding.AwayFromZero);

        /// <summary>Two decimals, rounded toward zero: for an amount the bank credits after a conversion (never in the customer's favour by rounding).</summary>
        public static decimal Truncate(decimal amount) => decimal.Round(amount, Decimals, MidpointRounding.ToZero);

        /// <summary>Two decimals, dot separator, whatever the machine culture: for audit text, descriptions and OTP bindings.</summary>
        public static string Format(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>[MoneyScale]: a money amount must not have more than two decimals.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Field)]
    public sealed class MoneyScaleAttribute : ValidationAttribute
    {
        public MoneyScaleAttribute() : base(Money.ScaleMessage) { }

        public override bool IsValid(object? value) => value switch
        {
            null => true,
            decimal d => Money.HasValidScale(d),
            _ => true
        };
    }
}
