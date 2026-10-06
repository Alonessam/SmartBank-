using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SmartBank.Core.Common;
using SmartBank.Core.Entities;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Services;

namespace SmartBank.Tests.Support
{
    /// <summary>
    /// An in-memory database, a clock the test controls, fake rates and one-time-code delivery, and the banking service on top.
    /// Helpers seed users, accounts and cards. Uses the static card-encryption key, so the test classes that use it belong to
    /// the "EncryptionHelper" collection.
    /// </summary>
    public sealed class BankingHarness : IDisposable
    {
        private int _counter;

        public string DatabaseName { get; } = Guid.NewGuid().ToString();
        public SmartBankDbContext Context { get; }
        public TestClock Clock { get; } = new();
        public FakeOtpDelivery Otp { get; } = new();
        public FakeMarketRates Rates { get; } = new();
        public BankingService Service { get; }

        public BankingHarness()
        {
            EncryptionHelper.Configure(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            Context = NewContext();
            Service = NewService(Context);
        }

        public static DbContextOptions<SmartBankDbContext> OptionsFor(string databaseName) =>
            new DbContextOptionsBuilder<SmartBankDbContext>()
                .UseInMemoryDatabase(databaseName)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;

        /// <summary>A second context on the same data, like another web request.</summary>
        public SmartBankDbContext NewContext() => new(OptionsFor(DatabaseName));

        public BankingService NewService(SmartBankDbContext context) => new(context, Otp, Rates, null, null, Clock);

        public async Task<User> AddUserAsync(string? name = null, bool twoFactor = false)
        {
            var n = Interlocked.Increment(ref _counter);
            name ??= "user" + n;
            var user = new User
            {
                Username = name,
                Tckn = TestTckn.Next(),
                PasswordHash = "x",
                FirstName = name,
                LastName = "Tester",
                FullName = name + " Tester",
                Email = $"{name}@test.example",
                TwoFactorEnabled = twoFactor,
                CreatedAt = Clock.UtcNow
            };
            Context.Users.Add(user);
            await Context.SaveChangesAsync();
            return user;
        }

        public async Task<Account> AddAccountAsync(User user, string number, decimal balance = 0m, string currency = "TRY",
            string type = "DemandDeposit", DateTime? createdAt = null)
        {
            var n = Interlocked.Increment(ref _counter);
            var account = new Account
            {
                UserId = user.Id,
                AccountNumber = number,
                AccountCode = "ACC-T" + n,
                Balance = balance,
                Currency = currency,
                AccountType = type,
                EncryptedCardNumber = EncryptionHelper.Encrypt("4" + SecureRandom.Digits(15)),
                CreatedAt = createdAt ?? Clock.UtcNow.AddSeconds(n)
            };
            Context.Accounts.Add(account);
            await Context.SaveChangesAsync();
            return account;
        }

        public async Task<CreditCard> AddCardAsync(User user, decimal debt = 0m, decimal limit = 10000m)
        {
            var card = new CreditCard
            {
                UserId = user.Id,
                EncryptedCardNumber = EncryptionHelper.Encrypt("4" + SecureRandom.Digits(15)),
                CardLimit = limit,
                CurrentDebt = debt,
                CreatedAt = Clock.UtcNow
            };
            Context.CreditCards.Add(card);
            await Context.SaveChangesAsync();
            return card;
        }

        public async Task<CreditCardStatement> AddStatementAsync(CreditCard card, decimal debt, decimal paid = 0m, DateTime? cutoff = null, bool isPaid = false, string name = "Ekim 2026")
        {
            var cut = cutoff ?? Clock.UtcNow.AddDays(10);
            var statement = new CreditCardStatement
            {
                CreditCardId = card.Id,
                PeriodName = name,
                PeriodDebt = debt,
                MinimumPayment = Money.Round(debt * 0.30m),
                PaidAmount = paid,
                CutoffDate = cut,
                DueDate = cut.AddDays(10),
                IsPaid = isPaid
            };
            Context.CreditCardStatements.Add(statement);
            await Context.SaveChangesAsync();
            return statement;
        }

        // Fresh reads, never tracked: what is really stored.
        public async Task<decimal> BalanceAsync(string accountNumber)
        {
            await using var read = NewContext();
            return (await read.Accounts.AsNoTracking().SingleAsync(a => a.AccountNumber == accountNumber)).Balance;
        }

        public async Task<T> ReadAsync<T>(Func<SmartBankDbContext, Task<T>> query)
        {
            await using var read = NewContext();
            return await query(read);
        }

        public void Dispose() => Context.Dispose();
    }
}
