using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;
using SmartBank.Core.Security;
using SmartBank.Infrastructure.Data;

namespace SmartBank.Infrastructure.Services
{
    /// <summary>
    /// Everything a customer does with money. The class is split by topic into partial files: accounts and transfers
    /// (this file), credit cards, exchange, standing orders and contacts. All of them share the rules below:
    /// money amounts have at most two decimals, computed amounts are rounded once with <see cref="Money.Round"/>, every
    /// change of a balance goes through the optimistic-concurrency retry, and "not found" and "not yours" look the same.
    /// </summary>
    public partial class BankingService : IBankingService
    {
        public const int MaxAccountsPerUser = 10;
        public const int MaxTransactionsPerRequest = 500;
        public const int DefaultTransactionsPerRequest = 200;

        // Transfer step-up rules (amounts in TRY, or the TRY value of another currency).
        private const int DuplicateWindowSeconds = 30;
        private const decimal FraudFloorTry = 500.00m;
        private const decimal NewAccountLimitTry = 2000.00m;
        private const decimal TwoFactorThresholdTry = 1000.00m;
        private const decimal FraudAverageMultiple = 5m;
        private static readonly TimeSpan AverageWindow = TimeSpan.FromDays(90);

        private readonly SmartBankDbContext _context;
        private readonly IOtpDelivery _otpDelivery;
        private readonly IClientInfo? _clientInfo;
        private readonly ILogger<BankingService> _logger;
        private readonly IMarketRateService _marketRateService;
        private readonly TimeProvider _time;

        public BankingService(SmartBankDbContext context, IOtpDelivery otpDelivery, IMarketRateService marketRateService,
            IClientInfo? clientInfo = null, ILogger<BankingService>? logger = null, TimeProvider? timeProvider = null)
        {
            _context = context;
            _otpDelivery = otpDelivery;
            _clientInfo = clientInfo;
            _logger = logger ?? NullLogger<BankingService>.Instance;
            _marketRateService = marketRateService;
            _time = timeProvider ?? TimeProvider.System;
        }

        private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

        private string ClientIp => _clientInfo?.IpAddress ?? "unknown";

        /// <summary>
        /// The canonical form of an account number typed by a person: trimmed and upper-case ("tr12..." is "TR12..."). PostgreSQL
        /// compares text case-sensitively and SQL Server does not, so without this the same input found an account on one and
        /// not on the other, and "tr1"/"TR1" got past the same-account check on SQL Server.
        /// </summary>
        internal static string NormalizeAccountNumber(string? accountNumber) => (accountNumber ?? string.Empty).Trim().ToUpperInvariant();

        private static ServiceResult<T> Fail<T>(string errorKey, string message) => ServiceResult<T>.Failure(errorKey, message);

        private AuditLog NewAudit(Guid userId, string action, string details) => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Action = action,
            Details = details,
            IpAddress = ClientIp,
            CreatedAt = UtcNow
        };

        /// <summary>Audit text with invariant number formatting, whatever the culture of the machine.</summary>
        private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

        /// <summary>An amount must be positive, at most <paramref name="max"/> and have at most two decimals.</summary>
        private static (string Key, string Message)? ValidateAmount(decimal amount, decimal max = Money.MaxAmount)
        {
            if (amount <= 0) return ("InvalidAmount", "Amount must be greater than zero.");
            if (amount > max) return ("InvalidAmount", $"Amount cannot exceed {Money.Format(max)}.");
            if (!Money.HasValidScale(amount)) return (Money.ScaleErrorKey, Money.ScaleMessage);
            return null;
        }

        // Display paths must not fail because one stored card cannot be decrypted (e.g. legacy rows from before v1.1).
        private static string SafeDecrypt(string? cipherText) =>
            EncryptionHelper.TryDecrypt(cipherText, out var plain) ? plain : string.Empty;

        private Task<ServiceResult<T>> RunWithConcurrencyRetryAsync<T>(Func<Task<ServiceResult<T>>> operation) =>
            ConcurrencyRetry.RunAsync(_context, operation, "The account was changed by another operation at the same time. Please try again.");

        // ---- accounts -----------------------------------------------------------------------------------------

        public async Task<ServiceResult<List<AccountDto>>> GetAccountsAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var accounts = await _context.Accounts
                .AsNoTracking()
                .Where(a => a.UserId == userId)
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                .ToListAsync(cancellationToken);

            return ServiceResult<List<AccountDto>>.Success(accounts.Select(a => BankingMappers.ToDto(a, SafeDecrypt(a.EncryptedCardNumber))).ToList());
        }

        public async Task<ServiceResult<List<TransactionDto>>> GetTransactionsAsync(Guid accountId, Guid userId, int take = DefaultTransactionsPerRequest, CancellationToken cancellationToken = default)
        {
            take = Math.Clamp(take, 1, MaxTransactionsPerRequest);

            // Someone else's account and an account that does not exist look the same.
            var owned = await _context.Accounts.AsNoTracking().AnyAsync(a => a.Id == accountId && a.UserId == userId, cancellationToken);
            if (!owned)
            {
                return Fail<List<TransactionDto>>("AccountNotFound", "Account not found.");
            }

            var transactions = await _context.Transactions
                .AsNoTracking()
                .Where(t => t.SourceAccountId == accountId || t.DestinationAccountId == accountId)
                .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
                .Take(take)
                .Select(t => new TransactionDto
                {
                    Id = t.Id,
                    SourceAccountNumber = t.SourceAccount != null ? t.SourceAccount.AccountNumber : null,
                    DestinationAccountNumber = t.DestinationAccount != null ? t.DestinationAccount.AccountNumber : null,
                    SourceCurrency = t.SourceAccount != null ? t.SourceAccount.Currency : null,
                    DestinationCurrency = t.DestinationAccount != null ? t.DestinationAccount.Currency : null,
                    SourceAccountOwnerName = t.SourceAccount != null && t.SourceAccount.User != null ? t.SourceAccount.User.FullName : null,
                    DestinationAccountOwnerName = t.DestinationAccount != null && t.DestinationAccount.User != null ? t.DestinationAccount.User.FullName : null,
                    Amount = t.Amount,
                    Description = t.Description,
                    Type = t.Type.ToString(),
                    Category = t.Category,
                    CreatedAt = t.CreatedAt
                })
                .ToListAsync(cancellationToken);

            foreach (var transaction in transactions) BankingMappers.WithSides(transaction);

            return ServiceResult<List<TransactionDto>>.Success(transactions);
        }

        public async Task<ServiceResult<AccountDto>> CreateAccountAsync(Guid userId, string currency, string accountType = AccountTypes.DemandDeposit)
        {
            var code = string.IsNullOrWhiteSpace(currency) ? Currencies.Try : Currencies.Normalize(currency);
            if (!Currencies.IsSupported(code))
            {
                return Fail<AccountDto>("InvalidCurrency", "Supported currencies are TRY, USD, EUR, XAU and XAG.");
            }

            var type = AccountTypes.Normalize(accountType);
            if (type == null)
            {
                return Fail<AccountDto>("InvalidAccountType", "Account type must be DemandDeposit or TimeDeposit.");
            }

            if (!await _context.Users.AnyAsync(u => u.Id == userId))
            {
                return Fail<AccountDto>("UserNotFound", "User details not found.");
            }

            if (await _context.Accounts.CountAsync(a => a.UserId == userId) >= MaxAccountsPerUser)
            {
                return Fail<AccountDto>("AccountLimitReached", $"You can have at most {MaxAccountsPerUser} accounts.");
            }

            var (account, cardNumber, cvv) = await BuildAccountAsync(userId, code!, type);
            _context.Accounts.Add(account);
            await _context.SaveChangesAsync();

            // The CVV and the full card number are shown to the customer once, in this response; the CVV is never stored.
            var dto = BankingMappers.ToDto(account, cardNumber);
            dto.CardCvv = cvv;
            return ServiceResult<AccountDto>.Success(dto);
        }

        /// <summary>A new, not yet saved account with a unique number and code, and its (not stored) card number and CVV.</summary>
        private async Task<(Account Account, string CardNumber, string Cvv)> BuildAccountAsync(Guid userId, string currency, string accountType)
        {
            var accountNumber = "TR" + SecureRandom.Digits(16);
            while (await _context.Accounts.AnyAsync(a => a.AccountNumber == accountNumber))
            {
                accountNumber = "TR" + SecureRandom.Digits(16);
            }

            var accountCode = "ACC-" + SecureRandom.Next(1000000, 10000000);
            while (await _context.Accounts.AnyAsync(a => a.AccountCode == accountCode))
            {
                accountCode = "ACC-" + SecureRandom.Next(1000000, 10000000);
            }

            var now = UtcNow;
            var cardNumber = GenerateCardNumber();
            var account = new Account
            {
                UserId = userId,
                AccountNumber = accountNumber,
                AccountCode = accountCode,
                Balance = 0.00m,
                Currency = currency,
                CreatedAt = now,
                EncryptedCardNumber = EncryptionHelper.Encrypt(cardNumber),
                CardTheme = "theme-neon-blue",
                ExpiryDate = CardFormat.ExpiryIn(now, CreditCardRules.DefaultCardValidityYears),
                AccountType = accountType
            };

            if (accountType == AccountTypes.TimeDeposit)
            {
                // Display values only: nothing accrues this interest and nothing enforces the maturity date.
                account.InterestRate = 48.00m;
                account.MaturityDate = now.AddDays(30);
            }

            return (account, cardNumber, GenerateCvv());
        }

        private static string GenerateCardNumber() => "4" + SecureRandom.Digits(15);

        private static string GenerateCvv() => SecureRandom.Next(100, 1000).ToString(CultureInfo.InvariantCulture);

        public Task<ServiceResult<TransactionDto>> DepositMoneyAsync(Guid userId, string accountNumber, decimal amount) =>
            RunWithConcurrencyRetryAsync(() => DepositMoneyCoreAsync(userId, accountNumber, amount));

        // A demo faucet: a real bank has nothing like it. It stays (see the README), but it obeys the same amount rules.
        private async Task<ServiceResult<TransactionDto>> DepositMoneyCoreAsync(Guid userId, string accountNumber, decimal amount)
        {
            var invalid = ValidateAmount(amount);
            if (invalid != null) return Fail<TransactionDto>(invalid.Value.Key, invalid.Value.Message);

            accountNumber = NormalizeAccountNumber(accountNumber);
            var account = await _context.Accounts.FirstOrDefaultAsync(a => a.AccountNumber == accountNumber && a.UserId == userId);
            if (account == null)
            {
                return Fail<TransactionDto>("AccountNotFound", "Hesap bulunamadı.");
            }

            var userName = await _context.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.FullName).FirstOrDefaultAsync()
                           ?? "SmartBank Müşterisi";

            account.Balance += amount;

            var transaction = new Transaction
            {
                Id = Guid.NewGuid(),
                SourceAccountId = account.Id,
                DestinationAccountId = account.Id,
                Type = TransactionType.Deposit,
                Amount = amount,
                Description = "Hesaba Para Yükleme",
                Category = TransactionCategories.Other,
                CreatedAt = UtcNow
            };

            _context.Transactions.Add(transaction);
            await _context.SaveChangesAsync();

            return ServiceResult<TransactionDto>.Success(BankingMappers.WithSides(new TransactionDto
            {
                Id = transaction.Id,
                SourceAccountNumber = accountNumber,
                DestinationAccountNumber = accountNumber,
                SourceCurrency = account.Currency,
                DestinationCurrency = account.Currency,
                SourceAccountOwnerName = userName,
                DestinationAccountOwnerName = userName,
                Type = transaction.Type.ToString(),
                Amount = transaction.Amount,
                Description = transaction.Description,
                Category = transaction.Category,
                CreatedAt = transaction.CreatedAt
            }));
        }

        // ---- transfers ----------------------------------------------------------------------------------------

        public async Task<ServiceResult<TransactionDto>> TransferMoneyAsync(Guid userId, TransferRequestDto transferRequest)
        {
            var invalid = ValidateAmount(transferRequest.Amount);
            if (invalid != null) return Fail<TransactionDto>(invalid.Value.Key, invalid.Value.Message);

            var sourceNumber = NormalizeAccountNumber(transferRequest.SourceAccountNumber);
            var destinationNumber = NormalizeAccountNumber(transferRequest.DestinationAccountNumber);
            transferRequest.SourceAccountNumber = sourceNumber;
            transferRequest.DestinationAccountNumber = destinationNumber;

            if (string.Equals(sourceNumber, destinationNumber, StringComparison.Ordinal))
            {
                return Fail<TransactionDto>("CannotTransferToSelf", "Cannot transfer money to the same account.");
            }

            // 1. The source must exist and belong to the caller. "Not found" and "not yours" give the same answer, so this
            //    cannot be used to find out which account numbers exist.
            var source = await _context.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.AccountNumber == sourceNumber);
            if (source == null || source.UserId != userId)
            {
                return Fail<TransactionDto>("SourceAccountNotFound", "Source account was not found.");
            }

            var destination = await _context.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.AccountNumber == destinationNumber);
            if (destination == null)
            {
                return Fail<TransactionDto>("DestinationAccountNotFound", "Destination account was not found.");
            }

            // Multi-currency transfers are not supported: moving between currencies is what the exchange is for.
            if (source.Currency != destination.Currency)
            {
                return Fail<TransactionDto>("CurrencyMismatch", "Currency exchange transfers are not supported in this version.");
            }

            if (source.Balance < transferRequest.Amount)
            {
                return Fail<TransactionDto>("InsufficientFunds", "Insufficient funds in the source account.");
            }

            // The code only approves the exact transfer it was issued for (same accounts, same amount).
            var binding = OtpManager.TransferBinding(source.AccountNumber, destination.AccountNumber, transferRequest.Amount);
            var approvedByCode = false;

            if (!string.IsNullOrEmpty(transferRequest.OtpCode))
            {
                var check = await VerifyOtpAsync(userId, OtpPurpose.Transfer, transferRequest.OtpCode, binding);
                if (check == null)
                {
                    return Fail<TransactionDto>("UserNotFound", "User details not found.");
                }

                if (check == OtpCheckResult.TooManyAttempts)
                {
                    return Fail<TransactionDto>("TooManyOtpAttempts", "Too many wrong codes. Start the transfer again to get a new code.");
                }

                if (check != OtpCheckResult.Valid)
                {
                    return Fail<TransactionDto>("InvalidOtpCode", "Invalid or expired verification code.");
                }

                approvedByCode = true; // the user proved it with a code, so the checks below are skipped
            }

            if (!approvedByCode)
            {
                var challenge = await CheckStepUpAsync(userId, source, destination, transferRequest.Amount);
                if (challenge != null)
                {
                    // One-time code, valid for 5 minutes, bound to this exact transfer.
                    var issued = await IssueOtpAsync(userId, OtpPurpose.Transfer, binding);
                    if (issued == null)
                    {
                        return Fail<TransactionDto>("UserNotFound", "User details not found.");
                    }

                    var detail = $"{Money.Format(transferRequest.Amount)} {source.Currency} -> {destination.AccountNumber}";
                    _otpDelivery.Send(issued.Value.User, issued.Value.Code, OtpPurpose.Transfer, detail);

                    var message = challenge.Value.Message;
                    if (_otpDelivery.ExposeCodeInResponse)
                    {
                        message += $"|OTP:{issued.Value.Code}"; // demo mode only, see IOtpDelivery.ExposeCodeInResponse
                    }

                    return Fail<TransactionDto>(challenge.Value.Key, message);
                }
            }

            // Everything above only decides whether this transfer is allowed. The money itself moves below, in a
            // step that is safe to repeat if another request touches the same accounts at the same moment.
            return await RunWithConcurrencyRetryAsync(() => ExecuteTransferAsync(userId, transferRequest));
        }

        /// <summary>
        /// Should this transfer be held until the customer enters a one-time code? Rules: a repeat of the same transfer
        /// within 30 seconds; an amount far above the usual outgoing transfer (or above 2000 TRY on an account that has none);
        /// any amount above 1000 TRY for a customer who turned two-factor on. Amounts in another currency are compared by
        /// their TRY value; when that value cannot be trusted (no live rate) the amount counts as above every limit.
        /// </summary>
        private async Task<(string Key, string Message)?> CheckStepUpAsync(Guid userId, Account source, Account destination, decimal amount)
        {
            var now = UtcNow;
            var tryAmount = await ToTryEquivalentAsync(amount, source.Currency);

            // Rule A: the same transfer again within 30 seconds.
            var duplicateSince = now.AddSeconds(-DuplicateWindowSeconds);
            var isDuplicate = await _context.Transactions.AsNoTracking()
                .AnyAsync(t => t.SourceAccountId == source.Id &&
                               t.DestinationAccountId == destination.Id &&
                               t.Amount == amount &&
                               t.CreatedAt >= duplicateSince);
            if (isDuplicate)
            {
                return ("SuspectedFraudDuplicate", "Şüpheli işlem: Son 30 saniye içerisinde aynı hesaba aynı miktarda transfer denemesi.");
            }

            // Rule B: far above the usual outgoing transfer. Only real outgoing transfers count: a deposit has the account on both
            // sides, and an exchange or a cross-currency closing transfer is booked as a Transfer between two accounts of the same
            // customer in DIFFERENT currencies (a real transfer never is), so those are left out by the currency test. They used
            // to drag the average up (one big deposit or one big purchase of dollars switched this rule off).
            var windowStart = now - AverageWindow;
            var average = await _context.Transactions.AsNoTracking()
                .Where(t => t.SourceAccountId == source.Id &&
                            t.Type == TransactionType.Transfer &&
                            t.DestinationAccountId != null &&
                            t.DestinationAccountId != t.SourceAccountId &&
                            t.SourceAccount != null && t.DestinationAccount != null &&
                            t.SourceAccount.Currency == t.DestinationAccount.Currency &&
                            t.CreatedAt >= windowStart)
                .Select(t => (decimal?)t.Amount)
                .AverageAsync();

            if (average.HasValue)
            {
                if (amount > FraudAverageMultiple * average.Value && (tryAmount == null || tryAmount > FraudFloorTry))
                {
                    return ("SuspectedFraudHighValue",
                        Text($"Şüpheli işlem: Transfer miktarı ortalama harcamanızın ({average.Value:F2} {source.Currency}) 5 katından fazla."));
                }
            }
            else if (tryAmount == null || tryAmount > NewAccountLimitTry)
            {
                return ("SuspectedFraudHighValue", "Şüpheli işlem: Yeni hesaplar için tek seferlik transfer limiti (2000 TRY) aşıldı.");
            }

            // Rule C: the customer asked for a second factor on larger transfers.
            var twoFactor = await _context.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TwoFactorEnabled).FirstOrDefaultAsync();
            if (twoFactor && (tryAmount == null || tryAmount > TwoFactorThresholdTry))
            {
                return ("Requires2FA", "Güvenlik doğrulaması: 1000 TRY üzerindeki transferler için doğrulama gerekiyor.");
            }

            return null;
        }

        /// <summary>The TRY value of an amount, or null when it cannot be determined from a live (non-stand-in) rate.</summary>
        private async Task<decimal?> ToTryEquivalentAsync(decimal amount, string currency)
        {
            if (currency == Currencies.Try) return amount;

            var rate = await _marketRateService.GetRateByCodeAsync(currency);
            if (rate == null || rate.IsFallback || rate.Sell <= 0m) return null;

            return amount * rate.Sell; // the higher side: when in doubt, the limit is reached sooner
        }

        /// <summary>
        /// Checks a one-time code and saves the outcome (a wrong guess is counted, a right code is used up). The user row
        /// carries a version, so two requests with the same code cannot both succeed and parallel wrong guesses cannot hide
        /// each other: the loser of a collision starts again from the fresh row. Null when the user does not exist.
        /// </summary>
        private async Task<OtpCheckResult?> VerifyOtpAsync(Guid userId, OtpPurpose purpose, string? code, string? binding)
        {
            for (var attempt = 1; attempt <= ConcurrencyRetry.MaxAttempts; attempt++)
            {
                _context.ChangeTracker.Clear();

                var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
                if (user == null) return null;

                var result = OtpManager.Verify(user, purpose, code, UtcNow, binding);

                try
                {
                    await _context.SaveChangesAsync(); // persists the failed-attempt counter, or the cleared code on success
                    return result;
                }
                catch (DbUpdateConcurrencyException)
                {
                    _context.ChangeTracker.Clear();
                    await Task.Delay(SecureRandom.Next(2, 20 * attempt));
                }
            }

            return OtpCheckResult.Invalid; // too many collisions: refuse rather than guess
        }

        private async Task<(string Code, User User)?> IssueOtpAsync(Guid userId, OtpPurpose purpose, string? binding)
        {
            for (var attempt = 1; attempt <= ConcurrencyRetry.MaxAttempts; attempt++)
            {
                _context.ChangeTracker.Clear();

                var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
                if (user == null) return null;

                var code = OtpManager.Issue(user, purpose, UtcNow, binding);

                try
                {
                    await _context.SaveChangesAsync();
                    return (code, user);
                }
                catch (DbUpdateConcurrencyException)
                {
                    _context.ChangeTracker.Clear();
                    await Task.Delay(SecureRandom.Next(2, 20 * attempt));
                }
            }

            return null;
        }

        private async Task<ServiceResult<TransactionDto>> ExecuteTransferAsync(Guid userId, TransferRequestDto transferRequest)
        {
            // Fresh reads on every attempt (the context was cleared first), so balances are current.
            var sourceAccount = await _context.Accounts.FirstOrDefaultAsync(a => a.AccountNumber == transferRequest.SourceAccountNumber);
            if (sourceAccount == null || sourceAccount.UserId != userId)
            {
                return Fail<TransactionDto>("SourceAccountNotFound", "Source account was not found.");
            }

            var destinationAccount = await _context.Accounts.FirstOrDefaultAsync(a => a.AccountNumber == transferRequest.DestinationAccountNumber);
            if (destinationAccount == null)
            {
                return Fail<TransactionDto>("DestinationAccountNotFound", "Destination account was not found.");
            }

            // The balance may have dropped since the first check, so it is checked again against what is stored now.
            if (sourceAccount.Balance < transferRequest.Amount)
            {
                return Fail<TransactionDto>("InsufficientFunds", "Insufficient funds in the source account.");
            }

            // Using a DB transaction to guarantee atomicity of the money transfer
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // Update balances. Both rows carry a version: if either was changed by someone else since the read
                // above, SaveChanges writes nothing and throws DbUpdateConcurrencyException.
                sourceAccount.Balance -= transferRequest.Amount;
                destinationAccount.Balance += transferRequest.Amount;

                var transaction = new Transaction
                {
                    SourceAccountId = sourceAccount.Id,
                    DestinationAccountId = destinationAccount.Id,
                    Amount = transferRequest.Amount,
                    Description = transferRequest.Description ?? string.Empty,
                    Type = TransactionType.Transfer,
                    Category = string.IsNullOrEmpty(transferRequest.Category) ? TransactionCategories.Other : transferRequest.Category,
                    CreatedAt = UtcNow
                };

                _context.Transactions.Add(transaction);
                _context.AuditLogs.Add(NewAudit(userId, "TransferMoney",
                    Text($"Transferred {Money.Format(transferRequest.Amount)} {sourceAccount.Currency} from {sourceAccount.AccountNumber} to {destinationAccount.AccountNumber}")));

                await _context.SaveChangesAsync();
                await dbTransaction.CommitAsync();

                return ServiceResult<TransactionDto>.Success(BankingMappers.WithSides(new TransactionDto
                {
                    Id = transaction.Id,
                    SourceAccountNumber = sourceAccount.AccountNumber,
                    DestinationAccountNumber = destinationAccount.AccountNumber,
                    SourceCurrency = sourceAccount.Currency,
                    DestinationCurrency = destinationAccount.Currency,
                    Amount = transaction.Amount,
                    Description = transaction.Description,
                    Type = transaction.Type.ToString(),
                    Category = transaction.Category,
                    CreatedAt = transaction.CreatedAt
                }));
            }
            catch (Exception ex) when (DatabaseConflict.IsRetryable(ex))
            {
                // Someone else got in the way (version conflict or deadlock victim): undo and let the retry loop start over.
                await dbTransaction.RollbackAsync();
                throw;
            }
            catch (Exception ex)
            {
                await dbTransaction.RollbackAsync();
                _logger.LogError(ex, "Transfer failed.");
                return Fail<TransactionDto>("TransactionFailed", "The transfer could not be completed. Please try again.");
            }
        }
    }
}
