using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartBank.Core.Entities;
using SmartBank.Infrastructure.BackgroundServices;
using SmartBank.Infrastructure.Data;
using SmartBank.Tests.Support;

namespace SmartBank.Tests.Worker
{
    /// <summary>
    /// The standing-order worker on the in-memory database (the same cases run against real databases in
    /// Database/StandingOrderWorkerTests): what is executed, what is rejected for good, and what is simply tried again.
    /// </summary>
    [Collection("EncryptionHelper")]
    public class StandingOrderWorkerTests : IDisposable
    {
        private const string Source = "TR0000000000000001";
        private const string Destination = "TR0000000000000002";

        private readonly BankingHarness _h = new();

        /// <summary>A context that fails its saves on demand, to play out database trouble.</summary>
        private sealed class TroubledContext : SmartBankDbContext
        {
            private readonly Func<int, Exception?> _failure;
            private int _saves;

            public TroubledContext(DbContextOptions<SmartBankDbContext> options, Func<int, Exception?> failure) : base(options) => _failure = failure;

            public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
            {
                var failure = _failure(Interlocked.Increment(ref _saves));
                return failure != null ? throw failure : base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            }
        }

        public void Dispose() => _h.Dispose();

        private StandingOrderExecutionWorker NewWorker(Func<Exception?>? failEverySave = null, Func<SmartBankDbContext>? contextFactory = null)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => contextFactory != null
                ? contextFactory()
                : failEverySave != null
                    ? new TroubledContext(BankingHarness.OptionsFor(_h.DatabaseName), _ => failEverySave())
                    : new SmartBankDbContext(BankingHarness.OptionsFor(_h.DatabaseName)));

            return new StandingOrderExecutionWorker(
                services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                NullLogger<StandingOrderExecutionWorker>.Instance,
                _h.Clock);
        }

        private async Task<(User User, Account From, Account To, StandingOrder Order)> SeedAsync(decimal balance = 1000m, string fromCurrency = "TRY", string toCurrency = "TRY",
            Action<StandingOrder>? customise = null)
        {
            var user = await _h.AddUserAsync();
            var from = await _h.AddAccountAsync(user, Source, balance, fromCurrency);
            var to = await _h.AddAccountAsync(user, Destination, 0m, toCurrency);
            var order = new StandingOrder
            {
                UserId = user.Id,
                SourceAccountNumber = Source,
                DestinationAccountNumber = Destination,
                Amount = 100m,
                Frequency = "Monthly",
                OrderType = "Transfer",
                CreatedAt = _h.Clock.UtcNow.AddDays(-1),
                NextExecutionDate = _h.Clock.UtcNow.AddMinutes(-5),
                MaturityDate = _h.Clock.UtcNow.AddYears(1)
            };
            customise?.Invoke(order);
            _h.Context.StandingOrders.Add(order);
            await _h.Context.SaveChangesAsync();
            return (user, from, to, order);
        }

        private Task<StandingOrder> StoredOrderAsync(Guid id) => _h.ReadAsync(c => c.StandingOrders.AsNoTracking().SingleAsync(o => o.Id == id));

        // ---- success -----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_due_transfer_moves_the_money_with_one_ledger_row_and_an_audit_entry()
        {
            var (_, from, to, order) = await SeedAsync();

            await NewWorker().RunOnceAsync(CancellationToken.None);

            Assert.Equal(900m, await _h.BalanceAsync(Source));
            Assert.Equal(100m, await _h.BalanceAsync(Destination));

            var row = await _h.ReadAsync(c => c.Transactions.AsNoTracking().SingleAsync()); // one row, not an outgoing and an incoming one
            Assert.Equal(TransactionType.Transfer, row.Type);
            Assert.Equal(from.Id, row.SourceAccountId);
            Assert.Equal(to.Id, row.DestinationAccountId);
            Assert.Equal(100m, row.Amount);

            var audit = await _h.ReadAsync(c => c.AuditLogs.AsNoTracking().SingleAsync());
            Assert.Equal("StandingOrderExecuted", audit.Action);
            Assert.Contains("Amount: 100.00 TRY", audit.Details);
            Assert.Equal("system:standing-order-worker", audit.IpAddress);

            var stored = await StoredOrderAsync(order.Id);
            Assert.True(stored.IsActive);
            Assert.Equal(order.NextExecutionDate.AddMonths(1), stored.NextExecutionDate);
        }

        [Theory]
        [InlineData("Daily", 1)]
        [InlineData("Weekly", 7)]
        public async Task The_next_run_is_one_period_after_the_previous_due_date(string frequency, int days)
        {
            var (_, _, _, order) = await SeedAsync(customise: o => o.Frequency = frequency);

            await NewWorker().RunOnceAsync(CancellationToken.None);

            Assert.Equal(order.NextExecutionDate.AddDays(days), (await StoredOrderAsync(order.Id)).NextExecutionDate);
        }

        [Fact]
        public async Task A_late_order_runs_once_per_cycle_and_catches_up_one_period_at_a_time()
        {
            var (_, _, _, order) = await SeedAsync(customise: o => { o.Frequency = "Daily"; o.NextExecutionDate = _h.Clock.UtcNow.AddDays(-3); });
            var worker = NewWorker();

            await worker.RunOnceAsync(CancellationToken.None);
            Assert.Equal(900m, await _h.BalanceAsync(Source));

            await worker.RunOnceAsync(CancellationToken.None);
            await worker.RunOnceAsync(CancellationToken.None);
            await worker.RunOnceAsync(CancellationToken.None);
            await worker.RunOnceAsync(CancellationToken.None); // a fifth pass: the order is now in the future, nothing is due

            Assert.Equal(600m, await _h.BalanceAsync(Source)); // the days -3, -2, -1 and today: four payments, no fifth
            Assert.True((await StoredOrderAsync(order.Id)).NextExecutionDate > _h.Clock.UtcNow);
        }

        [Fact]
        public async Task An_order_that_is_not_due_yet_is_left_alone()
        {
            await SeedAsync(customise: o => o.NextExecutionDate = _h.Clock.UtcNow.AddMinutes(5));

            await NewWorker().RunOnceAsync(CancellationToken.None);

            Assert.Equal(1000m, await _h.BalanceAsync(Source));
            Assert.Empty(await _h.ReadAsync(c => c.Transactions.ToListAsync()));
        }

        [Fact]
        public async Task The_clock_decides_when_an_order_is_due()
        {
            var (_, _, _, order) = await SeedAsync(customise: o => o.NextExecutionDate = _h.Clock.UtcNow.AddMinutes(10));
            var worker = NewWorker();

            await worker.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1000m, await _h.BalanceAsync(Source));

            _h.Clock.Advance(TimeSpan.FromMinutes(11));
            await worker.RunOnceAsync(CancellationToken.None);

            Assert.Equal(900m, await _h.BalanceAsync(Source));
            Assert.True((await StoredOrderAsync(order.Id)).IsActive);
        }

        [Fact]
        public async Task An_inactive_order_is_ignored()
        {
            await SeedAsync(customise: o => o.IsActive = false);

            await NewWorker().RunOnceAsync(CancellationToken.None);

            Assert.Equal(1000m, await _h.BalanceAsync(Source));
        }

        // ---- permanent rejections ----------------------------------------------------------------------------

        private async Task AssertRejectedAsync(StandingOrder order, string reasonFragment, decimal expectedSource = 1000m, decimal expectedDestination = 0m)
        {
            var stored = await StoredOrderAsync(order.Id);
            Assert.False(stored.IsActive);

            Assert.Equal(expectedSource, await _h.BalanceAsync(Source));
            Assert.Equal(expectedDestination, await _h.BalanceAsync(Destination));
            Assert.Empty(await _h.ReadAsync(c => c.Transactions.ToListAsync()));

            var audit = await _h.ReadAsync(c => c.AuditLogs.AsNoTracking().SingleAsync());
            Assert.Equal("StandingOrderDeactivated", audit.Action);
            Assert.Contains(reasonFragment, audit.Details);
            Assert.Equal(order.UserId, audit.UserId);
        }

        [Fact]
        public async Task A_low_balance_deactivates_the_order_and_says_so_in_the_audit_trail()
        {
            var (_, _, _, order) = await SeedAsync(balance: 50m);

            await NewWorker().RunOnceAsync(CancellationToken.None);

            await AssertRejectedAsync(order, "too low", expectedSource: 50m);
        }

        [Fact]
        public async Task Different_currencies_are_never_moved_one_to_one()
        {
            var (_, _, _, order) = await SeedAsync(toCurrency: "USD");

            await NewWorker().RunOnceAsync(CancellationToken.None);

            await AssertRejectedAsync(order, "different currencies");
        }

        [Fact]
        public async Task A_missing_destination_deactivates_the_order_without_taking_a_cent()
        {
            var (_, _, to, order) = await SeedAsync();
            _h.Context.Accounts.Remove(await _h.Context.Accounts.SingleAsync(a => a.Id == to.Id));
            await _h.Context.SaveChangesAsync();

            await NewWorker().RunOnceAsync(CancellationToken.None);

            var stored = await StoredOrderAsync(order.Id);
            Assert.False(stored.IsActive);
            Assert.Equal(1000m, await _h.BalanceAsync(Source)); // the money stays where it was
            Assert.Empty(await _h.ReadAsync(c => c.Transactions.ToListAsync()));
            Assert.Contains("destination account does not exist", (await _h.ReadAsync(c => c.AuditLogs.AsNoTracking().SingleAsync())).Details);
        }

        [Fact]
        public async Task An_order_without_a_destination_is_rejected_not_executed_into_thin_air()
        {
            var (_, _, _, order) = await SeedAsync(customise: o => o.DestinationAccountNumber = null);

            await NewWorker().RunOnceAsync(CancellationToken.None);

            await AssertRejectedAsync(order, "no destination");
        }

        [Fact]
        public async Task The_same_account_on_both_sides_is_rejected()
        {
            var (_, _, _, order) = await SeedAsync(customise: o => o.DestinationAccountNumber = Source);

            await NewWorker().RunOnceAsync(CancellationToken.None);

            await AssertRejectedAsync(order, "same account");
        }

        [Fact]
        public async Task A_source_that_no_longer_belongs_to_the_customer_is_rejected()
        {
            var (_, _, _, order) = await SeedAsync();
            var other = await _h.AddUserAsync("other");
            var stored = await _h.Context.StandingOrders.SingleAsync(o => o.Id == order.Id);
            stored.UserId = other.Id;
            await _h.Context.SaveChangesAsync();

            await NewWorker().RunOnceAsync(CancellationToken.None);

            var after = await StoredOrderAsync(order.Id);
            Assert.False(after.IsActive);
            Assert.Equal(1000m, await _h.BalanceAsync(Source));
            Assert.Contains("does not belong", (await _h.ReadAsync(c => c.AuditLogs.AsNoTracking().SingleAsync())).Details);
        }

        [Fact]
        public async Task A_missing_source_is_rejected()
        {
            var (_, from, _, order) = await SeedAsync();
            _h.Context.Accounts.Remove(await _h.Context.Accounts.SingleAsync(a => a.Id == from.Id));
            await _h.Context.SaveChangesAsync();

            await NewWorker().RunOnceAsync(CancellationToken.None);

            Assert.False((await StoredOrderAsync(order.Id)).IsActive);
            Assert.Contains("source account no longer exists", (await _h.ReadAsync(c => c.AuditLogs.AsNoTracking().SingleAsync())).Details);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        [InlineData(-10)]
        public async Task A_missing_zero_or_negative_amount_is_rejected(int? amount)
        {
            var (_, _, _, order) = await SeedAsync(customise: o => o.Amount = amount);

            await NewWorker().RunOnceAsync(CancellationToken.None);

            await AssertRejectedAsync(order, "amount of the order is not valid");
        }

        [Fact]
        public async Task An_order_past_its_maturity_date_is_switched_off_and_does_not_run()
        {
            var (_, _, _, order) = await SeedAsync(customise: o => o.MaturityDate = _h.Clock.UtcNow.AddMinutes(-1));

            await NewWorker().RunOnceAsync(CancellationToken.None);

            await AssertRejectedAsync(order, "maturity");
        }

        [Fact]
        public async Task An_order_on_its_last_day_still_runs()
        {
            var (_, _, _, order) = await SeedAsync(customise: o => o.MaturityDate = _h.Clock.UtcNow.AddMinutes(1));

            await NewWorker().RunOnceAsync(CancellationToken.None);

            Assert.Equal(900m, await _h.BalanceAsync(Source));
            Assert.True((await StoredOrderAsync(order.Id)).IsActive);
        }

        // ---- credit-card orders ------------------------------------------------------------------------------

        private async Task<(User User, CreditCard Card, StandingOrder Order)> SeedCardOrderAsync(string orderType = "CreditCardAutoPay", decimal balance = 1000m)
        {
            var (user, _, _, order) = await SeedAsync(balance, customise: o => { o.OrderType = orderType; o.DestinationAccountNumber = null; o.Amount = null; });
            var card = await _h.AddCardAsync(user, 500m);
            await _h.AddStatementAsync(card, 500m);
            var stored = await _h.Context.StandingOrders.SingleAsync(o => o.Id == order.Id);
            stored.CreditCardId = card.Id;
            await _h.Context.SaveChangesAsync();
            return (user, card, order);
        }

        [Theory]
        [InlineData("CreditCardAutoPay")]
        [InlineData("CreditCardDebt")]
        public async Task An_auto_pay_order_pays_the_open_statement_with_one_withdrawal_row(string orderType)
        {
            var (_, card, order) = await SeedCardOrderAsync(orderType);

            await NewWorker().RunOnceAsync(CancellationToken.None);

            Assert.Equal(500m, await _h.BalanceAsync(Source));
            Assert.Equal(0m, (await _h.ReadAsync(c => c.CreditCards.AsNoTracking().SingleAsync(x => x.Id == card.Id))).CurrentDebt);
            var statement = await _h.ReadAsync(c => c.CreditCardStatements.AsNoTracking().SingleAsync());
            Assert.True(statement.IsPaid);
            Assert.Equal(500m, statement.PaidAmount);
            var row = await _h.ReadAsync(c => c.Transactions.AsNoTracking().SingleAsync());
            Assert.Equal(TransactionType.Withdrawal, row.Type);
            Assert.Null(row.DestinationAccountId);
            Assert.True((await StoredOrderAsync(order.Id)).IsActive);
        }

        [Fact]
        public async Task An_auto_pay_order_with_nothing_to_pay_just_moves_to_the_next_period()
        {
            var (_, card, order) = await SeedCardOrderAsync();
            var statement = await _h.Context.CreditCardStatements.SingleAsync();
            statement.IsPaid = true;
            await _h.Context.SaveChangesAsync();

            await NewWorker().RunOnceAsync(CancellationToken.None);

            Assert.Equal(1000m, await _h.BalanceAsync(Source));
            Assert.Empty(await _h.ReadAsync(c => c.Transactions.ToListAsync()));
            var stored = await StoredOrderAsync(order.Id);
            Assert.True(stored.IsActive);
            Assert.Equal(order.NextExecutionDate.AddMonths(1), stored.NextExecutionDate);
        }

        [Fact]
        public async Task An_auto_pay_order_for_somebody_elses_card_is_rejected()
        {
            var (_, card, order) = await SeedCardOrderAsync();
            var other = await _h.AddUserAsync("other");
            var stored = await _h.Context.CreditCards.SingleAsync(c => c.Id == card.Id);
            stored.UserId = other.Id;
            await _h.Context.SaveChangesAsync();

            await NewWorker().RunOnceAsync(CancellationToken.None);

            Assert.False((await StoredOrderAsync(order.Id)).IsActive);
            Assert.Equal(1000m, await _h.BalanceAsync(Source));
            Assert.Equal(500m, (await _h.ReadAsync(c => c.CreditCards.AsNoTracking().SingleAsync())).CurrentDebt);
        }

        [Fact]
        public async Task An_auto_pay_order_from_a_foreign_currency_account_is_rejected()
        {
            var (user, _, _, order) = await SeedAsync(fromCurrency: "USD", customise: o => { o.OrderType = "CreditCardAutoPay"; o.DestinationAccountNumber = null; o.Amount = null; });
            var card = await _h.AddCardAsync(user, 500m);
            await _h.AddStatementAsync(card, 500m);
            var stored = await _h.Context.StandingOrders.SingleAsync(o => o.Id == order.Id);
            stored.CreditCardId = card.Id;
            await _h.Context.SaveChangesAsync();

            await NewWorker().RunOnceAsync(CancellationToken.None);

            Assert.False((await StoredOrderAsync(order.Id)).IsActive);
            Assert.Equal(1000m, await _h.BalanceAsync(Source));
        }

        // ---- trouble is not the order's fault ----------------------------------------------------------------

        [Fact]
        public async Task A_database_hiccup_leaves_the_order_active_and_the_next_cycle_executes_it_exactly_once()
        {
            var (_, _, _, order) = await SeedAsync();

            await NewWorker(failEverySave: () => new InvalidOperationException("the connection dropped")).RunOnceAsync(CancellationToken.None);

            Assert.True((await StoredOrderAsync(order.Id)).IsActive);
            Assert.Equal(1000m, await _h.BalanceAsync(Source));
            Assert.Empty(await _h.ReadAsync(c => c.Transactions.ToListAsync()));
            Assert.Empty(await _h.ReadAsync(c => c.AuditLogs.ToListAsync()));

            await NewWorker().RunOnceAsync(CancellationToken.None);

            Assert.Equal(900m, await _h.BalanceAsync(Source));
            Assert.Single(await _h.ReadAsync(c => c.Transactions.ToListAsync()));
        }

        [Fact]
        public async Task A_collision_with_another_change_leaves_the_order_active()
        {
            var (_, _, _, order) = await SeedAsync();

            await NewWorker(failEverySave: () => new DbUpdateConcurrencyException("someone else changed the account")).RunOnceAsync(CancellationToken.None);

            Assert.True((await StoredOrderAsync(order.Id)).IsActive);
            Assert.Equal(1000m, await _h.BalanceAsync(Source));
        }

        [Fact]
        public async Task A_failing_deactivation_does_not_crash_the_worker_and_the_order_is_judged_again_next_time()
        {
            var (_, _, _, order) = await SeedAsync(balance: 50m); // will be rejected for good...

            // ...but every save fails, including the one that would switch the order off.
            await NewWorker(failEverySave: () => new InvalidOperationException("the database is gone")).RunOnceAsync(CancellationToken.None);

            Assert.True((await StoredOrderAsync(order.Id)).IsActive);

            await NewWorker().RunOnceAsync(CancellationToken.None); // the database is back: now it is switched off

            Assert.False((await StoredOrderAsync(order.Id)).IsActive);
        }

        [Fact]
        public async Task One_orders_failure_does_not_stop_the_orders_after_it()
        {
            var (user, _, _, bad) = await SeedAsync(customise: o => { o.Amount = 5000m; o.NextExecutionDate = _h.Clock.UtcNow.AddMinutes(-10); }); // 5000 > 1000
            var good = new StandingOrder
            {
                UserId = user.Id,
                SourceAccountNumber = Source,
                DestinationAccountNumber = Destination,
                Amount = 100m,
                Frequency = "Monthly",
                OrderType = "Transfer",
                NextExecutionDate = _h.Clock.UtcNow.AddMinutes(-1),
                MaturityDate = _h.Clock.UtcNow.AddYears(1)
            };
            _h.Context.StandingOrders.Add(good);
            await _h.Context.SaveChangesAsync();

            await NewWorker().RunOnceAsync(CancellationToken.None);

            Assert.False((await StoredOrderAsync(bad.Id)).IsActive);
            Assert.True((await StoredOrderAsync(good.Id)).IsActive);
            Assert.Equal(900m, await _h.BalanceAsync(Source));
        }
    }
}
