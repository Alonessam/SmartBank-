using System.Globalization;
using System.Text.RegularExpressions;
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

        // The ledger keeps one amount per row. The other side of an exchange or of a cross-currency closing transfer is only
        // in the description the service wrote ("100.00 USD Alımı (Kur: 31.0465 TRY)", "Hesap Kapatma Bakiye Aktarımı
        // (USD -> TRY): 100.00 USD"). It is read back from there, never guessed: a description that does not match gives
        // the plain single-amount view.
        private static readonly Regex ExchangeText = new(
            @"^(?<qty>\d+\.\d{2}) (?<asset>[A-Z]{3}) (?<kind>Alımı|Satışı) \(Kur: (?<rate>\d+(?:\.\d+)?) TRY\)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private static readonly Regex ClosingText = new(
            @"^Hesap Kapatma Bakiye Aktarımı \((?<from>[A-Z]{3}) -> (?<to>[A-Z]{3})\): (?<amount>\d+\.\d{2}) [A-Z]{3}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        /// <summary>
        /// Fills the four side fields of a transaction. The caller has set the account numbers, Type, Amount, Description and
        /// the currencies of the linked accounts (null when the account is gone).
        /// </summary>
        public static TransactionDto WithSides(TransactionDto dto)
        {
            var hasSource = dto.SourceAccountNumber != null;
            var hasDestination = dto.DestinationAccountNumber != null;
            var sourceCurrency = dto.SourceCurrency;
            var destinationCurrency = dto.DestinationCurrency;

            dto.SourceCurrency = dto.DestinationCurrency = null;
            dto.SourceAmount = dto.DestinationAmount = null;

            if (dto.Type == nameof(TransactionType.Deposit))
            {
                dto.DestinationCurrency = destinationCurrency ?? sourceCurrency;
                dto.DestinationAmount = dto.Amount;
                return dto;
            }

            if (dto.Type == nameof(TransactionType.Withdrawal))
            {
                dto.SourceCurrency = sourceCurrency;
                dto.SourceAmount = hasSource ? dto.Amount : null;
                return dto;
            }

            try
            {
                var closing = ClosingText.Match(dto.Description);
                if (closing.Success)
                {
                    dto.SourceCurrency = closing.Groups["from"].Value;
                    dto.SourceAmount = decimal.Parse(closing.Groups["amount"].Value, NumberStyles.Number, CultureInfo.InvariantCulture);
                    dto.DestinationCurrency = destinationCurrency ?? closing.Groups["to"].Value;
                    dto.DestinationAmount = dto.Amount; // what the receiving account was credited
                    return dto;
                }

                var exchange = ExchangeText.Match(dto.Description);
                if (exchange.Success && hasSource && hasDestination)
                {
                    var quantity = decimal.Parse(exchange.Groups["qty"].Value, NumberStyles.Number, CultureInfo.InvariantCulture);
                    var asset = exchange.Groups["asset"].Value;
                    var rate = decimal.Parse(exchange.Groups["rate"].Value, NumberStyles.Number, CultureInfo.InvariantCulture);
                    var isBuy = exchange.Groups["kind"].Value == "Alımı";

                    if (isBuy)
                    {
                        // The row's amount is the TRY cost; the quantity bought is in the text.
                        dto.SourceCurrency = Currencies.Try;
                        dto.SourceAmount = dto.Amount;
                        dto.DestinationCurrency = asset;
                        dto.DestinationAmount = quantity;
                        return dto;
                    }

                    if (dto.Amount == quantity)
                    {
                        // The row's amount is the quantity sold; the proceeds are the same rounded product the exchange booked.
                        dto.SourceCurrency = asset;
                        dto.SourceAmount = dto.Amount;
                        dto.DestinationCurrency = Currencies.Try;
                        dto.DestinationAmount = Money.Round(quantity * rate);
                        return dto;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // fall through to the plain view
            }

            dto.SourceCurrency = sourceCurrency;
            dto.SourceAmount = hasSource ? dto.Amount : null;
            dto.DestinationCurrency = destinationCurrency;
            dto.DestinationAmount = hasDestination ? dto.Amount : null;
            return dto;
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
