using Microsoft.EntityFrameworkCore;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Infrastructure.Data;

namespace SmartBank.Infrastructure.Services
{
    public partial class BankingService
    {
        public Task<ServiceResult<TransactionDto>> ExchangeMoneyAsync(Guid userId, ExchangeDto exchangeDto) =>
            RunWithConcurrencyRetryAsync(() => ExchangeMoneyCoreAsync(userId, exchangeDto));

        private async Task<ServiceResult<TransactionDto>> ExchangeMoneyCoreAsync(Guid userId, ExchangeDto exchangeDto)
        {
            // 1. Everything that can be checked without touching the database or the rates.
            var action = exchangeDto.Action?.Trim().ToLowerInvariant();
            if (action is not ("buy" or "sell"))
            {
                return Fail<TransactionDto>("InvalidAction", "Action must be \"buy\" or \"sell\".");
            }

            var asset = Currencies.Normalize(exchangeDto.Asset);
            if (!Currencies.IsTradable(asset))
            {
                return Fail<TransactionDto>("InvalidCurrency", "Supported assets are USD, EUR, XAU and XAG.");
            }

            var invalid = ValidateAmount(exchangeDto.Amount);
            if (invalid != null) return Fail<TransactionDto>(invalid.Value.Key, invalid.Value.Message);

            if (!Guid.TryParse(exchangeDto.SourceAccountId, out var sourceAccountId))
            {
                return Fail<TransactionDto>("InvalidSourceAccount", "Geçersiz kaynak hesap.");
            }

            var sourceAcc = await _context.Accounts.FirstOrDefaultAsync(a => a.Id == sourceAccountId && a.UserId == userId);
            if (sourceAcc == null)
            {
                return Fail<TransactionDto>("AccountNotFound", "Kaynak hesap bulunamadı.");
            }

            var isBuy = action == "buy";
            if (isBuy && sourceAcc.Currency != Currencies.Try)
            {
                return Fail<TransactionDto>("InvalidExchangeSource", "Alış işlemi için kaynak hesap TL olmalıdır.");
            }

            if (!isBuy && sourceAcc.Currency != asset)
            {
                return Fail<TransactionDto>("InvalidExchangeSource", $"Satış işlemi için kaynak hesap {asset} olmalıdır.");
            }

            // 2. The price. It is looked up before any account is touched or created, and a stand-in price is refused:
            //    nobody should buy or sell at a made-up rate.
            var rateInfo = await _marketRateService.GetRateByCodeAsync(asset!);
            if (rateInfo == null)
            {
                return Fail<TransactionDto>("RateNotFound", "Kur bilgisi bulunamadı.");
            }

            if (rateInfo.IsFallback)
            {
                return Fail<TransactionDto>("RateUnavailable", "Güncel kur bilgisine şu anda ulaşılamıyor. Lütfen biraz sonra tekrar deneyin.");
            }

            var rate = isBuy ? rateInfo.Sell : rateInfo.Buy;
            if (rate <= 0m)
            {
                return Fail<TransactionDto>("RateUnavailable", "Güncel kur bilgisine şu anda ulaşılamıyor. Lütfen biraz sonra tekrar deneyin.");
            }

            // The TRY side is rounded once, here; the same value goes into the balances, the ledger row and the response.
            var tryValue = Money.Round(exchangeDto.Amount * rate);
            if (tryValue <= 0m)
            {
                return Fail<TransactionDto>("AmountTooSmall", "Bu tutar işlem yapılamayacak kadar küçük.");
            }

            // 3. The other account. Which one is chosen is deterministic: the oldest demand-deposit account of that currency.
            Account? targetAcc;
            if (isBuy)
            {
                if (sourceAcc.Balance < tryValue)
                {
                    return Fail<TransactionDto>("InsufficientFunds", "Yetersiz bakiye.");
                }

                targetAcc = await FindDemandAccountAsync(userId, asset!);
                if (targetAcc == null)
                {
                    if (await _context.Accounts.CountAsync(a => a.UserId == userId) >= MaxAccountsPerUser)
                    {
                        return Fail<TransactionDto>("AccountLimitReached", $"You can have at most {MaxAccountsPerUser} accounts.");
                    }

                    // Added here, saved together with the exchange below: if anything fails, no account is left behind.
                    (targetAcc, _, _) = await BuildAccountAsync(userId, asset!, AccountTypes.DemandDeposit);
                    _context.Accounts.Add(targetAcc);
                }
            }
            else
            {
                if (sourceAcc.Balance < exchangeDto.Amount)
                {
                    return Fail<TransactionDto>("InsufficientFunds", $"Yetersiz {asset} bakiyesi.");
                }

                targetAcc = await FindDemandAccountAsync(userId, Currencies.Try);
                if (targetAcc == null)
                {
                    return Fail<TransactionDto>("TryAccountNotFound", "Satış bedelinin aktarılacağı vadesiz TL hesabınız bulunamadı.");
                }
            }

            // 4. Move the money.
            if (isBuy)
            {
                sourceAcc.Balance -= tryValue;
                targetAcc.Balance += exchangeDto.Amount;
            }
            else
            {
                sourceAcc.Balance -= exchangeDto.Amount;
                targetAcc.Balance += tryValue;
            }

            var amountText = Money.Format(exchangeDto.Amount);
            var rateText = rate.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            var transaction = new Transaction
            {
                SourceAccountId = sourceAcc.Id,
                DestinationAccountId = targetAcc.Id,
                // One amount per row: the TRY value of a purchase, the quantity of a sale (the description says the rest).
                Amount = isBuy ? tryValue : exchangeDto.Amount,
                Description = isBuy
                    ? $"{amountText} {asset} Alımı (Kur: {rateText} TRY)"
                    : $"{amountText} {asset} Satışı (Kur: {rateText} TRY)",
                Type = TransactionType.Transfer,
                Category = TransactionCategories.Investment,
                CreatedAt = UtcNow
            };
            _context.Transactions.Add(transaction);

            _context.AuditLogs.Add(NewAudit(userId, isBuy ? "ExchangeBuy" : "ExchangeSell",
                isBuy
                    ? $"Bought {amountText} {asset} with {Money.Format(tryValue)} TRY. Rate: {rateText}"
                    : $"Sold {amountText} {asset} for {Money.Format(tryValue)} TRY. Rate: {rateText}"));

            // Balances, the ledger row, the audit entry and a new asset account are saved in one step.
            await _context.SaveChangesAsync();

            var ownerName = await _context.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.FullName).FirstOrDefaultAsync();
            return ServiceResult<TransactionDto>.Success(BankingMappers.WithSides(new TransactionDto
            {
                Id = transaction.Id,
                SourceAccountNumber = sourceAcc.AccountNumber,
                DestinationAccountNumber = targetAcc.AccountNumber,
                SourceCurrency = sourceAcc.Currency,
                DestinationCurrency = targetAcc.Currency,
                SourceAccountOwnerName = ownerName,
                DestinationAccountOwnerName = ownerName,
                Amount = transaction.Amount,
                Description = transaction.Description,
                Type = transaction.Type.ToString(),
                Category = transaction.Category,
                CreatedAt = transaction.CreatedAt
            }));
        }

        private Task<Account?> FindDemandAccountAsync(Guid userId, string currency) =>
            _context.Accounts
                .Where(a => a.UserId == userId && a.Currency == currency && a.AccountType == AccountTypes.DemandDeposit)
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                .FirstOrDefaultAsync();

        /// <summary>
        /// Converts an amount between two currencies through TRY at the bank's rates (what the bank pays for the source, what
        /// it charges for the target), rounded once. A currency without a live rate cannot be converted: there is no 1:1 guess.
        /// </summary>
        private async Task<(decimal? Amount, string? ErrorKey)> ConvertAsync(decimal amount, string from, string to)
        {
            if (from == to) return (Money.Round(amount), null);

            var rates = await _marketRateService.GetRatesAsync();

            MarketRateDto? Live(string code)
            {
                var rate = rates?.FirstOrDefault(r => r.Code == code);
                return rate == null || rate.IsFallback ? null : rate;
            }

            decimal tryValue;
            if (from == Currencies.Try)
            {
                tryValue = amount;
            }
            else
            {
                var sourceRate = Live(from);
                if (sourceRate == null || sourceRate.Buy <= 0m) return (null, "RateUnavailable");
                tryValue = amount * sourceRate.Buy;
            }

            if (to == Currencies.Try) return (Money.Round(tryValue), null);

            var targetRate = Live(to);
            if (targetRate == null || targetRate.Sell <= 0m) return (null, "RateUnavailable");
            return (Money.Round(tryValue / targetRate.Sell), null);
        }
    }
}
