using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;

namespace SmartBank.Infrastructure.Services
{
    /// <summary>Entity to DTO mapping for the banking API, kept out of the service so the rules there stay readable.</summary>
    internal static class BankingMappers
    {
        /// <param name="cardNumber">The decrypted debit card number ("" when it cannot be decrypted).</param>
        public static AccountDto ToDto(Account a, string cardNumber) => new()
        {
            Id = a.Id,
            AccountNumber = a.AccountNumber,
            AccountCode = a.AccountCode,
            Balance = a.Balance,
            Currency = a.Currency,
            CreatedAt = a.CreatedAt,
            CardNumber = cardNumber,
            CardTheme = a.CardTheme,
            ExpiryDate = a.ExpiryDate,
            AccountType = a.AccountType,
            InterestRate = DisplayedInterestRate(a),
            MaturityDate = a.MaturityDate
        };

        /// <summary>
        /// The rate shown for a time deposit depends on the balance (tiers). It is a display value only: no interest is ever
        /// booked on the account, and the balance can be moved at any time.
        /// </summary>
        public static decimal? DisplayedInterestRate(Account a)
        {
            if (!string.Equals(a.AccountType, AccountTypes.TimeDeposit, StringComparison.OrdinalIgnoreCase))
            {
                return a.InterestRate;
            }

            if (a.Balance < 50_000m) return 48.00m;
            if (a.Balance < 250_000m) return 49.50m;
            if (a.Balance < 1_000_000m) return 51.00m;
            return 52.50m;
        }

        public static CreditCardDto ToDto(CreditCard card, string cardNumber) => new()
        {
            Id = card.Id,
            CardNumber = cardNumber,
            ExpiryDate = card.ExpiryDate,
            CardLimit = card.CardLimit,
            CurrentDebt = card.CurrentDebt,
            AvailableLimit = card.CardLimit - card.CurrentDebt,
            CardTheme = card.CardTheme
        };

        public static CreditCardStatementDto ToDto(CreditCardStatement s, List<CreditCardTransactionDto>? transactions = null) => new()
        {
            Id = s.Id,
            PeriodName = s.PeriodName,
            PeriodDebt = s.PeriodDebt,
            MinimumPayment = s.MinimumPayment,
            PaidAmount = s.PaidAmount,
            CutoffDate = s.CutoffDate,
            DueDate = s.DueDate,
            IsPaid = s.IsPaid,
            Transactions = transactions ?? new List<CreditCardTransactionDto>()
        };

        public static StandingOrderDto ToDto(StandingOrder o, string? cardLast4 = null) => new()
        {
            Id = o.Id,
            SourceAccountNumber = o.SourceAccountNumber,
            DestinationAccountNumber = o.DestinationAccountNumber,
            Amount = o.Amount,
            Frequency = o.Frequency,
            MaturityDate = o.MaturityDate,
            NextExecutionDate = o.NextExecutionDate,
            IsActive = o.IsActive,
            OrderType = o.OrderType,
            CreditCardId = o.CreditCardId,
            CreditCardLast4 = cardLast4,
            CreatedAt = o.CreatedAt
        };

        public static SavedContactDto ToDto(SavedContact c) => new()
        {
            Id = c.Id,
            AccountNumber = c.AccountNumber,
            Alias = c.Alias,
            CreatedAt = c.CreatedAt
        };
    }
}
