using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Data;
using Microsoft.Extensions.Configuration;

namespace SmartBank.Infrastructure.Services
{
    public class BankingService : IBankingService
    {
        private readonly SmartBankDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IMarketRateService _marketRateService;

        public BankingService(SmartBankDbContext context, IConfiguration configuration, IMarketRateService marketRateService)
        {
            _context = context;
            _configuration = configuration;
            _marketRateService = marketRateService;
        }

        public async Task<ServiceResult<List<AccountDto>>> GetAccountsAsync(Guid userId)
        {
            var accounts = await _context.Accounts
                .Where(a => a.UserId == userId)
                .ToListAsync();

            var accountDtos = accounts.Select(a =>
            {
                decimal? calculatedInterestRate = a.InterestRate;
                if (a.AccountType != null && a.AccountType.Equals("TimeDeposit", StringComparison.OrdinalIgnoreCase))
                {
                    if (a.Balance < 50000m) calculatedInterestRate = 48.00m;
                    else if (a.Balance < 250000m) calculatedInterestRate = 49.50m;
                    else if (a.Balance < 1000000m) calculatedInterestRate = 51.00m;
                    else calculatedInterestRate = 52.50m;
                }

                return new AccountDto
                {
                    Id = a.Id,
                    AccountNumber = a.AccountNumber,
                    AccountCode = a.AccountCode,
                    Balance = a.Balance,
                    Currency = a.Currency,
                    CreatedAt = a.CreatedAt,
                    CardNumber = Core.Common.EncryptionHelper.Decrypt(a.EncryptedCardNumber) ?? string.Empty,
                    CardCvv = Core.Common.EncryptionHelper.Decrypt(a.EncryptedCardCvv) ?? string.Empty,
                    CardTheme = a.CardTheme,
                    ExpiryDate = a.ExpiryDate,
                    AccountType = a.AccountType,
                    InterestRate = calculatedInterestRate,
                    MaturityDate = a.MaturityDate
                };
            }).ToList();

            return ServiceResult<List<AccountDto>>.Success(accountDtos);
        }

        public async Task<ServiceResult<List<TransactionDto>>> GetTransactionsAsync(Guid accountId, Guid userId)
        {
            // Verify account exists and belongs to the user
            var accountExists = await _context.Accounts
                .AnyAsync(a => a.Id == accountId && a.UserId == userId);

            if (!accountExists)
            {
                return ServiceResult<List<TransactionDto>>.Failure("UnauthorizedAccountAccess", "Account not found or access denied.");
            }

            var transactions = await _context.Transactions
                .Include(t => t.SourceAccount).ThenInclude(sa => sa!.User)
                .Include(t => t.DestinationAccount).ThenInclude(da => da!.User)
                .Where(t => t.SourceAccountId == accountId || t.DestinationAccountId == accountId)
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => new TransactionDto
                {
                    Id = t.Id,
                    SourceAccountNumber = t.SourceAccount != null ? t.SourceAccount.AccountNumber : null,
                    DestinationAccountNumber = t.DestinationAccount != null ? t.DestinationAccount.AccountNumber : null,
                    SourceAccountOwnerName = t.SourceAccount != null && t.SourceAccount.User != null ? t.SourceAccount.User.FullName : null,
                    DestinationAccountOwnerName = t.DestinationAccount != null && t.DestinationAccount.User != null ? t.DestinationAccount.User.FullName : null,
                    Amount = t.Amount,
                    Description = t.Description,
                    Type = t.Type.ToString(),
                    Category = t.Category,
                    CreatedAt = t.CreatedAt
                })
                .ToListAsync();

            return ServiceResult<List<TransactionDto>>.Success(transactions);
        }

        public async Task<ServiceResult<TransactionDto>> TransferMoneyAsync(Guid userId, TransferRequestDto transferRequest)
        {
            if (transferRequest.Amount <= 0)
            {
                return ServiceResult<TransactionDto>.Failure("InvalidAmount", "Amount must be greater than zero.");
            }

            if (transferRequest.SourceAccountNumber == transferRequest.DestinationAccountNumber)
            {
                return ServiceResult<TransactionDto>.Failure("CannotTransferToSelf", "Cannot transfer money to the same account.");
            }

            // 1. Fetch and validate source account (must exist and belong to the user)
            var sourceAccount = await _context.Accounts
                .Include(a => a.User)
                .FirstOrDefaultAsync(a => a.AccountNumber == transferRequest.SourceAccountNumber);

            if (sourceAccount == null)
            {
                return ServiceResult<TransactionDto>.Failure("SourceAccountNotFound", "Source account was not found.");
            }

            if (sourceAccount.UserId != userId)
            {
                return ServiceResult<TransactionDto>.Failure("UnauthorizedAccountAccess", "You do not have access to this source account.");
            }

            // 2. Fetch and validate destination account
            var destinationAccount = await _context.Accounts
                .FirstOrDefaultAsync(a => a.AccountNumber == transferRequest.DestinationAccountNumber);

            if (destinationAccount == null)
            {
                return ServiceResult<TransactionDto>.Failure("DestinationAccountNotFound", "Destination account was not found.");
            }

            // Note: Multi-currency transfers are simplified to same currency for this MVP.
            if (sourceAccount.Currency != destinationAccount.Currency)
            {
                return ServiceResult<TransactionDto>.Failure("CurrencyMismatch", "Currency exchange transfers are not supported in this version.");
            }

            // 3. Verify balance
            if (sourceAccount.Balance < transferRequest.Amount)
            {
                return ServiceResult<TransactionDto>.Failure("InsufficientFunds", "Insufficient funds in the source account.");
            }

            // 4. Fraud and 2FA Verification Checks
            var user = sourceAccount.User;
            bool checkFraudAnd2FA = true;

            // If OTP is provided, verify it first
            if (!string.IsNullOrEmpty(transferRequest.OtpCode))
            {
                if (user == null)
                {
                    return ServiceResult<TransactionDto>.Failure("UserNotFound", "User details not found.");
                }

                if (user.TwoFactorSecret != transferRequest.OtpCode || 
                    !user.TwoFactorExpiry.HasValue || 
                    user.TwoFactorExpiry.Value < DateTime.UtcNow)
                {
                    return ServiceResult<TransactionDto>.Failure("InvalidOtpCode", "Invalid or expired verification code.");
                }

                // Clear OTP after successful verification
                user.TwoFactorSecret = null;
                user.TwoFactorExpiry = null;
                _context.Users.Update(user);
                await _context.SaveChangesAsync();

                // Skip fraud/2FA checks because user validated it via OTP
                checkFraudAnd2FA = false;
            }

            if (checkFraudAnd2FA)
            {
                bool needsOtp = false;
                string reasonKey = "Requires2FA";
                string reasonMessage = "Verification required.";

                // Rule A: Duplicate Transaction Check (last 30 seconds to same destination with same amount)
                var thirtySecondsAgo = DateTime.UtcNow.AddSeconds(-30);
                var isDuplicate = await _context.Transactions
                    .AnyAsync(t => t.SourceAccountId == sourceAccount.Id && 
                                   t.DestinationAccountId == destinationAccount.Id && 
                                   t.Amount == transferRequest.Amount && 
                                   t.CreatedAt >= thirtySecondsAgo);

                if (isDuplicate)
                {
                    needsOtp = true;
                    reasonKey = "SuspectedFraudDuplicate";
                    reasonMessage = "Şüpheli işlem: Son 30 saniye içerisinde aynı hesaba aynı miktarda transfer denemesi.";
                }

                // Rule B: High-Value Transaction Check (> 5x average spending or > 2000 TRY on new account)
                if (!needsOtp)
                {
                    var pastTransactions = await _context.Transactions
                        .Where(t => t.SourceAccountId == sourceAccount.Id)
                        .Select(t => t.Amount)
                        .ToListAsync();

                    if (pastTransactions.Count > 0)
                    {
                        var averageSpend = pastTransactions.Average();
                        if (transferRequest.Amount > 5 * averageSpend && transferRequest.Amount > 500.00m)
                        {
                            needsOtp = true;
                            reasonKey = "SuspectedFraudHighValue";
                            reasonMessage = $"Şüpheli işlem: Transfer miktarı ortalama harcamanızın ({averageSpend:F2} TRY) 5 katından fazla.";
                        }
                    }
                    else
                    {
                        if (transferRequest.Amount > 2000.00m)
                        {
                            needsOtp = true;
                            reasonKey = "SuspectedFraudHighValue";
                            reasonMessage = "Şüpheli işlem: Yeni hesaplar için tek seferlik transfer limiti (2000 TRY) aşıldı.";
                        }
                    }
                }

                // Rule C: General 2FA check (User has enabled 2FA and amount is > 1000 TRY)
                if (!needsOtp && user != null && user.TwoFactorEnabled && transferRequest.Amount > 1000.00m)
                {
                    needsOtp = true;
                    reasonKey = "Requires2FA";
                    reasonMessage = "Güvenlik doğrulaması: 1000 TRY üzerindeki transferler için doğrulama gerekiyor.";
                }

                if (needsOtp && user != null)
                {
                    // Generate 6-digit OTP code
                    var random = new Random();
                    var otp = random.Next(100000, 1000000).ToString();

                    user.TwoFactorSecret = otp;
                    user.TwoFactorExpiry = DateTime.UtcNow.AddMinutes(5);

                    _context.Users.Update(user);
                    await _context.SaveChangesAsync();

                    // Send email in a background task
                    _ = Task.Run(async () => {
                        await Send2FaEmailAsync(user.Email, user.FullName, otp);
                    });

                    // Print to server console for testing/audit purposes
                    Console.WriteLine($"[SmartBank 2FA OTP] Generated OTP Code: {otp} for user {user.Username} (Expires: {user.TwoFactorExpiry})");

                    // Return OTP code inside the message for simulation purposes in frontend
                    return ServiceResult<TransactionDto>.Failure(reasonKey, $"{reasonMessage}|OTP:{otp}");
                }
            }

            // Using DB Transaction to guarantee atomicity of the money transfer
            using var dbTransaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // 4. Update balances
                sourceAccount.Balance -= transferRequest.Amount;
                destinationAccount.Balance += transferRequest.Amount;

                // 5. Record Transaction
                var transaction = new Transaction
                {
                    SourceAccountId = sourceAccount.Id,
                    DestinationAccountId = destinationAccount.Id,
                    Amount = transferRequest.Amount,
                    Description = transferRequest.Description,
                    Type = TransactionType.Transfer,
                    Category = string.IsNullOrEmpty(transferRequest.Category) ? "Diğer" : transferRequest.Category,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Transactions.Add(transaction);

                // Write Audit Log
                var audit = new AuditLog
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Action = "TransferMoney",
                    Details = $"Transferred {transferRequest.Amount} TRY from {sourceAccount.AccountNumber} to {destinationAccount.AccountNumber}",
                    IpAddress = "127.0.0.1",
                    CreatedAt = DateTime.UtcNow
                };
                _context.AuditLogs.Add(audit);

                // Save EF context changes
                await _context.SaveChangesAsync();

                // Commit transactional scope
                await dbTransaction.CommitAsync();

                var dto = new TransactionDto
                {
                    Id = transaction.Id,
                    SourceAccountNumber = sourceAccount.AccountNumber,
                    DestinationAccountNumber = destinationAccount.AccountNumber,
                    Amount = transaction.Amount,
                    Description = transaction.Description,
                    Type = transaction.Type.ToString(),
                    Category = transaction.Category,
                    CreatedAt = transaction.CreatedAt
                };

                return ServiceResult<TransactionDto>.Success(dto);
            }
            catch (Exception ex)
            {
                // Rollback EF transaction changes on general exceptions
                await dbTransaction.RollbackAsync();
                return ServiceResult<TransactionDto>.Failure("TransactionFailed", $"An error occurred during transaction: {ex.Message}");
            }
        }

        public async Task<ServiceResult<AccountDto>> CreateAccountAsync(Guid userId, string currency, string accountType = "DemandDeposit")
        {
            var userExists = await _context.Users.AnyAsync(u => u.Id == userId);
            if (!userExists)
            {
                return ServiceResult<AccountDto>.Failure("UserNotFound", "User details not found.");
            }

            var accountNumber = GenerateAccountNumber();
            while (await _context.Accounts.AnyAsync(a => a.AccountNumber == accountNumber))
            {
                accountNumber = GenerateAccountNumber();
            }

            var cardNum = GenerateCardNumber();
            var cvv = GenerateCvv();

            var accountCode = "ACC-" + new Random().Next(1000000, 9999999).ToString();
            while (await _context.Accounts.AnyAsync(a => a.AccountCode == accountCode))
            {
                accountCode = "ACC-" + new Random().Next(1000000, 9999999).ToString();
            }

            var newAccount = new Account
            {
                UserId = userId,
                AccountNumber = accountNumber,
                AccountCode = accountCode,
                Balance = 0.00m,
                Currency = string.IsNullOrEmpty(currency) ? "TRY" : currency.ToUpper(),
                EncryptedCardNumber = Core.Common.EncryptionHelper.Encrypt(cardNum),
                EncryptedCardCvv = Core.Common.EncryptionHelper.Encrypt(cvv),
                CardTheme = "theme-neon-blue",
                ExpiryDate = DateTime.UtcNow.AddYears(5).ToString("MM/yy"),
                AccountType = string.IsNullOrEmpty(accountType) ? "DemandDeposit" : accountType
            };

            if (newAccount.AccountType.Equals("TimeDeposit", StringComparison.OrdinalIgnoreCase))
            {
                newAccount.InterestRate = 48.00m; // Starting default tier
                newAccount.MaturityDate = DateTime.UtcNow.AddDays(30);
            }

            _context.Accounts.Add(newAccount);
            await _context.SaveChangesAsync();

            var dto = new AccountDto
            {
                Id = newAccount.Id,
                AccountNumber = newAccount.AccountNumber,
                AccountCode = newAccount.AccountCode,
                Balance = newAccount.Balance,
                Currency = newAccount.Currency,
                CreatedAt = newAccount.CreatedAt,
                CardNumber = cardNum,
                CardCvv = cvv,
                CardTheme = newAccount.CardTheme,
                ExpiryDate = newAccount.ExpiryDate,
                AccountType = newAccount.AccountType,
                InterestRate = newAccount.InterestRate,
                MaturityDate = newAccount.MaturityDate
            };

            return ServiceResult<AccountDto>.Success(dto);
        }

        public async Task<ServiceResult<List<CreditCardDto>>> GetCreditCardsAsync(Guid userId)
        {
            var cards = await _context.CreditCards
                .Where(cc => cc.UserId == userId)
                .ToListAsync();

            var dtos = cards.Select(cc => new CreditCardDto
            {
                Id = cc.Id,
                CardNumber = Core.Common.EncryptionHelper.Decrypt(cc.EncryptedCardNumber),
                CardCvv = Core.Common.EncryptionHelper.Decrypt(cc.EncryptedCardCvv),
                ExpiryDate = cc.ExpiryDate,
                CardLimit = cc.CardLimit,
                CurrentDebt = cc.CurrentDebt,
                AvailableLimit = cc.CardLimit - cc.CurrentDebt,
                CardTheme = cc.CardTheme
            }).ToList();

            return ServiceResult<List<CreditCardDto>>.Success(dtos);
        }

        public async Task<ServiceResult<List<CreditCardStatementDto>>> GetStatementsAsync(Guid cardId, Guid userId)
        {
            var card = await _context.CreditCards.FirstOrDefaultAsync(cc => cc.Id == cardId && cc.UserId == userId);
            if (card == null)
            {
                return ServiceResult<List<CreditCardStatementDto>>.Failure("CreditCardNotFound", "Credit card not found or access denied.");
            }

            var statements = await _context.CreditCardStatements
                .Where(s => s.CreditCardId == cardId)
                .OrderByDescending(s => s.CutoffDate)
                .ToListAsync();

            var allTransactions = await _context.CreditCardTransactions
                .Where(t => t.CreditCardId == cardId)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();

            var dtos = new List<CreditCardStatementDto>();

            for (int i = 0; i < statements.Count; i++)
            {
                var currentStmt = statements[i];
                DateTime startDate = DateTime.MinValue;
                if (i + 1 < statements.Count)
                {
                    startDate = statements[i + 1].CutoffDate;
                }

                var stmtTransactions = allTransactions
                    .Where(t => t.CreatedAt > startDate && t.CreatedAt <= currentStmt.CutoffDate)
                    .Select(t => new CreditCardTransactionDto
                    {
                        Id = t.Id,
                        Description = t.Description,
                        Amount = t.Amount,
                        CreatedAt = t.CreatedAt
                    }).ToList();

                dtos.Add(new CreditCardStatementDto
                {
                    Id = currentStmt.Id,
                    PeriodName = currentStmt.PeriodName,
                    PeriodDebt = currentStmt.PeriodDebt,
                    MinimumPayment = currentStmt.MinimumPayment,
                    PaidAmount = currentStmt.PaidAmount,
                    CutoffDate = currentStmt.CutoffDate,
                    DueDate = currentStmt.DueDate,
                    IsPaid = currentStmt.IsPaid,
                    Transactions = stmtTransactions
                });
            }

            return ServiceResult<List<CreditCardStatementDto>>.Success(dtos);
        }

        public async Task<ServiceResult<bool>> PayCreditCardDebtAsync(Guid userId, Guid cardId, PayCreditCardDebtDto payRequest)
        {
            if (payRequest.Amount <= 0)
            {
                return ServiceResult<bool>.Failure("InvalidAmount", "Amount must be greater than zero.");
            }

            var card = await _context.CreditCards.FirstOrDefaultAsync(cc => cc.Id == cardId && cc.UserId == userId);
            if (card == null)
            {
                return ServiceResult<bool>.Failure("CreditCardNotFound", "Credit card not found.");
            }

            var sourceAccount = await _context.Accounts.FirstOrDefaultAsync(a => a.AccountNumber == payRequest.SourceAccountNumber && a.UserId == userId);
            if (sourceAccount == null)
            {
                return ServiceResult<bool>.Failure("SourceAccountNotFound", "Source account not found.");
            }

            if (sourceAccount.Currency != "TRY")
            {
                return ServiceResult<bool>.Failure("CurrencyMismatch", "Only TRY accounts can be used to pay credit card debt.");
            }

            if (sourceAccount.Balance < payRequest.Amount)
            {
                return ServiceResult<bool>.Failure("InsufficientFunds", "Insufficient funds in the source account.");
            }

            using var dbTransaction = await _context.Database.BeginTransactionAsync();
            try
            {
                sourceAccount.Balance -= payRequest.Amount;

                var debtToPay = Math.Min(card.CurrentDebt, payRequest.Amount);
                card.CurrentDebt -= debtToPay;

                var accountTx = new Transaction
                {
                    SourceAccountId = sourceAccount.Id,
                    DestinationAccountId = null,
                    Amount = payRequest.Amount,
                    Description = $"Kredi Kartı Borç Ödeme - Kart: *{(Core.Common.EncryptionHelper.Decrypt(card.EncryptedCardNumber).Length > 4 ? Core.Common.EncryptionHelper.Decrypt(card.EncryptedCardNumber).Substring(12) : "****")}",
                    Type = TransactionType.Transfer,
                    Category = "Fatura",
                    CreatedAt = DateTime.UtcNow
                };
                _context.Transactions.Add(accountTx);

                var unpaidStatement = await _context.CreditCardStatements
                    .Where(s => s.CreditCardId == cardId && !s.IsPaid)
                    .OrderBy(s => s.DueDate)
                    .FirstOrDefaultAsync();

                if (unpaidStatement != null)
                {
                    unpaidStatement.PaidAmount += payRequest.Amount;
                    if (unpaidStatement.PaidAmount >= unpaidStatement.PeriodDebt)
                    {
                        unpaidStatement.IsPaid = true;
                    }
                }

                await _context.SaveChangesAsync();
                await dbTransaction.CommitAsync();

                return ServiceResult<bool>.Success(true);
            }
            catch (Exception ex)
            {
                await dbTransaction.RollbackAsync();
                return ServiceResult<bool>.Failure("PaymentFailed", $"Payment failed: {ex.Message}");
            }
        }

        public async Task<ServiceResult<CreditCardStatementDto>> AdvanceStatementPeriodAsync(Guid userId, Guid cardId)
        {
            var card = await _context.CreditCards
                .Include(cc => cc.Statements)
                .FirstOrDefaultAsync(cc => cc.Id == cardId && cc.UserId == userId);

            if (card == null)
            {
                return ServiceResult<CreditCardStatementDto>.Failure("CreditCardNotFound", "Credit card not found.");
            }

            var latestStatement = card.Statements
                .OrderByDescending(s => s.CutoffDate)
                .FirstOrDefault();
            decimal unpaidBalance = 0.00m;
            decimal interest = 0.00m;

            // Check for Autopay Standing Order
            var autopayOrder = await _context.StandingOrders
                .FirstOrDefaultAsync(so => so.UserId == userId && so.CreditCardId == cardId && so.IsActive && so.OrderType == "CreditCardAutoPay");

            if (autopayOrder != null && latestStatement != null && !latestStatement.IsPaid)
            {
                var sourceAccount = await _context.Accounts
                    .FirstOrDefaultAsync(a => a.AccountNumber == autopayOrder.SourceAccountNumber && a.UserId == userId);

                if (sourceAccount != null)
                {
                    decimal amountToPay = latestStatement.PeriodDebt - latestStatement.PaidAmount;
                    if (amountToPay > 0)
                    {
                        if (sourceAccount.Balance >= amountToPay)
                        {
                            sourceAccount.Balance -= amountToPay;
                            latestStatement.PaidAmount += amountToPay;
                            latestStatement.IsPaid = true;
                            card.CurrentDebt -= amountToPay;

                            var tx = new Transaction
                            {
                                SourceAccountId = sourceAccount.Id,
                                Amount = amountToPay,
                                Description = $"Kredi Kartı Otomatik Borç Ödeme ({latestStatement.PeriodName})",
                                Type = TransactionType.Withdrawal,
                                Category = "Fatura"
                            };
                            _context.Transactions.Add(tx);
                        }
                        else if (sourceAccount.Balance > 0)
                        {
                            decimal partialAmount = sourceAccount.Balance;
                            sourceAccount.Balance = 0;
                            latestStatement.PaidAmount += partialAmount;
                            card.CurrentDebt -= partialAmount;

                            var tx = new Transaction
                            {
                                SourceAccountId = sourceAccount.Id,
                                Amount = partialAmount,
                                Description = $"Kredi Kartı Otomatik Borç Ödeme - Kısmi ({latestStatement.PeriodName})",
                                Type = TransactionType.Withdrawal,
                                Category = "Fatura"
                            };
                            _context.Transactions.Add(tx);
                        }
                    }
                }
            }

            if (latestStatement != null)
            {
                if (!latestStatement.IsPaid)
                {
                    var paid = latestStatement.PaidAmount;
                    var debt = latestStatement.PeriodDebt;
                    var min = latestStatement.MinimumPayment;

                    if (paid >= debt)
                    {
                        latestStatement.IsPaid = true;
                    }
                    else
                    {
                        unpaidBalance = debt - paid;

                        if (paid >= min)
                        {
                            interest = unpaidBalance * 0.0425m;
                        }
                        else
                        {
                            var unpaidMin = min - paid;
                            var lateInterest = unpaidMin * 0.05m;
                            var regularInterest = (unpaidBalance - unpaidMin) * 0.0425m;
                            interest = lateInterest + regularInterest;
                        }

                        interest = Math.Round(interest, 2);
                        card.CurrentDebt += interest;
                        
                        var interestTx = new CreditCardTransaction
                        {
                            CreditCardId = card.Id,
                            Description = $"Gecikme/Akdi Faiz Yansıması ({latestStatement.PeriodName})",
                            Amount = interest,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.CreditCardTransactions.Add(interestTx);
                    }
                    
                    latestStatement.IsPaid = true; 
                }
            }

            string nextPeriodName = "Temmuz 2026";
            DateTime nextCutoff = DateTime.UtcNow.AddMonths(1);
            DateTime nextDue = nextCutoff.AddDays(10);

            if (latestStatement != null)
            {
                nextCutoff = latestStatement.CutoffDate.AddMonths(1);
                nextDue = nextCutoff.AddDays(10);

                var parts = latestStatement.PeriodName.Split(' ');
                if (parts.Length == 2)
                {
                    var month = parts[0];
                    if (int.TryParse(parts[1], out var year))
                    {
                        var months = new List<string> { "Ocak", "Şubat", "Mart", "Nisan", "Mayıs", "Haziran", "Temmuz", "Ağustos", "Eylül", "Ekim", "Kasım", "Aralık" };
                        var idx = months.IndexOf(month);
                        if (idx != -1)
                        {
                            idx = (idx + 1) % 12;
                            if (idx == 0) year++;
                            nextPeriodName = $"{months[idx]} {year}";
                        }
                    }
                }
            }

            var newStatement = new CreditCardStatement
            {
                CreditCardId = card.Id,
                PeriodName = nextPeriodName,
                PeriodDebt = card.CurrentDebt,
                MinimumPayment = Math.Round(card.CurrentDebt * 0.30m, 2),
                PaidAmount = 0.00m,
                CutoffDate = nextCutoff,
                DueDate = nextDue,
                IsPaid = card.CurrentDebt <= 0m
            };

            _context.CreditCardStatements.Add(newStatement);
            await _context.SaveChangesAsync();

            var dto = new CreditCardStatementDto
            {
                Id = newStatement.Id,
                PeriodName = newStatement.PeriodName,
                PeriodDebt = newStatement.PeriodDebt,
                MinimumPayment = newStatement.MinimumPayment,
                PaidAmount = newStatement.PaidAmount,
                CutoffDate = newStatement.CutoffDate,
                DueDate = newStatement.DueDate,
                IsPaid = newStatement.IsPaid,
                Transactions = new List<CreditCardTransactionDto>()
            };

            return ServiceResult<CreditCardStatementDto>.Success(dto);
        }

        private string GenerateAccountNumber()
        {
            var random = new Random();
            var sb = new System.Text.StringBuilder("TR");
            for (int i = 0; i < 16; i++)
            {
                sb.Append(random.Next(0, 10));
            }
            return sb.ToString();
        }

        private string GenerateCardNumber()
        {
            var random = new Random();
            var sb = new System.Text.StringBuilder("4");
            for (int i = 0; i < 15; i++)
            {
                sb.Append(random.Next(0, 10));
            }
            return sb.ToString();
        }

        private string GenerateCvv()
        {
            var random = new Random();
            return random.Next(100, 1000).ToString();
        }

        public async Task<ServiceResult<bool>> DeleteAccountAsync(Guid userId, Guid accountId, Guid? transferTargetAccountId = null)
        {
            var account = await _context.Accounts
                .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == userId);

            if (account == null)
            {
                return ServiceResult<bool>.Failure("AccountNotFound", "Hesap bulunamadı.");
            }

            var totalAccountsCount = await _context.Accounts.CountAsync(a => a.UserId == userId);
            if (totalAccountsCount <= 1)
            {
                return ServiceResult<bool>.Failure("CannotDeleteLastAccount", "Daima en az bir aktif hesabınız bulunmalıdır.");
            }

            // Transfer balance if balance > 0
            if (account.Balance > 0)
            {
                if (transferTargetAccountId == null || transferTargetAccountId == Guid.Empty)
                {
                    return ServiceResult<bool>.Failure("TargetAccountRequired", "Hesapta bakiye bulunmaktadır. Silmeden önce bakiyenizi aktarmak istediğiniz hesabı seçmelisiniz.");
                }

                var targetAccount = await _context.Accounts.FirstOrDefaultAsync(a => a.Id == transferTargetAccountId && a.UserId == userId);
                if (targetAccount == null)
                {
                    return ServiceResult<bool>.Failure("TargetAccountNotFound", "Hedef hesap bulunamadı.");
                }

                decimal convertedAmount = account.Balance;
                if (account.Currency != targetAccount.Currency)
                {
                    var rates = await _marketRateService.GetRatesAsync();
                    if (rates != null && rates.Any())
                    {
                        
                        // Convert source currency to TRY
                        decimal tryAmount = account.Balance;
                        if (account.Currency != "TRY")
                        {
                            var srcRate = rates.FirstOrDefault(r => r.Code == account.Currency);
                            if (srcRate != null) tryAmount = account.Balance * srcRate.Buy;
                        }

                        // Convert TRY to target currency
                        if (targetAccount.Currency == "TRY")
                        {
                            convertedAmount = tryAmount;
                        }
                        else
                        {
                            var destRate = rates.FirstOrDefault(r => r.Code == targetAccount.Currency);
                            if (destRate != null && destRate.Sell > 0) convertedAmount = tryAmount / destRate.Sell;
                        }
                    }
                }

                targetAccount.Balance += convertedAmount;

                // Add transfer transaction
                var tx = new Transaction
                {
                    SourceAccountId = accountId,
                    DestinationAccountId = targetAccount.Id,
                    Amount = account.Balance,
                    Description = $"Hesap Kapatma Bakiye Aktarımı ({account.Currency} -> {targetAccount.Currency})",
                    Type = TransactionType.Transfer,
                    Category = "Yatırım",
                    CreatedAt = DateTime.UtcNow
                };
                _context.Transactions.Add(tx);
            }

            var sentTx = await _context.Transactions.Where(t => t.SourceAccountId == accountId).ToListAsync();
            foreach (var t in sentTx) t.SourceAccountId = null;

            var receivedTx = await _context.Transactions.Where(t => t.DestinationAccountId == accountId).ToListAsync();
            foreach (var t in receivedTx) t.DestinationAccountId = null;

            _context.Accounts.Remove(account);
            await _context.SaveChangesAsync();

            // Write Audit Log
            var audit = new AuditLog
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Action = "DeleteAccount",
                Details = $"Deleted account ID: {accountId}. AccountNumber: {account.AccountNumber}",
                IpAddress = "127.0.0.1",
                CreatedAt = DateTime.UtcNow
            };
            _context.AuditLogs.Add(audit);
            await _context.SaveChangesAsync();

            return ServiceResult<bool>.Success(true);
        }

        public async Task<ServiceResult<List<SavedContactDto>>> GetSavedContactsAsync(Guid userId)
        {
            var contacts = await _context.SavedContacts
                .Where(sc => sc.UserId == userId)
                .OrderByDescending(sc => sc.CreatedAt)
                .Select(sc => new SavedContactDto
                {
                    Id = sc.Id,
                    AccountNumber = sc.AccountNumber,
                    Alias = sc.Alias,
                    CreatedAt = sc.CreatedAt
                })
                .ToListAsync();

            return ServiceResult<List<SavedContactDto>>.Success(contacts);
        }

        public async Task<ServiceResult<SavedContactDto>> SaveContactAsync(Guid userId, CreateSavedContactDto contactDto)
        {
            var existing = await _context.SavedContacts
                .FirstOrDefaultAsync(sc => sc.UserId == userId && sc.AccountNumber == contactDto.AccountNumber);

            if (existing != null)
            {
                existing.Alias = contactDto.Alias;
                await _context.SaveChangesAsync();
                return ServiceResult<SavedContactDto>.Success(new SavedContactDto
                {
                    Id = existing.Id,
                    AccountNumber = existing.AccountNumber,
                    Alias = existing.Alias,
                    CreatedAt = existing.CreatedAt
                });
            }

            var contact = new SavedContact
            {
                UserId = userId,
                AccountNumber = contactDto.AccountNumber,
                Alias = contactDto.Alias
            };

            _context.SavedContacts.Add(contact);
            await _context.SaveChangesAsync();

            return ServiceResult<SavedContactDto>.Success(new SavedContactDto
            {
                Id = contact.Id,
                AccountNumber = contact.AccountNumber,
                Alias = contact.Alias,
                CreatedAt = contact.CreatedAt
            });
        }

        public async Task<ServiceResult<bool>> DeleteContactAsync(Guid userId, Guid contactId)
        {
            var contact = await _context.SavedContacts
                .FirstOrDefaultAsync(sc => sc.Id == contactId && sc.UserId == userId);

            if (contact == null)
            {
                return ServiceResult<bool>.Failure("ContactNotFound", "Kayıtlı alıcı bulunamadı.");
            }

            _context.SavedContacts.Remove(contact);
            await _context.SaveChangesAsync();

            return ServiceResult<bool>.Success(true);
        }

        public async Task<ServiceResult<List<StandingOrderDto>>> GetStandingOrdersAsync(Guid userId)
        {
            var orders = await _context.StandingOrders
                .Include(so => so.CreditCard)
                .Where(so => so.UserId == userId)
                .OrderByDescending(so => so.CreatedAt)
                .Select(so => new StandingOrderDto
                {
                    Id = so.Id,
                    SourceAccountNumber = so.SourceAccountNumber,
                    DestinationAccountNumber = so.DestinationAccountNumber,
                    Amount = so.Amount,
                    Frequency = so.Frequency,
                    MaturityDate = so.MaturityDate,
                    NextExecutionDate = so.NextExecutionDate,
                    IsActive = so.IsActive,
                    OrderType = so.OrderType,
                    CreditCardId = so.CreditCardId,
                    CreditCardNumber = so.CreditCard != null ? so.CreditCard.EncryptedCardNumber : null,
                    CreatedAt = so.CreatedAt
                })
                .ToListAsync();

            return ServiceResult<List<StandingOrderDto>>.Success(orders);
        }

        public async Task<ServiceResult<StandingOrderDto>> CreateStandingOrderAsync(Guid userId, CreateStandingOrderDto orderDto)
        {
            var sourceAcc = await _context.Accounts
                .AnyAsync(a => a.AccountNumber == orderDto.SourceAccountNumber && a.UserId == userId);
            if (!sourceAcc)
            {
                return ServiceResult<StandingOrderDto>.Failure("SourceAccountNotFound", "Kaynak hesap bulunamadı.");
            }

            var order = new StandingOrder
            {
                UserId = userId,
                SourceAccountNumber = orderDto.SourceAccountNumber,
                DestinationAccountNumber = orderDto.DestinationAccountNumber,
                Amount = orderDto.Amount,
                Frequency = orderDto.Frequency,
                OrderType = orderDto.OrderType,
                CreditCardId = orderDto.CreditCardId,
                MaturityDate = DateTime.UtcNow.AddYears(1),
                NextExecutionDate = DateTime.UtcNow
            };

            _context.StandingOrders.Add(order);
            await _context.SaveChangesAsync();

            return ServiceResult<StandingOrderDto>.Success(new StandingOrderDto
            {
                Id = order.Id,
                SourceAccountNumber = order.SourceAccountNumber,
                DestinationAccountNumber = order.DestinationAccountNumber,
                Amount = order.Amount,
                Frequency = order.Frequency,
                MaturityDate = order.MaturityDate,
                NextExecutionDate = order.NextExecutionDate,
                IsActive = order.IsActive,
                OrderType = order.OrderType,
                CreditCardId = order.CreditCardId,
                CreatedAt = order.CreatedAt
            });
        }

        public async Task<ServiceResult<bool>> DeleteStandingOrderAsync(Guid userId, Guid orderId)
        {
            var order = await _context.StandingOrders
                .FirstOrDefaultAsync(so => so.Id == orderId && so.UserId == userId);

            if (order == null)
            {
                return ServiceResult<bool>.Failure("OrderNotFound", "Talimat bulunamadı.");
            }

            _context.StandingOrders.Remove(order);
            await _context.SaveChangesAsync();

            return ServiceResult<bool>.Success(true);
        }

        public async Task<ServiceResult<TransactionDto>> ExchangeMoneyAsync(Guid userId, ExchangeDto exchangeDto)
        {
            if (!Guid.TryParse(exchangeDto.SourceAccountId, out var sourceAccountId))
            {
                return ServiceResult<TransactionDto>.Failure("InvalidSourceAccount", "Geçersiz kaynak hesap.");
            }

            var sourceAcc = await _context.Accounts
                .FirstOrDefaultAsync(a => a.Id == sourceAccountId && a.UserId == userId);

            if (sourceAcc == null)
            {
                return ServiceResult<TransactionDto>.Failure("AccountNotFound", "Kaynak hesap bulunamadı.");
            }

            bool isBuy = exchangeDto.Action.Equals("buy", StringComparison.OrdinalIgnoreCase);
            var asset = exchangeDto.Asset;

            Account? targetAcc = null;
            if (isBuy)
            {
                if (sourceAcc.Currency != "TRY")
                {
                    return ServiceResult<TransactionDto>.Failure("InvalidExchangeSource", "Alış işlemi için kaynak hesap TL olmalıdır.");
                }

                targetAcc = await _context.Accounts
                    .FirstOrDefaultAsync(a => a.UserId == userId && a.Currency == asset && a.AccountType == "DemandDeposit");

                if (targetAcc == null)
                {
                    var openResult = await CreateAccountAsync(userId, asset, "DemandDeposit");
                    if (!openResult.IsSuccess)
                    {
                        return ServiceResult<TransactionDto>.Failure("FailedToOpenAssetAccount", "Döviz/metal hesabı otomatik açılamadı.");
                    }
                    targetAcc = await _context.Accounts.FirstOrDefaultAsync(a => a.AccountNumber == openResult.Data!.AccountNumber);
                }
            }
            else
            {
                if (sourceAcc.Currency != asset)
                {
                    return ServiceResult<TransactionDto>.Failure("InvalidExchangeSource", $"Satış işlemi için kaynak hesap {asset} olmalıdır.");
                }

                targetAcc = await _context.Accounts
                    .FirstOrDefaultAsync(a => a.UserId == userId && a.Currency == "TRY" && a.AccountType == "DemandDeposit");

                if (targetAcc == null)
                {
                    return ServiceResult<TransactionDto>.Failure("TryAccountNotFound", "Satış bedelinin aktarılacağı vadesiz TL hesabınız bulunamadı.");
                }
            }

            if (targetAcc == null)
            {
                return ServiceResult<TransactionDto>.Failure("TargetAccountNotFound", "Hedef hesap bulunamadı.");
            }

            var rateInfo = await _marketRateService.GetRateByCodeAsync(asset);
            if (rateInfo == null)
            {
                return ServiceResult<TransactionDto>.Failure("RateNotFound", "Kur bilgisi bulunamadı.");
            }

            decimal rate = isBuy ? rateInfo.Sell : rateInfo.Buy;
            decimal tryCost = exchangeDto.Amount * rate;

            if (isBuy)
            {
                if (sourceAcc.Balance < tryCost)
                {
                    return ServiceResult<TransactionDto>.Failure("InsufficientFunds", "Yetersiz bakiye.");
                }

                sourceAcc.Balance -= tryCost;
                targetAcc.Balance += exchangeDto.Amount;
            }
            else
            {
                if (sourceAcc.Balance < exchangeDto.Amount)
                {
                    return ServiceResult<TransactionDto>.Failure("InsufficientFunds", $"Yetersiz {asset} bakiyesi.");
                }

                sourceAcc.Balance -= exchangeDto.Amount;
                targetAcc.Balance += tryCost;
            }

            var transaction = new Transaction
            {
                SourceAccountId = sourceAcc.Id,
                DestinationAccountId = targetAcc.Id,
                Amount = isBuy ? tryCost : exchangeDto.Amount,
                Description = isBuy 
                    ? $"{exchangeDto.Amount} {asset} Alımı (Kur: {rate} TRY)"
                    : $"{exchangeDto.Amount} {asset} Satışı (Kur: {rate} TRY)",
                Type = TransactionType.Transfer,
                Category = "Yatırım",
                CreatedAt = DateTime.UtcNow
            };

            _context.Transactions.Add(transaction);
            await _context.SaveChangesAsync();

            // Write Audit Log
            var audit = new AuditLog
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Action = isBuy ? "ExchangeBuy" : "ExchangeSell",
                Details = isBuy 
                    ? $"Bought {exchangeDto.Amount} {asset} with {tryCost} TRY. Rate: {rate}"
                    : $"Sold {exchangeDto.Amount} {asset} for {tryCost} TRY. Rate: {rate}",
                IpAddress = "127.0.0.1",
                CreatedAt = DateTime.UtcNow
            };
            _context.AuditLogs.Add(audit);
            await _context.SaveChangesAsync();

            var sourceUser = await _context.Users.FindAsync(sourceAcc.UserId);
            var targetUser = await _context.Users.FindAsync(targetAcc.UserId);

            return ServiceResult<TransactionDto>.Success(new TransactionDto
            {
                Id = transaction.Id,
                SourceAccountNumber = sourceAcc.AccountNumber,
                DestinationAccountNumber = targetAcc.AccountNumber,
                SourceAccountOwnerName = sourceUser?.FullName,
                DestinationAccountOwnerName = targetUser?.FullName,
                Amount = transaction.Amount,
                Description = transaction.Description,
                Type = transaction.Type.ToString(),
                Category = transaction.Category,
                CreatedAt = transaction.CreatedAt
            });
        }

        public async Task<ServiceResult<CreditCardDto>> CreateCreditCardAsync(Guid userId)
        {
            var existingCardsCount = await _context.CreditCards.CountAsync(cc => cc.UserId == userId);
            if (existingCardsCount >= 1)
            {
                return ServiceResult<CreditCardDto>.Failure("MaxCreditCardsLimitReached", "En fazla 1 adet kredi kartı sahibi olabilirsiniz.");
            }

            var random = new Random();
            string cardNumber = "4" + string.Join("", Enumerable.Range(0, 15).Select(_ => random.Next(0, 10).ToString()));
            while (await _context.CreditCards.AnyAsync(cc => cc.EncryptedCardNumber == Core.Common.EncryptionHelper.Encrypt(cardNumber)))
            {
                cardNumber = "4" + string.Join("", Enumerable.Range(0, 15).Select(_ => random.Next(0, 10).ToString()));
            }

            string cvv = random.Next(100, 1000).ToString();
            string expiryDate = DateTime.UtcNow.AddYears(8).ToString("MM/yy");

            var creditCard = new CreditCard
            {
                UserId = userId,
                EncryptedCardNumber = Core.Common.EncryptionHelper.Encrypt(cardNumber),
                EncryptedCardCvv = Core.Common.EncryptionHelper.Encrypt(cvv),
                ExpiryDate = expiryDate,
                CardLimit = 10000.00m,
                CurrentDebt = 0.00m,
                CardTheme = "theme-neon-blue"
            };

            creditCard.Statements.Add(new CreditCardStatement
            {
                PeriodName = DateTime.UtcNow.ToString("MMMM yyyy", new System.Globalization.CultureInfo("tr-TR")),
                PeriodDebt = 0.00m,
                MinimumPayment = 0.00m,
                PaidAmount = 0.00m,
                CutoffDate = DateTime.UtcNow.AddDays(30),
                DueDate = DateTime.UtcNow.AddDays(40),
                IsPaid = true
            });

            _context.CreditCards.Add(creditCard);
            await _context.SaveChangesAsync();

            var dto = new CreditCardDto
            {
                Id = creditCard.Id,
                CardNumber = cardNumber,
                CardCvv = cvv,
                ExpiryDate = creditCard.ExpiryDate,
                CardLimit = creditCard.CardLimit,
                CurrentDebt = creditCard.CurrentDebt,
                AvailableLimit = creditCard.CardLimit,
                CardTheme = creditCard.CardTheme
            };

            return ServiceResult<CreditCardDto>.Success(dto);
        }

        private async Task Send2FaEmailAsync(string emailAddress, string username, string otpCode)
        {
            try
            {
                var smtpHost = _configuration["SmtpSettings:Host"] ?? "localhost";
                var smtpPortStr = _configuration["SmtpSettings:Port"] ?? "25";
                int.TryParse(smtpPortStr, out var smtpPort);
                var smtpUsername = _configuration["SmtpSettings:Username"] ?? "";
                var smtpPassword = _configuration["SmtpSettings:Password"] ?? "";
                var enableSsl = bool.Parse(_configuration["SmtpSettings:EnableSsl"] ?? "false");
                var fromAddress = _configuration["SmtpSettings:FromAddress"] ?? "no-reply@smartbank.com";

                using (var mail = new System.Net.Mail.MailMessage())
                {
                    mail.From = new System.Net.Mail.MailAddress(fromAddress, "SmartBank Güvenlik");
                    mail.To.Add(emailAddress);
                    mail.Subject = "SmartBank Güvenlik Doğrulama Kodu";
                    
                    mail.Body = $@"
                    <html>
                    <body style='font-family: Arial, sans-serif; background-color: #0d1b2a; color: #e0e1dd; padding: 2rem;'>
                        <div style='max-width: 600px; margin: 0 auto; background-color: #1b263b; border-radius: 12px; border: 1px solid #415a77; padding: 2rem;'>
                            <h2 style='color: #00f260; text-align: center; font-size: 1.8rem; margin-top: 0;'>❖ SmartBank Güvenlik</h2>
                            <p style='font-size: 1.1rem;'>Merhaba <strong>{username}</strong>,</p>
                            <p style='font-size: 1.1rem; line-height: 1.6;'>Hesabınızdan başlatılan para transferi işlemini onaylamak için aşağıdaki 6 haneli doğrulama kodunu kullanın:</p>
                            <div style='text-align: center; margin: 2rem 0;'>
                                <span style='font-size: 2.2rem; font-weight: bold; background-color: #0d1b2a; color: #00f260; padding: 0.75rem 2rem; border-radius: 8px; letter-spacing: 5px; border: 1px solid #415a77;'>{otpCode}</span>
                            </div>
                            <p style='color: #a3b18a; font-size: 0.9rem; line-height: 1.6;'>Bu kod 5 dakika boyunca geçerlidir. İşlemi siz başlatmadıysanız lütfen hemen müşteri hizmetlerimizle iletişime geçiniz.</p>
                            <hr style='border: 0; border-top: 1px solid #415a77; margin: 2rem 0;' />
                            <p style='font-size: 0.8rem; text-align: center; color: #a3b18a;'>SmartBank A.Ş. &copy; {DateTime.UtcNow.Year}</p>
                        </div>
                    </body>
                    </html>";
                    mail.IsBodyHtml = true;

                    using (var smtp = new System.Net.Mail.SmtpClient(smtpHost, smtpPort))
                    {
                        if (!string.IsNullOrEmpty(smtpUsername))
                        {
                            smtp.Credentials = new System.Net.NetworkCredential(smtpUsername, smtpPassword);
                        }
                        smtp.EnableSsl = enableSsl;
                        
                        await smtp.SendMailAsync(mail);
                    }
                }
                Console.WriteLine($"[SmartBank 2FA Email] Real verification email successfully sent to {emailAddress}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SmartBank 2FA Email Error] Failed to send email to {emailAddress}: {ex.Message}");
            }
        }

        public async Task<ServiceResult<TransactionDto>> DepositMoneyAsync(Guid userId, string accountNumber, decimal amount)
        {
            if (amount <= 0)
            {
                return ServiceResult<TransactionDto>.Failure("InvalidAmount", "Tutar 0'dan büyük olmalıdır.");
            }

            var account = await _context.Accounts
                .FirstOrDefaultAsync(a => a.AccountNumber == accountNumber && a.UserId == userId);

            if (account == null)
            {
                return ServiceResult<TransactionDto>.Failure("AccountNotFound", "Hesap bulunamadı.");
            }

            var user = await _context.Users.FindAsync(userId);
            var userName = user?.FullName ?? "SmartBank Müşterisi";

            account.Balance += amount;

            var transaction = new Transaction
            {
                Id = Guid.NewGuid(),
                SourceAccountId = account.Id,
                DestinationAccountId = account.Id,
                Type = TransactionType.Deposit,
                Amount = amount,
                Description = "Hesaba Para Yükleme",
                Category = "Diğer",
                CreatedAt = DateTime.UtcNow
            };

            _context.Transactions.Add(transaction);
            await _context.SaveChangesAsync();

            var transactionDto = new TransactionDto
            {
                Id = transaction.Id,
                SourceAccountNumber = accountNumber,
                DestinationAccountNumber = accountNumber,
                SourceAccountOwnerName = userName,
                DestinationAccountOwnerName = userName,
                Type = transaction.Type.ToString(),
                Amount = transaction.Amount,
                Description = transaction.Description,
                Category = transaction.Category,
                CreatedAt = transaction.CreatedAt
            };

            return ServiceResult<TransactionDto>.Success(transactionDto);
        }
    }
}
