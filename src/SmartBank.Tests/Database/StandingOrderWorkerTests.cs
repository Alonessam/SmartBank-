using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartBank.Core.Entities;
using SmartBank.Infrastructure.BackgroundServices;
using SmartBank.Infrastructure.Data;

namespace SmartBank.Tests.Database
{
    /// <summary>The standing-order worker against a real database: runs must be exactly-once and failures must be clean.</summary>
    // One collection: these classes each open dozens of connections, so they run one after another, not in parallel.
    [Collection("Database")]
    public class StandingOrderWorkerTests
    {
        private const string Source = "TR0000000000000001";
        private const string Destination = "TR0000000000000002";

        private static StandingOrderExecutionWorker NewWorker(TestDb db)
        {
            var services = new ServiceCollection();
            services.AddScoped<SmartBankDbContext>(_ => db.NewContext());
            return new StandingOrderExecutionWorker(
                services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                NullLogger<StandingOrderExecutionWorker>.Instance);
        }

        private static async Task<Guid> SeedAsync(TestDb db, decimal sourceBalance, Action<SmartBankDbContext, User, StandingOrder>? customise = null)
        {
            await using var context = db.NewContext();
            var user = new User { Username = "alice", Tckn = "11111111111", PasswordHash = "x", FullName = "Alice", Email = "a@test.com" };
            context.Users.Add(user);
            context.Accounts.AddRange(
                new Account { UserId = user.Id, AccountNumber = Source, AccountCode = "ACC-1", Balance = sourceBalance, Currency = "TRY" },
                new Account { UserId = user.Id, AccountNumber = Destination, AccountCode = "ACC-2", Balance = 0m, Currency = "TRY" });

            var order = new StandingOrder
            {
                UserId = user.Id,
                SourceAccountNumber = Source,
                DestinationAccountNumber = Destination,
                Amount = 100m,
                Frequency = "Monthly",
                OrderType = "Transfer",
                NextExecutionDate = DateTime.UtcNow.AddMinutes(-5),
                MaturityDate = DateTime.UtcNow.AddYears(1)
            };
            customise?.Invoke(context, user, order);
            context.StandingOrders.Add(order);

            await context.SaveChangesAsync();
            return order.Id;
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task A_due_order_is_executed_exactly_once_even_if_several_workers_run_at_the_same_time(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var orderId = await SeedAsync(db, sourceBalance: 1000m);

            await ConcurrencyHarness.RunTogetherAsync(Enumerable.Range(0, 4).Select(_ =>
                (Func<Task<bool>>)(async () => { await NewWorker(db).RunOnceAsync(CancellationToken.None); return true; })));

            await using var context = db.NewContext();
            var source = await context.Accounts.AsNoTracking().SingleAsync(a => a.AccountNumber == Source);
            var destination = await context.Accounts.AsNoTracking().SingleAsync(a => a.AccountNumber == Destination);
            var order = await context.StandingOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);

            Assert.Equal(900m, source.Balance);          // debited once, not four times
            Assert.Equal(100m, destination.Balance);
            Assert.True(order.IsActive);
            Assert.True(order.NextExecutionDate > DateTime.UtcNow, "The order should have moved to its next period.");
            Assert.Equal(1, await context.Transactions.CountAsync()); // ONE ledger row per movement, not an outgoing and an incoming one
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Different_currencies_are_rejected_not_moved_one_to_one(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var orderId = await SeedAsync(db, sourceBalance: 1000m, (context, user, order) =>
            {
                context.ChangeTracker.Entries<Account>().Single(e => e.Entity.AccountNumber == Destination).Entity.Currency = "USD";
            });

            await NewWorker(db).RunOnceAsync(CancellationToken.None);

            await using var verify = db.NewContext();
            Assert.False((await verify.StandingOrders.AsNoTracking().SingleAsync(o => o.Id == orderId)).IsActive);
            Assert.Equal(1000m, (await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountNumber == Source)).Balance);
            Assert.Equal(0m, (await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountNumber == Destination)).Balance);
            Assert.Equal(0, await verify.Transactions.CountAsync());
            Assert.Contains(await verify.AuditLogs.AsNoTracking().ToListAsync(), a => a.Action == "StandingOrderDeactivated" && a.Details.Contains("different currencies"));
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task A_missing_destination_deactivates_the_order_without_taking_money(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var orderId = await SeedAsync(db, sourceBalance: 1000m, (context, user, order) => order.DestinationAccountNumber = "TR9999999999999999");

            await NewWorker(db).RunOnceAsync(CancellationToken.None);

            await using var verify = db.NewContext();
            Assert.False((await verify.StandingOrders.AsNoTracking().SingleAsync(o => o.Id == orderId)).IsActive);
            Assert.Equal(1000m, (await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountNumber == Source)).Balance);
            Assert.Equal(0, await verify.Transactions.CountAsync());
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task An_order_past_its_maturity_date_is_switched_off_and_does_not_run(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var orderId = await SeedAsync(db, sourceBalance: 1000m, (context, user, order) => order.MaturityDate = DateTime.UtcNow.AddMinutes(-1));

            await NewWorker(db).RunOnceAsync(CancellationToken.None);

            await using var verify = db.NewContext();
            Assert.False((await verify.StandingOrders.AsNoTracking().SingleAsync(o => o.Id == orderId)).IsActive);
            Assert.Equal(1000m, (await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountNumber == Source)).Balance);
            Assert.Contains(await verify.AuditLogs.AsNoTracking().ToListAsync(), a => a.Details.Contains("maturity"));
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task A_failing_order_is_deactivated_and_leaves_no_partial_changes(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var orderId = await SeedAsync(db, sourceBalance: 50m); // the order wants 100

            await NewWorker(db).RunOnceAsync(CancellationToken.None);

            await using var context = db.NewContext();
            var source = await context.Accounts.AsNoTracking().SingleAsync(a => a.AccountNumber == Source);
            var order = await context.StandingOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);

            Assert.False(order.IsActive);
            Assert.Equal(50m, source.Balance);
            Assert.Equal(0, await context.Transactions.CountAsync());
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task A_credit_card_auto_pay_order_pays_the_due_statement(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            Guid cardId = Guid.Empty;
            var orderId = await SeedAsync(db, sourceBalance: 1000m, (context, user, order) =>
            {
                var card = new CreditCard { UserId = user.Id, EncryptedCardNumber = string.Empty, CardLimit = 10000m, CurrentDebt = 500m };
                card.Statements.Add(new CreditCardStatement
                {
                    PeriodName = "Ekim 2026",
                    PeriodDebt = 500m,
                    MinimumPayment = 150m,
                    PaidAmount = 0m,
                    CutoffDate = DateTime.UtcNow.AddDays(-10),
                    DueDate = DateTime.UtcNow.AddDays(5),
                    IsPaid = false
                });
                context.CreditCards.Add(card);
                cardId = card.Id;

                order.OrderType = "CreditCardAutoPay"; // the name the UI and BankingService use
                order.CreditCardId = card.Id;
                order.DestinationAccountNumber = null;
                order.Amount = null;
            });

            await NewWorker(db).RunOnceAsync(CancellationToken.None);

            await using var verify = db.NewContext();
            var source = await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountNumber == Source);
            var card = await verify.CreditCards.AsNoTracking().Include(c => c.Statements).SingleAsync(c => c.Id == cardId);
            var order = await verify.StandingOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);

            Assert.Equal(500m, source.Balance);
            Assert.Equal(0m, card.CurrentDebt);
            Assert.All(card.Statements, s => Assert.True(s.IsPaid));
            Assert.True(order.IsActive);
        }
    }
}
