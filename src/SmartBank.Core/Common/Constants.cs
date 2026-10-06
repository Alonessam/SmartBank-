using System;
using System.Collections.Generic;
using System.Linq;

namespace SmartBank.Core.Common
{
    /// <summary>Currencies and metals an account can hold. The stored values are the codes below, always upper-case.</summary>
    public static class Currencies
    {
        public const string Try = "TRY";
        public const string Usd = "USD";
        public const string Eur = "EUR";
        public const string Gold = "XAU";   // one gram
        public const string Silver = "XAG"; // one gram

        /// <summary>Everything an account may be opened in.</summary>
        public static readonly IReadOnlyList<string> All = new[] { Try, Usd, Eur, Gold, Silver };

        /// <summary>What can be bought or sold against TRY (the market-rate feed prices exactly these).</summary>
        public static readonly IReadOnlyList<string> Tradable = new[] { Usd, Eur, Gold, Silver };

        /// <summary>Upper-cases (culture-independent) and trims; null or blank gives null.</summary>
        public static string? Normalize(string? code)
        {
            var trimmed = code?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToUpperInvariant();
        }

        public static bool IsSupported(string? code) => code != null && All.Contains(code, StringComparer.Ordinal);

        public static bool IsTradable(string? code) => code != null && Tradable.Contains(code, StringComparer.Ordinal);
    }

    public static class AccountTypes
    {
        public const string DemandDeposit = "DemandDeposit";

        // Interest on a time deposit is a display value only: nothing accrues it and nothing enforces the maturity date.
        public const string TimeDeposit = "TimeDeposit";

        public static readonly IReadOnlyList<string> All = new[] { DemandDeposit, TimeDeposit };

        /// <summary>The canonical spelling of an account type, or null when it is not one we offer. Blank means demand deposit.</summary>
        public static string? Normalize(string? type)
        {
            var trimmed = type?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return DemandDeposit;
            return All.FirstOrDefault(t => t.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Standing-order kinds as they are stored in the database.</summary>
    public static class OrderTypes
    {
        public const string Transfer = "Transfer";
        public const string CreditCardAutoPay = "CreditCardAutoPay";

        /// <summary>The name the worker used to look for before v1.1. Old rows may still carry it.</summary>
        public const string LegacyCreditCardDebt = "CreditCardDebt";

        public static bool IsCreditCard(string? type) => type is CreditCardAutoPay or LegacyCreditCardDebt;

        public static string? Normalize(string? type)
        {
            var trimmed = type?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (trimmed.Equals(Transfer, StringComparison.OrdinalIgnoreCase)) return Transfer;
            if (trimmed.Equals(CreditCardAutoPay, StringComparison.OrdinalIgnoreCase)) return CreditCardAutoPay;
            return null;
        }
    }

    public static class Frequencies
    {
        public const string Daily = "Daily";
        public const string Weekly = "Weekly";
        public const string Monthly = "Monthly";

        public static string? Normalize(string? frequency)
        {
            var trimmed = frequency?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (trimmed.Equals(Daily, StringComparison.OrdinalIgnoreCase)) return Daily;
            if (trimmed.Equals(Weekly, StringComparison.OrdinalIgnoreCase)) return Weekly;
            if (trimmed.Equals(Monthly, StringComparison.OrdinalIgnoreCase)) return Monthly;
            return null;
        }
    }

    /// <summary>Who wrote a chat message. "System" rows are written by the server and never sent to the AI model as instructions.</summary>
    public static class ChatSenders
    {
        public const string User = "User";
        public const string Ai = "AI";
        public const string Agent = "Agent";
        public const string System = "System";

        /// <summary>Only used between the server and the AI services: retrieved FAQ text for the model. Never stored.</summary>
        public const string Context = "Context";
    }

    public static class TransactionCategories
    {
        public const string Other = "Diğer";
        public const string Bills = "Fatura";
        public const string Investment = "Yatırım";
    }
}
