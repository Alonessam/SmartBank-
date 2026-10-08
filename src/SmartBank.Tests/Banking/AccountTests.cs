using Microsoft.EntityFrameworkCore;
using SmartBank.Core.Entities;
using SmartBank.Tests.Support;

namespace SmartBank.Tests.Banking
{
    /// <summary>Opening, funding and closing accounts.</summary>
    [Collection("EncryptionHelper")]
    public class AccountTests : IDisposable
    {
        private readonly BankingHarness _h = new();

        public void Dispose() => _h.Dispose();

        // ---- opening -----------------------------------------------------------------------------------------

        [Theory]
        [InlineData("TRY")]
        [InlineData("usd")]
        [InlineData(" eur ")]
        [InlineData("XAU")]
        [InlineData("xag")]
        public async Task The_five_supported_currencies_open_an_account_in_upper_case(string currency)
        {
            var user = await _h.AddUserAsync();

            var result = await _h.Service.CreateAccountAsync(user.Id, currency);

            Assert.True(result.IsSuccess);
            Assert.Equal(currency.Trim().ToUpperInvariant(), result.Data!.Currency);
            Assert.Matches("^TR[0-9]{16}$", result.Data.AccountNumber);
            Assert.Matches("^4[0-9]{15}$", result.Data.CardNumber);
            Assert.Matches("^[0-9]{3}$", result.Data.CardCvv);
            Assert.Equal(0m, result.Data.Balance);
        }

        [Fact]
        public async Task A_blank_currency_means_try()
        {
            var user = await _h.AddUserAsync();

            Assert.Equal("TRY", (await _h.Service.CreateAccountAsync(user.Id, "")).Data!.Currency);
            Assert.Equal("TRY", (await _h.Service.CreateAccountAsync(user.Id, "  ")).Data!.Currency);
        }

        [Theory]
        [InlineData("GBP")]
        [InlineData("ABCD")]
        [InlineData("<script>")]
        [InlineData("12")]
        public async Task Any_other_currency_is_refused_and_nothing_is_stored(string currency)
        {
            var user = await _h.AddUserAsync();

            var result = await _h.Service.CreateAccountAsync(user.Id, currency);

            Assert.False(result.IsSuccess);
            Assert.Equal("InvalidCurrency", result.ErrorKey);
            Assert.Empty(await _h.ReadAsync(c => c.Accounts.ToListAsync()));
        }

        [Theory]
        [InlineData("demanddeposit", "DemandDeposit")]
        [InlineData("TIMEDEPOSIT", "TimeDeposit")]
        [InlineData("", "DemandDeposit")]
        public async Task The_account_type_is_normalised_to_its_canonical_spelling(string given, string stored)
        {
            var user = await _h.AddUserAsync();

            var result = await _h.Service.CreateAccountAsync(user.Id, "TRY", given);

            Assert.Equal(stored, result.Data!.AccountType);
            Assert.Equal(stored, (await _h.ReadAsync(c => c.Accounts.AsNoTracking().SingleAsync())).AccountType);
        }

        [Theory]
        [InlineData("Savings")]
        [InlineData("DemandDepositDemandDepositDemandDeposit")]
        public async Task Another_account_type_is_refused(string type)
        {
            var user = await _h.AddUserAsync();

            var result = await _h.Service.CreateAccountAsync(user.Id, "TRY", type);

            Assert.Equal("InvalidAccountType", result.ErrorKey);
        }

        [Fact]
        public async Task A_time_deposit_gets_a_display_rate_and_a_maturity_date_from_the_clock()
        {
            var user = await _h.AddUserAsync();

            var result = await _h.Service.CreateAccountAsync(user.Id, "TRY", "TimeDeposit");

            Assert.Equal(48.00m, result.Data!.InterestRate);
            Assert.Equal(_h.Clock.UtcNow.AddDays(30), result.Data.MaturityDate);
            Assert.Equal("10/31", result.Data.ExpiryDate); // five years from 2026-10
        }

        [Fact]
        public async Task The_displayed_time_deposit_rate_follows_the_balance_tiers()
        {
            var user = await _h.AddUserAsync();
            await _h.AddAccountAsync(user, "TR0000000000000001", 10m, type: "TimeDeposit");
            await _h.AddAccountAsync(user, "TR0000000000000002", 60_000m, type: "TimeDeposit");
            await _h.AddAccountAsync(user, "TR0000000000000003", 300_000m, type: "TimeDeposit");
            await _h.AddAccountAsync(user, "TR0000000000000004", 2_000_000m, type: "TimeDeposit");

            var rates = (await _h.Service.GetAccountsAsync(user.Id)).Data!.Select(a => a.InterestRate).ToArray();

            Assert.Equal(new decimal?[] { 48.00m, 49.50m, 51.00m, 52.50m }, rates);
        }

        [Fact]
        public async Task A_customer_has_at_most_ten_accounts()
        {
            var user = await _h.AddUserAsync();
            for (var i = 0; i < 10; i++) Assert.True((await _h.Service.CreateAccountAsync(user.Id, "TRY")).IsSuccess);

            var eleventh = await _h.Service.CreateAccountAsync(user.Id, "TRY");

            Assert.Equal("AccountLimitReached", eleventh.ErrorKey);
            Assert.Equal(10, await _h.ReadAsync(c => c.Accounts.CountAsync()));
        }

        [Fact]
        public async Task An_unknown_user_cannot_open_an_account()
        {
            var result = await _h.Service.CreateAccountAsync(Guid.NewGuid(), "TRY");

            Assert.Equal("UserNotFound", result.ErrorKey);
        }

        [Fact]
        public async Task Reading_accounts_shows_the_decrypted_card_number_but_never_a_cvv()
        {
            var user = await _h.AddUserAsync();
            var created = (await _h.Service.CreateAccountAsync(user.Id, "USD")).Data!;

            var read = (await _h.Service.GetAccountsAsync(user.Id)).Data!.Single();

            Assert.Equal(created.CardNumber, read.CardNumber);
            Assert.Equal(string.Empty, read.CardCvv);
        }

        // ---- deposits ----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_deposit_adds_money_and_one_ledger_row_with_the_same_account_on_both_sides()
        {
            var user = await _h.AddUserAsync();
            var account = await _h.AddAccountAsync(user, "TR0000000000000001", 10m);

            var result = await _h.Service.DepositMoneyAsync(user.Id, account.AccountNumber, 123.45m);

            Assert.True(result.IsSuccess);
            Assert.Equal(133.45m, await _h.BalanceAsync(account.AccountNumber));
            var row = await _h.ReadAsync(c => c.Transactions.AsNoTracking().SingleAsync());
            Assert.Equal(TransactionType.Deposit, row.Type);
            Assert.Equal(row.SourceAccountId, row.DestinationAccountId);
        }

        [Theory]
        [InlineData(0, "InvalidAmount")]
        [InlineData(-1, "InvalidAmount")]
        [InlineData(10_000_000.01, "InvalidAmount")]
        [InlineData(1.005, "InvalidAmountScale")]
        [InlineData(0.001, "InvalidAmountScale")]
        public async Task A_deposit_needs_a_sensible_amount(double amount, string errorKey)
        {
            var user = await _h.AddUserAsync();
            var account = await _h.AddAccountAsync(user, "TR0000000000000001", 10m);

            var result = await _h.Service.DepositMoneyAsync(user.Id, account.AccountNumber, (decimal)amount);

            Assert.Equal(errorKey, result.ErrorKey);
            Assert.Equal(10m, await _h.BalanceAsync(account.AccountNumber));
        }

        [Fact]
        public async Task The_largest_allowed_deposit_works()
        {
            var user = await _h.AddUserAsync();
            var account = await _h.AddAccountAsync(user, "TR0000000000000001", 0m);

            Assert.True((await _h.Service.DepositMoneyAsync(user.Id, account.AccountNumber, 10_000_000m)).IsSuccess);
        }

        [Fact]
        public async Task Money_cannot_be_deposited_into_somebody_elses_account()
        {
            var owner = await _h.AddUserAsync("owner");
            var stranger = await _h.AddUserAsync("stranger");
            var account = await _h.AddAccountAsync(owner, "TR0000000000000001", 10m);

            var result = await _h.Service.DepositMoneyAsync(stranger.Id, account.AccountNumber, 5m);

            Assert.Equal("AccountNotFound", result.ErrorKey);
            Assert.Equal(10m, await _h.BalanceAsync(account.AccountNumber));
        }

        // ---- history -----------------------------------------------------------------------------------------

        [Fact]
        public async Task The_history_is_newest_first_and_limited_by_take()
        {
            var user = await _h.AddUserAsync();
            var account = await _h.AddAccountAsync(user, "TR0000000000000001", 0m);
            for (var i = 1; i <= 12; i++)
            {
                _h.Clock.Advance(TimeSpan.FromMinutes(1));
                await _h.Service.DepositMoneyAsync(user.Id, account.AccountNumber, i);
            }

            var latestThree = (await _h.Service.GetTransactionsAsync(account.Id, user.Id, 3)).Data!;
            var all = (await _h.Service.GetTransactionsAsync(account.Id, user.Id)).Data!;

            Assert.Equal(new[] { 12m, 11m, 10m }, latestThree.Select(t => t.Amount).ToArray());
            Assert.Equal(12, all.Count);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(-5, 1)]
        [InlineData(100000, 500)]
        public async Task Take_is_clamped_to_between_one_and_500(int take, int expectedAtMost)
        {
            var user = await _h.AddUserAsync();
            var account = await _h.AddAccountAsync(user, "TR0000000000000001", 0m);
            for (var i = 0; i < 3; i++) await _h.Service.DepositMoneyAsync(user.Id, account.AccountNumber, 1m);

            var rows = (await _h.Service.GetTransactionsAsync(account.Id, user.Id, take)).Data!;

            Assert.Equal(Math.Min(3, expectedAtMost), rows.Count);
        }

        [Fact]
        public async Task The_history_of_somebody_elses_account_looks_like_an_account_that_does_not_exist()
        {
            var owner = await _h.AddUserAsync("owner");
            var stranger = await _h.AddUserAsync("stranger");
            var account = await _h.AddAccountAsync(owner, "TR0000000000000001", 0m);

            var foreign = await _h.Service.GetTransactionsAsync(account.Id, stranger.Id);
            var missing = await _h.Service.GetTransactionsAsync(Guid.NewGuid(), stranger.Id);

            Assert.Equal("AccountNotFound", foreign.ErrorKey);
            Assert.Equal(missing.ErrorKey, foreign.ErrorKey);
            Assert.Equal(missing.Message, foreign.Message);
        }

        // ---- closing -----------------------------------------------------------------------------------------

        private async Task<(User User, Account Closing, Account Target)> TwoAccountsAsync(decimal closingBalance, string closingCurrency = "TRY", string targetCurrency = "TRY", decimal targetBalance = 0m)
        {
            var user = await _h.AddUserAsync();
            var closing = await _h.AddAccountAsync(user, "TR0000000000000001", closingBalance, closingCurrency);
            var target = await _h.AddAccountAsync(user, "TR0000000000000002", targetBalance, targetCurrency);
            return (user, closing, target);
        }

        [Fact]
        public async Task Closing_an_account_moves_the_balance_and_keeps_the_history_without_the_link()
        {
            var (user, closing, target) = await TwoAccountsAsync(250m, targetBalance: 10m);
            var stranger = await _h.AddUserAsync("stranger");
            var strangerAccount = await _h.AddAccountAsync(stranger, "TR0000000000000009", 100m);
            _h.Context.Transactions.AddRange(
                new Transaction { SourceAccountId = closing.Id, DestinationAccountId = strangerAccount.Id, Amount = 5m, Type = TransactionType.Transfer, Description = "out" },
                new Transaction { SourceAccountId = strangerAccount.Id, DestinationAccountId = closing.Id, Amount = 7m, Type = TransactionType.Transfer, Description = "in" });
            await _h.Context.SaveChangesAsync();

            var result = await _h.Service.DeleteAccountAsync(user.Id, closing.Id, target.Id);

            Assert.True(result.IsSuccess);
            Assert.False(await _h.ReadAsync(c => c.Accounts.AnyAsync(a => a.Id == closing.Id)));
            Assert.Equal(260m, await _h.BalanceAsync(target.AccountNumber));

            var rows = await _h.ReadAsync(c => c.Transactions.AsNoTracking().ToListAsync());
            Assert.Equal(3, rows.Count); // nothing is deleted from the history
            Assert.DoesNotContain(rows, r => r.SourceAccountId == closing.Id || r.DestinationAccountId == closing.Id);

            var closingRow = rows.Single(r => r.Description.StartsWith("Hesap Kapatma"));
            Assert.Null(closingRow.SourceAccountId);
            Assert.Equal(target.Id, closingRow.DestinationAccountId);
            Assert.Equal(250m, closingRow.Amount);

            // The other customer's history keeps its rows too: they only lose the link to the closed account.
            Assert.Equal(100m, await _h.BalanceAsync(strangerAccount.AccountNumber));
            Assert.Contains(rows, r => r.Description == "out" && r.SourceAccountId == null && r.DestinationAccountId == strangerAccount.Id);
            Assert.Contains(rows, r => r.Description == "in" && r.DestinationAccountId == null && r.SourceAccountId == strangerAccount.Id);

            Assert.Contains(await _h.ReadAsync(c => c.AuditLogs.AsNoTracking().ToListAsync()), a => a.Action == "DeleteAccount" && a.Details.Contains(closing.AccountNumber));
        }

        [Fact]
        public async Task Closing_an_account_switches_off_standing_orders_and_removes_recipients_that_point_at_it()
        {
            var (user, closing, target) = await TwoAccountsAsync(0m);
            var other = await _h.AddUserAsync("other");
            _h.Context.StandingOrders.AddRange(
                new StandingOrder { UserId = user.Id, SourceAccountNumber = closing.AccountNumber, DestinationAccountNumber = target.AccountNumber, Amount = 5m },
                new StandingOrder { UserId = other.Id, SourceAccountNumber = "TR0000000000000009", DestinationAccountNumber = closing.AccountNumber, Amount = 5m },
                new StandingOrder { UserId = user.Id, SourceAccountNumber = target.AccountNumber, DestinationAccountNumber = "TR0000000000000009", Amount = 5m });
            _h.Context.SavedContacts.AddRange(
                new SavedContact { UserId = other.Id, AccountNumber = closing.AccountNumber, Alias = "was here" },
                new SavedContact { UserId = other.Id, AccountNumber = "TR0000000000000009", Alias = "stays" });
            await _h.Context.SaveChangesAsync();

            var result = await _h.Service.DeleteAccountAsync(user.Id, closing.Id);

            Assert.True(result.IsSuccess);
            var orders = await _h.ReadAsync(c => c.StandingOrders.AsNoTracking().ToListAsync());
            Assert.Equal(2, orders.Count(o => !o.IsActive));
            Assert.Single(orders, o => o.IsActive && o.SourceAccountNumber == target.AccountNumber);
            Assert.Equal("stays", (await _h.ReadAsync(c => c.SavedContacts.AsNoTracking().SingleAsync())).Alias);
        }

        [Fact]
        public async Task An_empty_account_needs_no_target()
        {
            var (user, closing, _) = await TwoAccountsAsync(0m);

            Assert.True((await _h.Service.DeleteAccountAsync(user.Id, closing.Id)).IsSuccess);
            Assert.False(await _h.ReadAsync(c => c.Accounts.AnyAsync(a => a.Id == closing.Id)));
        }

        [Fact]
        public async Task A_balance_needs_a_target_and_it_must_be_another_account_of_the_same_customer()
        {
            var (user, closing, _) = await TwoAccountsAsync(100m);
            var stranger = await _h.AddUserAsync("stranger");
            var strangerAccount = await _h.AddAccountAsync(stranger, "TR0000000000000009", 0m);

            Assert.Equal("TargetAccountRequired", (await _h.Service.DeleteAccountAsync(user.Id, closing.Id)).ErrorKey);
            Assert.Equal("TargetAccountRequired", (await _h.Service.DeleteAccountAsync(user.Id, closing.Id, Guid.Empty)).ErrorKey);
            Assert.Equal("TargetAccountNotFound", (await _h.Service.DeleteAccountAsync(user.Id, closing.Id, strangerAccount.Id)).ErrorKey);
            Assert.Equal("TargetAccountNotFound", (await _h.Service.DeleteAccountAsync(user.Id, closing.Id, closing.Id)).ErrorKey);
            Assert.Equal("TargetAccountNotFound", (await _h.Service.DeleteAccountAsync(user.Id, closing.Id, Guid.NewGuid())).ErrorKey);

            Assert.True(await _h.ReadAsync(c => c.Accounts.AnyAsync(a => a.Id == closing.Id)));
            Assert.Equal(0m, await _h.BalanceAsync(strangerAccount.AccountNumber));
        }

        [Fact]
        public async Task The_last_account_cannot_be_closed()
        {
            var user = await _h.AddUserAsync();
            var only = await _h.AddAccountAsync(user, "TR0000000000000001", 0m);

            var result = await _h.Service.DeleteAccountAsync(user.Id, only.Id);

            Assert.Equal("CannotDeleteLastAccount", result.ErrorKey);
        }

        [Fact]
        public async Task Somebody_elses_account_cannot_be_closed()
        {
            var (_, closing, _) = await TwoAccountsAsync(0m);
            var stranger = await _h.AddUserAsync("stranger");
            await _h.AddAccountAsync(stranger, "TR0000000000000009", 0m);
            await _h.AddAccountAsync(stranger, "TR0000000000000008", 0m);

            var result = await _h.Service.DeleteAccountAsync(stranger.Id, closing.Id);

            Assert.Equal("AccountNotFound", result.ErrorKey);
            Assert.True(await _h.ReadAsync(c => c.Accounts.AnyAsync(a => a.Id == closing.Id)));
        }

        [Fact]
        public async Task Closing_a_foreign_currency_account_converts_at_the_live_buy_rate_rounded_once()
        {
            var (user, closing, target) = await TwoAccountsAsync(100.50m, closingCurrency: "USD");

            var result = await _h.Service.DeleteAccountAsync(user.Id, closing.Id, target.Id);

            Assert.True(result.IsSuccess);
            Assert.Equal(3015.00m, await _h.BalanceAsync(target.AccountNumber)); // 100.50 x 30.00
        }

        [Fact]
        public async Task Closing_a_try_account_into_a_foreign_wallet_uses_the_sell_rate()
        {
            var (user, closing, target) = await TwoAccountsAsync(1000m, targetCurrency: "USD");

            await _h.Service.DeleteAccountAsync(user.Id, closing.Id, target.Id);

            Assert.Equal(32.25m, await _h.BalanceAsync(target.AccountNumber)); // 1000 / 31.00 = 32.258..., credited rounded toward zero
        }

        [Fact]
        public async Task Two_foreign_currencies_are_converted_through_try()
        {
            var (user, closing, target) = await TwoAccountsAsync(10m, closingCurrency: "EUR", targetCurrency: "USD");

            await _h.Service.DeleteAccountAsync(user.Id, closing.Id, target.Id);

            Assert.Equal(10.96m, await _h.BalanceAsync(target.AccountNumber)); // 10 x 34.00 / 31.00 = 10.9677, credited rounded toward zero
        }

        [Fact]
        public async Task Without_live_rates_a_foreign_account_is_not_closed_and_nothing_is_converted_one_to_one()
        {
            _h.Rates.Fallback = true;
            var (user, closing, target) = await TwoAccountsAsync(100m, closingCurrency: "USD");

            var result = await _h.Service.DeleteAccountAsync(user.Id, closing.Id, target.Id);

            Assert.Equal("RateUnavailable", result.ErrorKey);
            Assert.True(await _h.ReadAsync(c => c.Accounts.AnyAsync(a => a.Id == closing.Id)));
            Assert.Equal(0m, await _h.BalanceAsync(target.AccountNumber));
            Assert.Empty(await _h.ReadAsync(c => c.Transactions.ToListAsync()));
        }

        [Fact]
        public async Task A_missing_price_list_and_an_unknown_currency_are_not_guessed_either()
        {
            var (user, closing, target) = await TwoAccountsAsync(100m, closingCurrency: "USD");
            _h.Rates.Empty = true;
            Assert.Equal("RateUnavailable", (await _h.Service.DeleteAccountAsync(user.Id, closing.Id, target.Id)).ErrorKey);

            _h.Rates.Empty = false;
            var strange = await _h.AddAccountAsync(user, "TR0000000000000003", 100m, "ABC");
            Assert.Equal("RateUnavailable", (await _h.Service.DeleteAccountAsync(user.Id, strange.Id, target.Id)).ErrorKey);
            Assert.Equal(0m, await _h.BalanceAsync(target.AccountNumber));
        }

        [Fact]
        public async Task A_balance_too_small_to_convert_is_refused_instead_of_being_lost()
        {
            _h.Rates.Set("XAU", 0.10m, 0.10m);
            var (user, closing, target) = await TwoAccountsAsync(0.01m, closingCurrency: "XAU"); // 0.01 x 0.10 = 0.001 TRY

            var result = await _h.Service.DeleteAccountAsync(user.Id, closing.Id, target.Id);

            Assert.Equal("AmountTooSmall", result.ErrorKey);
            Assert.True(await _h.ReadAsync(c => c.Accounts.AnyAsync(a => a.Id == closing.Id)));
        }
    }
}
