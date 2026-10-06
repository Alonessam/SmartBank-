using Microsoft.EntityFrameworkCore;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Tests.Support;

namespace SmartBank.Tests.Banking
{
    /// <summary>Paying card debt, charging the card, closing statements (interest), reading statements and issuing the card.</summary>
    [Collection("EncryptionHelper")]
    public class CreditCardTests : IDisposable
    {
        private readonly BankingHarness _h = new();

        public void Dispose() => _h.Dispose();

        private static PayCreditCardDebtDto Pay(string source, decimal amount) => new() { SourceAccountNumber = source, Amount = amount };

        private async Task<(User User, Account Account, CreditCard Card, CreditCardStatement Statement)> SeedAsync(decimal balance = 1000m, decimal debt = 500m)
        {
            var user = await _h.AddUserAsync();
            var account = await _h.AddAccountAsync(user, "TR0000000000000001", balance);
            var card = await _h.AddCardAsync(user, debt);
            var statement = await _h.AddStatementAsync(card, debt);
            return (user, account, card, statement);
        }

        // ---- paying ------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_partial_payment_lowers_the_debt_and_is_recorded_on_the_statement()
        {
            var (user, account, card, statement) = await SeedAsync();

            var result = await _h.Service.PayCreditCardDebtAsync(user.Id, card.Id, Pay(account.AccountNumber, 200m));

            Assert.True(result.IsSuccess);
            Assert.Equal(800m, await _h.BalanceAsync(account.AccountNumber));
            var storedCard = await _h.ReadAsync(c => c.CreditCards.AsNoTracking().SingleAsync());
            Assert.Equal(300m, storedCard.CurrentDebt);
            var storedStatement = await _h.ReadAsync(c => c.CreditCardStatements.AsNoTracking().SingleAsync());
            Assert.Equal(200m, storedStatement.PaidAmount);
            Assert.False(storedStatement.IsPaid);
        }

        [Fact]
        public async Task Paying_exactly_the_debt_settles_card_and_statement_and_is_recorded_as_a_withdrawal_with_an_audit_entry()
        {
            var (user, account, card, _) = await SeedAsync();

            var result = await _h.Service.PayCreditCardDebtAsync(user.Id, card.Id, Pay(account.AccountNumber, 500m));

            Assert.True(result.IsSuccess);
            Assert.Equal(0m, (await _h.ReadAsync(c => c.CreditCards.AsNoTracking().SingleAsync())).CurrentDebt);
            Assert.True((await _h.ReadAsync(c => c.CreditCardStatements.AsNoTracking().SingleAsync())).IsPaid);

            var tx = await _h.ReadAsync(c => c.Transactions.AsNoTracking().SingleAsync());
            Assert.Equal(TransactionType.Withdrawal, tx.Type);
            Assert.Equal(500m, tx.Amount);
            Assert.Null(tx.DestinationAccountId);
            Assert.Contains(await _h.ReadAsync(c => c.AuditLogs.AsNoTracking().ToListAsync()), a => a.Action == "CreditCardPayment" && a.Details.Contains("500.00"));
        }

        [Fact]
        public async Task Paying_more_than_the_debt_is_refused_and_no_money_vanishes()
        {
            var (user, account, card, _) = await SeedAsync();

            var result = await _h.Service.PayCreditCardDebtAsync(user.Id, card.Id, Pay(account.AccountNumber, 500.01m));

            Assert.False(result.IsSuccess);
            Assert.Equal("PaymentExceedsDebt", result.ErrorKey);
            Assert.Equal(1000m, await _h.BalanceAsync(account.AccountNumber));
            Assert.Equal(500m, (await _h.ReadAsync(c => c.CreditCards.AsNoTracking().SingleAsync())).CurrentDebt);
        }

        [Fact]
        public async Task A_card_without_debt_takes_no_payment()
        {
            var (user, account, card, _) = await SeedAsync(debt: 0m);

            var result = await _h.Service.PayCreditCardDebtAsync(user.Id, card.Id, Pay(account.AccountNumber, 10m));

            Assert.Equal("PaymentExceedsDebt", result.ErrorKey);
            Assert.Equal(1000m, await _h.BalanceAsync(account.AccountNumber));
        }

        [Fact]
        public async Task The_statement_records_only_what_was_applied_to_it()
        {
            // 150 owed: 100 on the closed statement and 50 of charges made after it.
            var user = await _h.AddUserAsync();
            var account = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);
            var card = await _h.AddCardAsync(user, 150m);
            await _h.AddStatementAsync(card, 100m);

            var result = await _h.Service.PayCreditCardDebtAsync(user.Id, card.Id, Pay(account.AccountNumber, 120m));

            Assert.True(result.IsSuccess);
            var statement = await _h.ReadAsync(c => c.CreditCardStatements.AsNoTracking().SingleAsync());
            Assert.Equal(100m, statement.PaidAmount); // not 120
            Assert.True(statement.IsPaid);
            Assert.Equal(30m, (await _h.ReadAsync(c => c.CreditCards.AsNoTracking().SingleAsync())).CurrentDebt);
        }

        [Fact]
        public async Task A_payment_settles_the_oldest_open_statement_first_and_the_rest_goes_to_the_next()
        {
            var user = await _h.AddUserAsync();
            var account = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);
            var card = await _h.AddCardAsync(user, 300m);
            var older = await _h.AddStatementAsync(card, 100m, cutoff: _h.Clock.UtcNow.AddDays(-40), name: "Eylül 2026");
            var newer = await _h.AddStatementAsync(card, 200m, cutoff: _h.Clock.UtcNow.AddDays(-10), name: "Ekim 2026");

            await _h.Service.PayCreditCardDebtAsync(user.Id, card.Id, Pay(account.AccountNumber, 150m));

            var statements = await _h.ReadAsync(c => c.CreditCardStatements.AsNoTracking().ToListAsync());
            var storedOlder = statements.Single(s => s.Id == older.Id);
            var storedNewer = statements.Single(s => s.Id == newer.Id);
            Assert.True(storedOlder.IsPaid);
            Assert.Equal(100m, storedOlder.PaidAmount);
            Assert.False(storedNewer.IsPaid);
            Assert.Equal(50m, storedNewer.PaidAmount);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task Zero_and_negative_payments_are_refused(double amount)
        {
            var (user, account, card, _) = await SeedAsync();

            var result = await _h.Service.PayCreditCardDebtAsync(user.Id, card.Id, Pay(account.AccountNumber, (decimal)amount));

            Assert.Equal("InvalidAmount", result.ErrorKey);
        }

        [Fact]
        public async Task More_than_two_decimals_is_refused()
        {
            var (user, account, card, _) = await SeedAsync();

            var result = await _h.Service.PayCreditCardDebtAsync(user.Id, card.Id, Pay(account.AccountNumber, 10.001m));

            Assert.Equal("InvalidAmountScale", result.ErrorKey);
            Assert.Equal(1000m, await _h.BalanceAsync(account.AccountNumber));
        }

        [Fact]
        public async Task Only_a_try_account_can_pay_and_it_must_hold_the_money_and_belong_to_the_caller()
        {
            var (user, account, card, _) = await SeedAsync(balance: 100m);
            var usd = await _h.AddAccountAsync(user, "TR0000000000000002", 5000m, "USD");
            var stranger = await _h.AddUserAsync("stranger");
            var strangerAccount = await _h.AddAccountAsync(stranger, "TR0000000000000003", 5000m);

            Assert.Equal("CurrencyMismatch", (await _h.Service.PayCreditCardDebtAsync(user.Id, card.Id, Pay(usd.AccountNumber, 10m))).ErrorKey);
            Assert.Equal("InsufficientFunds", (await _h.Service.PayCreditCardDebtAsync(user.Id, card.Id, Pay(account.AccountNumber, 200m))).ErrorKey);
            Assert.Equal("SourceAccountNotFound", (await _h.Service.PayCreditCardDebtAsync(user.Id, card.Id, Pay(strangerAccount.AccountNumber, 10m))).ErrorKey);
            Assert.Equal(5000m, await _h.BalanceAsync(strangerAccount.AccountNumber));
        }

        [Fact]
        public async Task Somebody_elses_card_cannot_be_paid()
        {
            var (_, _, card, _) = await SeedAsync();
            var stranger = await _h.AddUserAsync("stranger");
            var strangerAccount = await _h.AddAccountAsync(stranger, "TR0000000000000003", 5000m);

            var result = await _h.Service.PayCreditCardDebtAsync(stranger.Id, card.Id, Pay(strangerAccount.AccountNumber, 10m));

            Assert.Equal("CreditCardNotFound", result.ErrorKey);
            Assert.Equal(5000m, await _h.BalanceAsync(strangerAccount.AccountNumber));
        }

        // ---- charging ----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_charge_adds_debt_a_card_transaction_and_an_audit_entry()
        {
            var (user, _, card, _) = await SeedAsync();

            var result = await _h.Service.ChargeCreditCardAsync(user.Id, card.Id, 100.50m, "Coffee");

            Assert.True(result.IsSuccess);
            Assert.Equal(600.50m, result.Data!.CurrentDebt);
            Assert.Equal(9399.50m, result.Data.AvailableLimit);
            var charge = await _h.ReadAsync(c => c.CreditCardTransactions.AsNoTracking().SingleAsync());
            Assert.Equal("Coffee", charge.Description);
            Assert.Contains(await _h.ReadAsync(c => c.AuditLogs.AsNoTracking().ToListAsync()), a => a.Action == "CreditCardCharge" && a.Details.Contains("100.50 TRY"));
        }

        [Fact]
        public async Task A_charge_without_a_description_gets_the_default_text()
        {
            var (user, _, card, _) = await SeedAsync();

            await _h.Service.ChargeCreditCardAsync(user.Id, card.Id, 10m, "  ");

            Assert.Equal("Market Harcaması", (await _h.ReadAsync(c => c.CreditCardTransactions.AsNoTracking().SingleAsync())).Description);
        }

        [Fact]
        public async Task A_charge_cannot_exceed_the_limit()
        {
            var (user, _, card, _) = await SeedAsync(debt: 9500m);

            var result = await _h.Service.ChargeCreditCardAsync(user.Id, card.Id, 500.01m, "x");

            Assert.Equal("InsufficientLimit", result.ErrorKey);
        }

        [Theory]
        [InlineData(0, "InvalidAmount")]
        [InlineData(-5, "InvalidAmount")]
        [InlineData(1.234, "InvalidAmountScale")]
        public async Task A_charge_needs_a_positive_amount_with_at_most_two_decimals(double amount, string errorKey)
        {
            var (user, _, card, _) = await SeedAsync();

            var result = await _h.Service.ChargeCreditCardAsync(user.Id, card.Id, (decimal)amount, "x");

            Assert.Equal(errorKey, result.ErrorKey);
        }

        [Fact]
        public async Task A_description_over_200_characters_is_refused_with_a_clear_error()
        {
            var (user, _, card, _) = await SeedAsync();

            var result = await _h.Service.ChargeCreditCardAsync(user.Id, card.Id, 10m, new string('d', 201));

            Assert.Equal("InvalidDescription", result.ErrorKey);
            Assert.Equal(500m, (await _h.ReadAsync(c => c.CreditCards.AsNoTracking().SingleAsync())).CurrentDebt);
        }

        [Fact]
        public async Task Somebody_elses_card_cannot_be_charged()
        {
            var (_, _, card, _) = await SeedAsync();
            var stranger = await _h.AddUserAsync("stranger");

            var result = await _h.Service.ChargeCreditCardAsync(stranger.Id, card.Id, 10m, "x");

            Assert.Equal("CreditCardNotFound", result.ErrorKey);
        }

        // ---- closing a statement -----------------------------------------------------------------------------

        [Fact]
        public async Task An_unpaid_statement_below_the_minimum_costs_late_fee_plus_interest()
        {
            var user = await _h.AddUserAsync();
            var card = await _h.AddCardAsync(user, 1000m);
            await _h.AddStatementAsync(card, 1000m, cutoff: new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc));

            var result = await _h.Service.AdvanceStatementPeriodAsync(user.Id, card.Id);

            // unpaid 1000, minimum 300 unpaid: 300 x 5 % = 15 plus 700 x 4.25 % = 29.75
            Assert.True(result.IsSuccess);
            Assert.Equal(1044.75m, result.Data!.PeriodDebt);
            Assert.Equal(313.43m, result.Data.MinimumPayment); // 313.425 rounded away from zero
            Assert.False(result.Data.IsPaid);
            Assert.Equal(1044.75m, (await _h.ReadAsync(c => c.CreditCards.AsNoTracking().SingleAsync())).CurrentDebt);
            Assert.Equal(44.75m, (await _h.ReadAsync(c => c.CreditCardTransactions.AsNoTracking().SingleAsync())).Amount);
        }

        [Fact]
        public async Task Paying_the_minimum_means_interest_only_on_the_rest()
        {
            var user = await _h.AddUserAsync();
            var card = await _h.AddCardAsync(user, 700m); // 1000 owed on the statement, 300 already paid
            await _h.AddStatementAsync(card, 1000m, paid: 300m);

            var result = await _h.Service.AdvanceStatementPeriodAsync(user.Id, card.Id);

            Assert.Equal(729.75m, result.Data!.PeriodDebt); // 700 + 700 x 4.25 %
        }

        [Fact]
        public async Task A_settled_statement_costs_nothing_and_the_next_one_is_empty()
        {
            var user = await _h.AddUserAsync();
            var card = await _h.AddCardAsync(user, 0m);
            await _h.AddStatementAsync(card, 0m, isPaid: true);

            var result = await _h.Service.AdvanceStatementPeriodAsync(user.Id, card.Id);

            Assert.Equal(0m, result.Data!.PeriodDebt);
            Assert.True(result.Data.IsPaid);
            Assert.Empty(await _h.ReadAsync(c => c.CreditCardTransactions.ToListAsync()));
        }

        [Fact]
        public async Task The_period_names_and_dates_come_from_the_dates_not_from_parsing_text()
        {
            var user = await _h.AddUserAsync();
            var card = await _h.AddCardAsync(user, 0m);
            await _h.AddStatementAsync(card, 0m, isPaid: true, cutoff: new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc), name: "anything at all");

            var next = (await _h.Service.AdvanceStatementPeriodAsync(user.Id, card.Id)).Data!;
            var afterThat = (await _h.Service.AdvanceStatementPeriodAsync(user.Id, card.Id)).Data!;

            Assert.Equal("Kasım 2026", next.PeriodName);  // closes 5 Dec: its period began in November
            Assert.Equal(new DateTime(2026, 12, 5, 0, 0, 0, DateTimeKind.Utc), next.CutoffDate);
            Assert.Equal(new DateTime(2026, 12, 15, 0, 0, 0, DateTimeKind.Utc), next.DueDate);
            Assert.Equal("Aralık 2026", afterThat.PeriodName);
            Assert.Equal(new DateTime(2027, 1, 5, 0, 0, 0, DateTimeKind.Utc), afterThat.CutoffDate);
        }

        [Fact]
        public async Task An_auto_pay_order_pays_what_the_account_holds_and_interest_is_charged_on_the_rest()
        {
            var user = await _h.AddUserAsync();
            var account = await _h.AddAccountAsync(user, "TR0000000000000001", 500m);
            var card = await _h.AddCardAsync(user, 1000m);
            var statement = await _h.AddStatementAsync(card, 1000m);
            _h.Context.StandingOrders.Add(new StandingOrder { UserId = user.Id, SourceAccountNumber = account.AccountNumber, OrderType = OrderTypes.CreditCardAutoPay, CreditCardId = card.Id });
            await _h.Context.SaveChangesAsync();

            var result = await _h.Service.AdvanceStatementPeriodAsync(user.Id, card.Id);

            Assert.True(result.IsSuccess);
            Assert.Equal(0m, await _h.BalanceAsync(account.AccountNumber));
            var closed = await _h.ReadAsync(c => c.CreditCardStatements.AsNoTracking().SingleAsync(s => s.Id == statement.Id));
            Assert.Equal(500m, closed.PaidAmount);
            // 500 left, the paid part (500) is above the 300 minimum: 500 x 4.25 % = 21.25
            Assert.Equal(521.25m, result.Data!.PeriodDebt);
            var tx = await _h.ReadAsync(c => c.Transactions.AsNoTracking().SingleAsync());
            Assert.Equal(TransactionType.Withdrawal, tx.Type);
            Assert.Equal(500m, tx.Amount);
        }

        [Fact]
        public async Task An_auto_pay_order_pays_the_whole_statement_when_the_account_can_cover_it()
        {
            var user = await _h.AddUserAsync();
            var account = await _h.AddAccountAsync(user, "TR0000000000000001", 2000m);
            var card = await _h.AddCardAsync(user, 1000m);
            await _h.AddStatementAsync(card, 1000m);
            _h.Context.StandingOrders.Add(new StandingOrder { UserId = user.Id, SourceAccountNumber = account.AccountNumber, OrderType = OrderTypes.CreditCardAutoPay, CreditCardId = card.Id });
            await _h.Context.SaveChangesAsync();

            var result = await _h.Service.AdvanceStatementPeriodAsync(user.Id, card.Id);

            Assert.Equal(1000m, await _h.BalanceAsync(account.AccountNumber));
            Assert.Equal(0m, result.Data!.PeriodDebt);
            Assert.True(result.Data.IsPaid);
        }

        [Fact]
        public async Task Closing_a_statement_is_audited()
        {
            var (user, _, card, _) = await SeedAsync();

            await _h.Service.AdvanceStatementPeriodAsync(user.Id, card.Id);

            Assert.Contains(await _h.ReadAsync(c => c.AuditLogs.AsNoTracking().ToListAsync()), a => a.Action == "StatementAdvanced");
        }

        [Fact]
        public async Task Somebody_elses_card_cannot_be_advanced()
        {
            var (_, _, card, _) = await SeedAsync();
            var stranger = await _h.AddUserAsync("stranger");

            var result = await _h.Service.AdvanceStatementPeriodAsync(stranger.Id, card.Id);

            Assert.Equal("CreditCardNotFound", result.ErrorKey);
        }

        // ---- reading statements ------------------------------------------------------------------------------

        [Fact]
        public async Task Statements_come_newest_first_each_with_the_transactions_of_its_own_period()
        {
            var user = await _h.AddUserAsync();
            var card = await _h.AddCardAsync(user, 0m);
            var cut1 = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);
            var cut2 = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
            await _h.AddStatementAsync(card, 10m, cutoff: cut1, isPaid: true, name: "Temmuz 2026");
            await _h.AddStatementAsync(card, 20m, cutoff: cut2, isPaid: true, name: "Ağustos 2026");
            _h.Context.CreditCardTransactions.AddRange(
                new CreditCardTransaction { CreditCardId = card.Id, Description = "july shop", Amount = 10m, CreatedAt = cut1.AddDays(-3) },
                new CreditCardTransaction { CreditCardId = card.Id, Description = "august shop", Amount = 20m, CreatedAt = cut2.AddDays(-3) },
                new CreditCardTransaction { CreditCardId = card.Id, Description = "on the cutoff day", Amount = 5m, CreatedAt = cut2 });
            await _h.Context.SaveChangesAsync();

            var result = await _h.Service.GetStatementsAsync(card.Id, user.Id);

            Assert.True(result.IsSuccess);
            Assert.Equal(new[] { "Ağustos 2026", "Temmuz 2026" }, result.Data!.Select(s => s.PeriodName).ToArray());
            Assert.Equal(new[] { "august shop", "on the cutoff day" }, result.Data[0].Transactions.Select(t => t.Description).OrderBy(d => d).ToArray());
            Assert.Equal(new[] { "july shop" }, result.Data[1].Transactions.Select(t => t.Description).ToArray());
        }

        [Fact]
        public async Task At_most_the_newest_24_statements_are_returned()
        {
            var user = await _h.AddUserAsync();
            var card = await _h.AddCardAsync(user, 0m);
            for (var i = 0; i < 30; i++) await _h.AddStatementAsync(card, i, cutoff: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(i), isPaid: true, name: "S" + i);

            var result = await _h.Service.GetStatementsAsync(card.Id, user.Id);

            Assert.Equal(CreditCardRules.MaxStatementsReturned, result.Data!.Count);
            Assert.Equal("S29", result.Data[0].PeriodName);
            Assert.Equal("S6", result.Data[^1].PeriodName);
        }

        [Fact]
        public async Task Somebody_elses_statements_cannot_be_read_but_the_owners_can()
        {
            var (user, _, card, _) = await SeedAsync();
            var stranger = await _h.AddUserAsync("stranger");

            var denied = await _h.Service.GetStatementsAsync(card.Id, stranger.Id);
            var allowed = await _h.Service.GetStatementsAsync(card.Id, user.Id);

            Assert.Equal("CreditCardNotFound", denied.ErrorKey);
            Assert.True(allowed.IsSuccess);
            Assert.Single(allowed.Data!);
        }

        // ---- issuing -----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_new_card_has_the_default_limit_and_a_first_statement_named_from_the_clock()
        {
            var user = await _h.AddUserAsync();

            var result = await _h.Service.CreateCreditCardAsync(user.Id);

            Assert.True(result.IsSuccess);
            Assert.Equal(10000m, result.Data!.CardLimit);
            Assert.Matches("^[0-9]{3}$", result.Data.CardCvv);
            Assert.Equal("10/34", result.Data.ExpiryDate); // eight years from 2026-10
            var statement = await _h.ReadAsync(c => c.CreditCardStatements.AsNoTracking().SingleAsync());
            Assert.Equal("Ekim 2026", statement.PeriodName);
            Assert.Equal(_h.Clock.UtcNow.AddDays(30), statement.CutoffDate);
            Assert.Equal(_h.Clock.UtcNow.AddDays(40), statement.DueDate);
            Assert.True(statement.IsPaid);
        }

        [Fact]
        public async Task A_second_card_is_refused()
        {
            var user = await _h.AddUserAsync();
            Assert.True((await _h.Service.CreateCreditCardAsync(user.Id)).IsSuccess);

            var second = await _h.Service.CreateCreditCardAsync(user.Id);

            Assert.Equal("MaxCreditCardsLimitReached", second.ErrorKey);
            Assert.Equal(1, await _h.ReadAsync(c => c.CreditCards.CountAsync()));
        }
    }
}
