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
        public async Task The_same_transfer_sent_several_times_at_once_goes_through_only_once(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var seed = await SeedAsync(db, balanceA: 1000m, balanceB: 0m);

            // "The same transfer again within 30 seconds" is decided before the money moves; requests that arrive together all
            // pass that check, so the step that moves the money asks again.
            var operations = Enumerable.Range(0, 8)
                .Select(_ => (Func<Task<ServiceResult>>)(() => TransferAsync(db, seed.UserA, AccountA, AccountB, 50m)))
                .ToList();

            var results = await ConcurrencyHarness.RunTogetherAsync(operations);

            Assert.Single(results, r => r.IsSuccess);
            Assert.All(results.Where(r => !r.IsSuccess), r => Assert.Contains(r.ErrorKey, new[] { "SuspectedFraudDuplicate", "ConcurrentModification" }));
            await using var context = db.NewContext();
            Assert.Equal(950m, (await context.Accounts.AsNoTracking().SingleAsync(x => x.AccountNumber == AccountA)).Balance);
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Repeated_bursts_of_transfers_never_surface_a_deadlock_as_an_error(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var seed = await SeedAsync(db, balanceA: 100000m, balanceB: 0m);

            // SQL Server picks a deadlock victim among readers and writers of the same rows (error 1205); that must be retried,
            // not returned as a 500. A transfer that does fail may only fail the way a collision is allowed to.
            var all = new List<ServiceResult>();
            for (var round = 0; round < 4; round++)
            {
                var operations = Enumerable.Range(0, 30)
                    .Select(i => 1m + round * 0.5m + i / 100m)
                    .Select(amount => (Func<Task<ServiceResult>>)(() => TransferAsync(db, seed.UserA, AccountA, AccountB, amount)))
                    .ToList();
                all.AddRange(await ConcurrencyHarness.RunTogetherAsync(operations));
            }

            await AssertMoneyIsConservedAsync(db, expectedTotal: 100000m, all.ToArray());
            Assert.True(all.Count(r => r.IsSuccess) > 60, "most transfers should succeed");
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
