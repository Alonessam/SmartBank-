using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Infrastructure.Data;

namespace SmartBank.Infrastructure.Services
{
    public partial class BankingService
    {
        private const int MaxChargeDescriptionLength = 200;

        public async Task<ServiceResult<List<CreditCardDto>>> GetCreditCardsAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var cards = await _context.CreditCards
                .AsNoTracking()
                .Where(cc => cc.UserId == userId)
                .OrderBy(cc => cc.CreatedAt).ThenBy(cc => cc.Id)
                .ToListAsync(cancellationToken);

            return ServiceResult<List<CreditCardDto>>.Success(cards.Select(cc => BankingMappers.ToDto(cc, SafeDecrypt(cc.EncryptedCardNumber))).ToList());
        }

        public async Task<ServiceResult<CreditCardDto>> CreateCreditCardAsync(Guid userId)
        {
            if (await _context.CreditCards.AnyAsync(cc => cc.UserId == userId))
            {
                return Fail<CreditCardDto>("MaxCreditCardsLimitReached", "En fazla 1 adet kredi kartı sahibi olabilirsiniz.");
            }

            // Duplicate check goes through the keyed hash: ciphertext is randomised, so it cannot be compared.
            var cardNumber = GenerateCardNumber();
            var cardHash = EncryptionHelper.HashCardNumber(cardNumber);
            while (await _context.CreditCards.AnyAsync(cc => cc.CardNumberHash == cardHash))
            {
                cardNumber = GenerateCardNumber();
                cardHash = EncryptionHelper.HashCardNumber(cardNumber);
            }

            var now = UtcNow;
            var firstCutoff = now.AddDays(CreditCardRules.FirstStatementDays);

            var creditCard = new CreditCard
            {
                UserId = userId,
                EncryptedCardNumber = EncryptionHelper.Encrypt(cardNumber),
                CardNumberHash = cardHash,
                ExpiryDate = CardFormat.ExpiryIn(now, CreditCardRules.IssuedCardValidityYears),
                CardLimit = CreditCardRules.DefaultLimit,
                CurrentDebt = 0.00m,
                CardTheme = "theme-neon-blue",
                CreatedAt = now
            };

            creditCard.Statements.Add(new CreditCardStatement
            {
                PeriodName = CreditCardRules.PeriodName(firstCutoff),
                PeriodDebt = 0.00m,
                MinimumPayment = 0.00m,
                PaidAmount = 0.00m,
                CutoffDate = firstCutoff,
                DueDate = firstCutoff.AddDays(CreditCardRules.DueDaysAfterCutoff),
                IsPaid = true
            });

            _context.CreditCards.Add(creditCard);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (DatabaseConflict.UniqueConstraintName(ex) is { } index &&
                                               !index.Contains("CardNumberHash", StringComparison.OrdinalIgnoreCase))
            {
                // Two requests passed the check above at the same moment; the unique index on the owner let only one in.
                _context.ChangeTracker.Clear(); // the card AND the statement that was added with it
                return Fail<CreditCardDto>("MaxCreditCardsLimitReached", "En fazla 1 adet kredi kartı sahibi olabilirsiniz.");
            }

            // Shown to the user once in this response; the CVV is never stored.
            var dto = BankingMappers.ToDto(creditCard, cardNumber);
            dto.CardCvv = GenerateCvv();
            return ServiceResult<CreditCardDto>.Success(dto);
        }

        public async Task<ServiceResult<List<CreditCardStatementDto>>> GetStatementsAsync(Guid cardId, Guid userId, CancellationToken cancellationToken = default)
        {
            var owned = await _context.CreditCards.AsNoTracking().AnyAsync(cc => cc.Id == cardId && cc.UserId == userId, cancellationToken);
            if (!owned)
            {
                return Fail<List<CreditCardStatementDto>>("CreditCardNotFound", "Credit card not found.");
            }

            // The newest statements only. One extra row tells where the oldest returned statement's period begins.
            var statements = await _context.CreditCardStatements
                .AsNoTracking()
                .Where(s => s.CreditCardId == cardId)
                .OrderByDescending(s => s.CutoffDate)
                .Take(CreditCardRules.MaxStatementsReturned + 1)
                .ToListAsync(cancellationToken);

            var shown = statements.Take(CreditCardRules.MaxStatementsReturned).ToList();
            var windowStart = statements.Count > shown.Count ? statements[^1].CutoffDate : DateTime.MinValue;

            var transactions = await _context.CreditCardTransactions
                .AsNoTracking()
                .Where(t => t.CreditCardId == cardId && t.CreatedAt > windowStart)
                .OrderByDescending(t => t.CreatedAt)
                .Take(CreditCardRules.MaxStatementTransactions)
                .ToListAsync(cancellationToken);

            var dtos = new List<CreditCardStatementDto>();
            for (var i = 0; i < shown.Count; i++)
            {
                var current = shown[i];
                // A statement covers what happened after the previous cut-off up to its own.
                var start = i + 1 < statements.Count ? statements[i + 1].CutoffDate : DateTime.MinValue;

                var inPeriod = transactions
                    .Where(t => t.CreatedAt > start && t.CreatedAt <= current.CutoffDate)
                    .Select(t => new CreditCardTransactionDto { Id = t.Id, Description = t.Description, Amount = t.Amount, CreatedAt = t.CreatedAt })
                    .ToList();

                dtos.Add(BankingMappers.ToDto(current, inPeriod));
            }

            return ServiceResult<List<CreditCardStatementDto>>.Success(dtos);
        }

        public Task<ServiceResult<bool>> PayCreditCardDebtAsync(Guid userId, Guid cardId, PayCreditCardDebtDto payRequest) =>
            RunWithConcurrencyRetryAsync(() => PayCreditCardDebtCoreAsync(userId, cardId, payRequest));

        private async Task<ServiceResult<bool>> PayCreditCardDebtCoreAsync(Guid userId, Guid cardId, PayCreditCardDebtDto payRequest)
        {
            var invalid = ValidateAmount(payRequest.Amount);
            if (invalid != null) return Fail<bool>(invalid.Value.Key, invalid.Value.Message);

            var card = await _context.CreditCards.FirstOrDefaultAsync(cc => cc.Id == cardId && cc.UserId == userId);
            if (card == null)
            {
                return Fail<bool>("CreditCardNotFound", "Credit card not found.");
            }

            // Paying more than is owed used to take the whole amount from the account and credit only the debt: the rest vanished.
            if (card.CurrentDebt <= 0m)
            {
                return Fail<bool>("PaymentExceedsDebt", "There is no debt to pay on this card.");
            }

            if (payRequest.Amount > card.CurrentDebt)
            {
                return Fail<bool>("PaymentExceedsDebt", $"The payment cannot be more than the current debt ({Money.Format(card.CurrentDebt)}).");
            }

            var sourceNumber = NormalizeAccountNumber(payRequest.SourceAccountNumber);
            var sourceAccount = await _context.Accounts.FirstOrDefaultAsync(a => a.AccountNumber == sourceNumber && a.UserId == userId);
            if (sourceAccount == null)
            {
                return Fail<bool>("SourceAccountNotFound", "Source account not found.");
            }

            if (sourceAccount.Currency != Currencies.Try)
            {
                return Fail<bool>("CurrencyMismatch", "Only TRY accounts can be used to pay credit card debt.");
            }

            if (sourceAccount.Balance < payRequest.Amount)
            {
                return Fail<bool>("InsufficientFunds", "Insufficient funds in the source account.");
            }

            await using var dbTransaction = await _context.Database.BeginTransactionAsync();
            try
            {
                sourceAccount.Balance -= payRequest.Amount;
                card.CurrentDebt -= payRequest.Amount;

                // The payment settles the oldest open statements first; each one records only what was applied to it.
                var remaining = payRequest.Amount;
                var openStatements = await _context.CreditCardStatements
                    .Where(s => s.CreditCardId == cardId && !s.IsPaid)
                    .OrderBy(s => s.DueDate).ThenBy(s => s.CutoffDate)
                    .ToListAsync();

                foreach (var statement in openStatements)
                {
                    if (remaining <= 0m) break;

                    var applied = Math.Min(remaining, Math.Max(0m, statement.PeriodDebt - statement.PaidAmount));
                    statement.PaidAmount += applied;
                    remaining -= applied;
                    if (statement.PaidAmount >= statement.PeriodDebt)
                    {
                        statement.IsPaid = true;
                    }
                }

                _context.Transactions.Add(new Transaction
                {
                    SourceAccountId = sourceAccount.Id,
                    DestinationAccountId = null,
                    Amount = payRequest.Amount,
                    Description = $"Kredi Kartı Borç Ödeme - Kart: *{CardMasking.LastFour(SafeDecrypt(card.EncryptedCardNumber))}",
                    Type = TransactionType.Withdrawal,
                    Category = TransactionCategories.Bills,
                    CreatedAt = UtcNow
                });
                _context.AuditLogs.Add(NewAudit(userId, "CreditCardPayment",
                    Text($"Paid {Money.Format(payRequest.Amount)} TRY of credit card debt from {sourceAccount.AccountNumber}.")));

                await _context.SaveChangesAsync();
                await dbTransaction.CommitAsync();

                return ServiceResult<bool>.Success(true);
            }
            catch (Exception ex) when (DatabaseConflict.IsRetryable(ex))
            {
                await dbTransaction.RollbackAsync();
                throw;
            }
            catch (Exception ex)
            {
                await dbTransaction.RollbackAsync();
                _logger.LogError(ex, "Credit card payment failed.");
                return Fail<bool>("PaymentFailed", "The payment could not be completed. Please try again.");
            }
        }

        public Task<ServiceResult<CreditCardDto>> ChargeCreditCardAsync(Guid userId, Guid cardId, decimal amount, string description) =>
            RunWithConcurrencyRetryAsync(() => ChargeCreditCardCoreAsync(userId, cardId, amount, description));

        // A simulation of a shop charging the card (the controller can switch it off with Demo:EnableSimulationEndpoints).
        private async Task<ServiceResult<CreditCardDto>> ChargeCreditCardCoreAsync(Guid userId, Guid cardId, decimal amount, string description)
        {
            var invalid = ValidateAmount(amount);
            if (invalid != null) return Fail<CreditCardDto>(invalid.Value.Key, invalid.Value.Message);

            description = description?.Trim() ?? string.Empty;
            if (description.Length > MaxChargeDescriptionLength)
            {
                return Fail<CreditCardDto>("InvalidDescription", $"Description cannot exceed {MaxChargeDescriptionLength} characters.");
            }

            var card = await _context.CreditCards.FirstOrDefaultAsync(cc => cc.Id == cardId && cc.UserId == userId);
            if (card == null)
            {
                return Fail<CreditCardDto>("CreditCardNotFound", "Credit card not found.");
            }

            if (card.CardLimit - card.CurrentDebt < amount)
            {
                return Fail<CreditCardDto>("InsufficientLimit", "Insufficient credit card limit.");
            }

            await using var dbTransaction = await _context.Database.BeginTransactionAsync();
            try
            {
                card.CurrentDebt += amount;

                var ccTx = new CreditCardTransaction
                {
                    CreditCardId = card.Id,
                    Description = string.IsNullOrEmpty(description) ? "Market Harcaması" : description,
                    Amount = amount,
                    CreatedAt = UtcNow
                };
                _context.CreditCardTransactions.Add(ccTx);
                _context.AuditLogs.Add(NewAudit(userId, "CreditCardCharge",
                    Text($"Kredi kartından harcama yapıldı. Tutar: {Money.Format(amount)} TRY, İşyeri: {ccTx.Description}")));

                await _context.SaveChangesAsync();
                await dbTransaction.CommitAsync();

                return ServiceResult<CreditCardDto>.Success(BankingMappers.ToDto(card, SafeDecrypt(card.EncryptedCardNumber)));
            }
            catch (Exception ex) when (DatabaseConflict.IsRetryable(ex))
            {
                await dbTransaction.RollbackAsync();
                throw;
            }
            catch (Exception ex)
            {
                await dbTransaction.RollbackAsync();
                _logger.LogError(ex, "Credit card charge failed.");
                return Fail<CreditCardDto>("ChargeFailed", "The charge could not be completed. Please try again.");
            }
        }

        public Task<ServiceResult<CreditCardStatementDto>> AdvanceStatementPeriodAsync(Guid userId, Guid cardId) =>
            RunWithConcurrencyRetryAsync(() => AdvanceStatementPeriodCoreAsync(userId, cardId));

        // Closes the current statement and opens the next one. In a real bank the calendar does this; here the customer can
        // trigger it to see interest and statements without waiting a month (switchable with Demo:EnableSimulationEndpoints).
        private async Task<ServiceResult<CreditCardStatementDto>> AdvanceStatementPeriodCoreAsync(Guid userId, Guid cardId)
        {
            var card = await _context.CreditCards.FirstOrDefaultAsync(cc => cc.Id == cardId && cc.UserId == userId);
            if (card == null)
            {
                return Fail<CreditCardStatementDto>("CreditCardNotFound", "Credit card not found.");
            }

            var latestStatement = await _context.CreditCardStatements
                .Where(s => s.CreditCardId == cardId)
                .OrderByDescending(s => s.CutoffDate)
                .FirstOrDefaultAsync();

            var now = UtcNow;
            var interest = 0.00m;

            // An automatic payment order pays the open statement from its account first (all of it, or what the account holds).
            var autopayOrder = await _context.StandingOrders
                .FirstOrDefaultAsync(so => so.UserId == userId && so.CreditCardId == cardId && so.IsActive && so.OrderType == OrderTypes.CreditCardAutoPay);

            if (autopayOrder != null && latestStatement != null && !latestStatement.IsPaid)
            {
                var sourceAccount = await _context.Accounts
                    .FirstOrDefaultAsync(a => a.AccountNumber == autopayOrder.SourceAccountNumber && a.UserId == userId && a.Currency == Currencies.Try);

                var amountToPay = latestStatement.PeriodDebt - latestStatement.PaidAmount;
                if (sourceAccount != null && amountToPay > 0m && sourceAccount.Balance > 0m)
                {
                    var paid = Math.Min(amountToPay, sourceAccount.Balance);
                    var isPartial = paid < amountToPay;

                    sourceAccount.Balance -= paid;
                    latestStatement.PaidAmount += paid;
                    latestStatement.IsPaid = !isPartial;
                    card.CurrentDebt -= paid;

                    _context.Transactions.Add(new Transaction
                    {
                        SourceAccountId = sourceAccount.Id,
                        Amount = paid,
                        Description = isPartial
                            ? $"Kredi Kartı Otomatik Borç Ödeme - Kısmi ({latestStatement.PeriodName})"
                            : $"Kredi Kartı Otomatik Borç Ödeme ({latestStatement.PeriodName})",
                        Type = TransactionType.Withdrawal,
                        Category = TransactionCategories.Bills,
                        CreatedAt = now
                    });
                }
            }

            // Closing the statement: what is still unpaid carries interest into the next period.
            if (latestStatement != null && !latestStatement.IsPaid)
            {
                if (latestStatement.PaidAmount >= latestStatement.PeriodDebt)
                {
                    latestStatement.IsPaid = true;
                }
                else
                {
                    interest = CreditCardRules.Interest(latestStatement.PeriodDebt, latestStatement.PaidAmount, latestStatement.MinimumPayment);
                    card.CurrentDebt += interest;

                    _context.CreditCardTransactions.Add(new CreditCardTransaction
                    {
                        CreditCardId = card.Id,
                        Description = $"Gecikme/Akdi Faiz Yansıması ({latestStatement.PeriodName})",
                        Amount = interest,
                        CreatedAt = now
                    });

                    latestStatement.IsPaid = true; // closed by the rollover; the unpaid rest lives on in the new statement
                }
            }

            var nextCutoff = latestStatement != null ? latestStatement.CutoffDate.AddMonths(1) : now.AddMonths(1);
            var newStatement = new CreditCardStatement
            {
                CreditCardId = card.Id,
                PeriodName = CreditCardRules.PeriodName(nextCutoff),
                PeriodDebt = card.CurrentDebt,
                MinimumPayment = CreditCardRules.MinimumPayment(card.CurrentDebt),
                PaidAmount = 0.00m,
                CutoffDate = nextCutoff,
                DueDate = nextCutoff.AddDays(CreditCardRules.DueDaysAfterCutoff),
                IsPaid = card.CurrentDebt <= 0m
            };

            _context.CreditCardStatements.Add(newStatement);
            _context.AuditLogs.Add(NewAudit(userId, "StatementAdvanced",
                Text($"Statement period closed, next: {newStatement.PeriodName}. Interest added: {Money.Format(interest)} TRY.")));
            await _context.SaveChangesAsync();

            return ServiceResult<CreditCardStatementDto>.Success(BankingMappers.ToDto(newStatement));
        }
    }
}
