using Microsoft.EntityFrameworkCore;
using Moq;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Services;

namespace SmartBank.Tests.Database
{
    /// <summary>
    /// Transfers that run at the same time on a real database. Each "request" gets its own DbContext and service,
    /// exactly like separate HTTP requests. Whatever interleaving happens, money must be neither created nor
    /// destroyed, a balance must never go negative, and every successful transfer must be recorded exactly once.
    /// </summary>
    // One collection: these classes each open dozens of connections, so they run one after another, not in parallel.
    [Collection("Database")]
    public class ConcurrentTransferTests
    {
        private const string AccountA = "TR0000000000000001";
        private const string AccountB = "TR0000000000000002";

        // The only failures a caller may legitimately see under contention.
        private static readonly string[] AllowedFailures = { "InsufficientFunds", "ConcurrentModification" };

        private sealed record Seed(Guid UserA, Guid UserB);

        private static async Task<Seed> SeedAsync(TestDb db, decimal balanceA, decimal balanceB)
        {
            await using var context = db.NewContext();
            var a = new User { Username = "alice", Tckn = "11111111111", PasswordHash = "x", FullName = "Alice", Email = "a@test.com" };
            var b = new User { Username = "bob", Tckn = "22222222222", PasswordHash = "x", FullName = "Bob", Email = "b@test.com" };
            context.Users.AddRange(a, b);
            context.Accounts.AddRange(
                new Account { UserId = a.Id, AccountNumber = AccountA, AccountCode = "ACC-1", Balance = balanceA, Currency = "TRY" },
                new Account { UserId = b.Id, AccountNumber = AccountB, AccountCode = "ACC-2", Balance = balanceB, Currency = "TRY" });
            await context.SaveChangesAsync();
            return new Seed(a.Id, b.Id);
        }

        private static async Task<ServiceResult> TransferAsync(TestDb db, Guid userId, string from, string to, decimal amount)
        {
            // New context + new service per call = one web request.
            await using var context = db.NewContext();
            var service = new BankingService(context, new FakeOtpDelivery(), new Mock<IMarketRateService>().Object);

            var result = await service.TransferMoneyAsync(userId, new TransferRequestDto
            {
                SourceAccountNumber = from,
                DestinationAccountNumber = to,
                Amount = amount,
                Description = "concurrency test"
            });

            return new ServiceResult(result.IsSuccess, result.ErrorKey, result.Message, result.Data?.Amount ?? 0m);
        }

        private sealed record ServiceResult(bool IsSuccess, string? ErrorKey, string? Message, decimal Amount);

        private static async Task AssertMoneyIsConservedAsync(TestDb db, decimal expectedTotal, ServiceResult[] results)
        {
            await using var context = db.NewContext();
            var a = await context.Accounts.AsNoTracking().SingleAsync(x => x.AccountNumber == AccountA);
            var b = await context.Accounts.AsNoTracking().SingleAsync(x => x.AccountNumber == AccountB);
            var recorded = await context.Transactions.CountAsync();

            Assert.Equal(expectedTotal, a.Balance + b.Balance);     // nothing created, nothing destroyed
            Assert.True(a.Balance >= 0m, $"Account A went negative: {a.Balance}");
            Assert.True(b.Balance >= 0m, $"Account B went negative: {b.Balance}");
            Assert.Equal(results.Count(r => r.IsSuccess), recorded); // every success recorded exactly once

            var unexpected = results.Where(r => !r.IsSuccess && !AllowedFailures.Contains(r.ErrorKey)).ToList();
            Assert.True(unexpected.Count == 0,
                "Unexpected failures: " + string.Join("; ", unexpected.Select(r => $"{r.ErrorKey}: {r.Message}")));
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Many_transfers_out_of_one_account_never_overdraw_it_or_lose_money(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var seed = await SeedAsync(db, balanceA: 1000m, balanceB: 0m);

            // 40 distinct amounts (so the duplicate-transfer fraud rule stays out of the way) that together
            // exceed the balance: 30.00 .. 30.39 TRY each, about 1,215 TRY in total.
            var operations = Enumerable.Range(0, 40)
                .Select(i => 30m + i / 100m)
                .Select(amount => (Func<Task<ServiceResult>>)(() => TransferAsync(db, seed.UserA, AccountA, AccountB, amount)))
                .ToList();

            var results = await ConcurrencyHarness.RunTogetherAsync(operations);

            await AssertMoneyIsConservedAsync(db, expectedTotal: 1000m, results);
            Assert.Contains(results, r => r.IsSuccess);

            await using var context = db.NewContext();
            var b = await context.Accounts.AsNoTracking().SingleAsync(x => x.AccountNumber == AccountB);
            Assert.Equal(results.Where(r => r.IsSuccess).Sum(r => r.Amount), b.Balance);
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Transfers_in_opposite_directions_at_the_same_time_neither_deadlock_nor_lose_money(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var seed = await SeedAsync(db, balanceA: 500m, balanceB: 500m);

            var operations = new List<Func<Task<ServiceResult>>>();
            for (var i = 0; i < 12; i++)
            {
                var amount = 20m + i / 100m;
                operations.Add(() => TransferAsync(db, seed.UserA, AccountA, AccountB, amount));
                operations.Add(() => TransferAsync(db, seed.UserB, AccountB, AccountA, amount + 0.5m));
            }

            var results = await ConcurrencyHarness.RunTogetherAsync(operations);

            await AssertMoneyIsConservedAsync(db, expectedTotal: 1000m, results);
            Assert.Contains(results, r => r.IsSuccess);
        }
    }
}
