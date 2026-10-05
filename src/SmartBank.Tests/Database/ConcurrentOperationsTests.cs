using Microsoft.EntityFrameworkCore;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;

namespace SmartBank.Tests.Database
{
    /// <summary>The other operations that read a balance or a debt and write it back, run against a real database.</summary>
    // One collection: these classes each open dozens of connections, so they run one after another, not in parallel.
    [Collection("Database")]
    public class ConcurrentOperationsTests
    {
        private const string AccountA = "TR0000000000000001";
        private const string AccountB = "TR0000000000000002";

        private static readonly string[] AllowedFailures = { "InsufficientFunds", "InsufficientLimit", "ConcurrentModification" };

        private static async Task<(Guid UserA, Guid UserB)> SeedUsersAndAccountsAsync(TestDb db, decimal balanceA, decimal balanceB)
        {
            await using var context = db.NewContext();
            var a = new User { Username = "alice", Tckn = "11111111111", PasswordHash = "x", FullName = "Alice", Email = "a@test.com" };
            var b = new User { Username = "bob", Tckn = "22222222222", PasswordHash = "x", FullName = "Bob", Email = "b@test.com" };
            context.Users.AddRange(a, b);
            context.Accounts.AddRange(
                new Account { UserId = a.Id, AccountNumber = AccountA, AccountCode = "ACC-1", Balance = balanceA, Currency = "TRY" },
                new Account { UserId = b.Id, AccountNumber = AccountB, AccountCode = "ACC-2", Balance = balanceB, Currency = "TRY" });
            await context.SaveChangesAsync();
            return (a.Id, b.Id);
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Deposits_and_transfers_at_the_same_time_keep_the_books_balanced(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var (userA, _) = await SeedUsersAndAccountsAsync(db, balanceA: 1000m, balanceB: 0m);

            var operations = new List<Func<Task<(string Kind, decimal Amount, ServiceResult<TransactionDto> Result)>>>();
            for (var i = 0; i < 15; i++)
            {
                var deposit = 10m + i / 100m;
                var transfer = 25m + i / 100m;
                operations.Add(async () => ("deposit", deposit,
                    await ConcurrencyHarness.InNewRequestAsync(db, s => s.DepositMoneyAsync(userA, AccountA, deposit))));
                operations.Add(async () => ("transfer", transfer,
                    await ConcurrencyHarness.InNewRequestAsync(db, s => s.TransferMoneyAsync(userA, new TransferRequestDto
                    {
                        SourceAccountNumber = AccountA,
                        DestinationAccountNumber = AccountB,
                        Amount = transfer,
                        Description = "mixed"
                    }))));
            }

            var outcomes = await ConcurrencyHarness.RunTogetherAsync(operations);

            var depositedIn = outcomes.Where(o => o.Kind == "deposit" && o.Result.IsSuccess).Sum(o => o.Amount);

            await using var context = db.NewContext();
            var a = await context.Accounts.AsNoTracking().SingleAsync(x => x.AccountNumber == AccountA);
            var b = await context.Accounts.AsNoTracking().SingleAsync(x => x.AccountNumber == AccountB);

            // Money in the system = what was there + what was successfully deposited. Transfers only move it around.
            Assert.Equal(1000m + depositedIn, a.Balance + b.Balance);
            Assert.True(a.Balance >= 0m);
            Assert.Equal(outcomes.Where(o => o.Kind == "transfer" && o.Result.IsSuccess).Sum(o => o.Amount), b.Balance);
            Assert.Equal(outcomes.Count(o => o.Result.IsSuccess), await context.Transactions.CountAsync());

            var unexpected = outcomes.Where(o => !o.Result.IsSuccess && !AllowedFailures.Contains(o.Result.ErrorKey)).ToList();
            Assert.True(unexpected.Count == 0, "Unexpected failures: " + string.Join("; ", unexpected.Select(o => $"{o.Kind} {o.Result.ErrorKey}: {o.Result.Message}")));
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Concurrent_card_charges_never_exceed_the_limit(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var (userA, _) = await SeedUsersAndAccountsAsync(db, balanceA: 0m, balanceB: 0m);

            Guid cardId;
            await using (var seed = db.NewContext())
            {
                var card = new CreditCard { UserId = userA, EncryptedCardNumber = string.Empty, CardLimit = 1000m, CurrentDebt = 0m };
                seed.CreditCards.Add(card);
                await seed.SaveChangesAsync();
                cardId = card.Id;
            }

            // 30 charges of about 50 TRY: 1,500 in total against a 1,000 limit.
            var operations = Enumerable.Range(0, 30)
                .Select(i => 50m + i / 100m)
                .Select(amount => (Func<Task<(decimal Amount, ServiceResult<CreditCardDto> Result)>>)(async () =>
                    (amount, await ConcurrencyHarness.InNewRequestAsync(db, s => s.ChargeCreditCardAsync(userA, cardId, amount, "test")))))
                .ToList();

            var outcomes = await ConcurrencyHarness.RunTogetherAsync(operations);

            await using var context = db.NewContext();
            var stored = await context.CreditCards.AsNoTracking().SingleAsync(c => c.Id == cardId);

            Assert.True(stored.CurrentDebt <= stored.CardLimit, $"Debt {stored.CurrentDebt} exceeds the limit {stored.CardLimit}");
            Assert.Equal(outcomes.Where(o => o.Result.IsSuccess).Sum(o => o.Amount), stored.CurrentDebt);
            Assert.Equal(outcomes.Count(o => o.Result.IsSuccess), await context.CreditCardTransactions.CountAsync());
            Assert.Contains(outcomes, o => o.Result.IsSuccess);

            var unexpected = outcomes.Where(o => !o.Result.IsSuccess && !AllowedFailures.Contains(o.Result.ErrorKey)).ToList();
            Assert.True(unexpected.Count == 0, "Unexpected failures: " + string.Join("; ", unexpected.Select(o => $"{o.Result.ErrorKey}: {o.Result.Message}")));
        }
    }
}
