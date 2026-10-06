using Microsoft.EntityFrameworkCore;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Tests.Support;

namespace SmartBank.Tests.Banking
{
    /// <summary>Buying and selling USD, EUR, gold and silver against TRY: exact amounts, safe account handling, no trading at stand-in prices.</summary>
    [Collection("EncryptionHelper")]
    public class ExchangeTests : IDisposable
    {
        private readonly BankingHarness _h = new();

        public void Dispose() => _h.Dispose();

        private static ExchangeDto Buy(Account source, string asset, decimal amount, string action = "buy") => new()
        {
            SourceAccountId = source.Id.ToString(),
            Asset = asset,
            Action = action,
            Amount = amount
        };

        [Fact]
        public async Task Buying_a_currency_opens_the_wallet_and_moves_exactly_the_rounded_cost()
        {
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(tryAccount, "USD", 10m)); // 10 x 31.00

            Assert.True(result.IsSuccess);
            Assert.Equal(310.00m, result.Data!.Amount);
            Assert.Equal(690.00m, await _h.BalanceAsync("TR0000000000000001"));

            var usd = await _h.ReadAsync(c => c.Accounts.AsNoTracking().SingleAsync(a => a.Currency == "USD"));
            Assert.Equal(10.00m, usd.Balance);
            Assert.Equal(usd.AccountNumber, result.Data.DestinationAccountNumber);

            var ledger = await _h.ReadAsync(c => c.Transactions.AsNoTracking().ToListAsync());
            Assert.Equal(310.00m, Assert.Single(ledger).Amount);
            Assert.Contains(await _h.ReadAsync(c => c.AuditLogs.AsNoTracking().ToListAsync()), a => a.Action == "ExchangeBuy" && a.Details.Contains("10.00 USD") && a.Details.Contains("310.00 TRY"));
        }

        [Fact]
        public async Task The_cost_is_rounded_once_and_the_balance_the_ledger_and_the_answer_agree()
        {
            _h.Rates.Set("USD", 30.01m, 31.0049m);
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(tryAccount, "USD", 1.10m)); // 1.10 x 31.0049 = 34.10539

            Assert.True(result.IsSuccess);
            Assert.Equal(34.11m, result.Data!.Amount);
            Assert.Equal(965.89m, await _h.BalanceAsync("TR0000000000000001"));
            Assert.Equal(34.11m, (await _h.ReadAsync(c => c.Transactions.AsNoTracking().SingleAsync())).Amount);
        }

        [Fact]
        public async Task A_half_cent_rounds_away_from_zero_not_to_even()
        {
            _h.Rates.Set("USD", 30.01m, 31.00m);
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 0m);
            var usdAccount = await _h.AddAccountAsync(user, "TR0000000000000002", 10m, "USD");

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(usdAccount, "USD", 0.50m, "sell")); // 0.50 x 30.01 = 15.005

            Assert.True(result.IsSuccess);
            Assert.Equal(15.01m, await _h.BalanceAsync("TR0000000000000001"));
            Assert.Equal(9.50m, await _h.BalanceAsync("TR0000000000000002"));
        }

        [Fact]
        public async Task Selling_credits_the_oldest_try_demand_account_at_the_buy_rate()
        {
            var user = await _h.AddUserAsync();
            var older = await _h.AddAccountAsync(user, "TR0000000000000001", 0m, createdAt: _h.Clock.UtcNow.AddDays(-5));
            await _h.AddAccountAsync(user, "TR0000000000000002", 0m, createdAt: _h.Clock.UtcNow.AddDays(-1));
            var usdAccount = await _h.AddAccountAsync(user, "TR0000000000000003", 10m, "USD");

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(usdAccount, "USD", 4m, "sell")); // 4 x 30.00

            Assert.True(result.IsSuccess);
            Assert.Equal(120.00m, await _h.BalanceAsync(older.AccountNumber));
            Assert.Equal(0m, await _h.BalanceAsync("TR0000000000000002"));
            Assert.Equal(6.00m, await _h.BalanceAsync("TR0000000000000003"));
            Assert.Equal(4.00m, result.Data!.Amount); // a sale is recorded as the quantity sold
        }

        [Fact]
        public async Task A_buy_goes_to_the_oldest_wallet_of_that_currency()
        {
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);
            await _h.AddAccountAsync(user, "TR0000000000000002", 0m, "USD", createdAt: _h.Clock.UtcNow.AddDays(-1));
            await _h.AddAccountAsync(user, "TR0000000000000003", 0m, "USD", createdAt: _h.Clock.UtcNow.AddDays(-9));

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(tryAccount, "USD", 1m));

            Assert.True(result.IsSuccess);
            Assert.Equal("TR0000000000000003", result.Data!.DestinationAccountNumber);
            Assert.Equal(1.00m, await _h.BalanceAsync("TR0000000000000003"));
            Assert.Equal(0m, await _h.BalanceAsync("TR0000000000000002"));
        }

        [Theory]
        [InlineData("usd")]
        [InlineData("Usd")]
        [InlineData(" USD ")]
        public async Task The_asset_is_normalised_so_one_wallet_is_reused(string spelling)
        {
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);

            Assert.True((await _h.Service.ExchangeMoneyAsync(user.Id, Buy(tryAccount, spelling, 1m))).IsSuccess);
            Assert.True((await _h.Service.ExchangeMoneyAsync(user.Id, Buy(tryAccount, spelling, 1m))).IsSuccess);

            var wallets = await _h.ReadAsync(c => c.Accounts.AsNoTracking().Where(a => a.Currency == "USD").ToListAsync());
            Assert.Equal(2.00m, Assert.Single(wallets).Balance);
        }

        [Theory]
        [InlineData("GBP")]
        [InlineData("TRY")]
        [InlineData("")]
        [InlineData("ABC")]
        [InlineData("DROP TABLE")]
        public async Task Only_the_four_tradable_assets_are_accepted_and_nothing_is_created_otherwise(string asset)
        {
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(tryAccount, asset, 1m));

            Assert.False(result.IsSuccess);
            Assert.Equal("InvalidCurrency", result.ErrorKey);
            Assert.Equal(1, await _h.ReadAsync(c => c.Accounts.CountAsync()));
            Assert.Equal(1000m, await _h.BalanceAsync("TR0000000000000001"));
        }

        [Theory]
        [InlineData("hold")]
        [InlineData("")]
        [InlineData("exchange")]
        public async Task The_action_must_be_buy_or_sell(string action)
        {
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(tryAccount, "USD", 1m, action));

            Assert.Equal("InvalidAction", result.ErrorKey);
        }

        [Theory]
        [InlineData(1.005)]
        [InlineData(0.001)]
        [InlineData(2.3456)]
        public async Task More_than_two_decimals_is_refused(double amount)
        {
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(tryAccount, "USD", (decimal)amount));

            Assert.Equal("InvalidAmountScale", result.ErrorKey);
            Assert.Equal(1000m, await _h.BalanceAsync("TR0000000000000001"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        [InlineData(10_000_000.01)]
        public async Task Zero_negative_and_huge_amounts_are_refused(double amount)
        {
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(tryAccount, "USD", (decimal)amount));

            Assert.Equal("InvalidAmount", result.ErrorKey);
        }

        [Fact]
        public async Task A_value_that_rounds_to_nothing_is_refused_instead_of_being_given_away()
        {
            _h.Rates.Set("USD", 0.20m, 0.20m);
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);
            var usdAccount = await _h.AddAccountAsync(user, "TR0000000000000002", 10m, "USD");

            var buy = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(tryAccount, "USD", 0.01m)); // 0.002 TRY
            var sell = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(usdAccount, "USD", 0.01m, "sell"));

            Assert.Equal("AmountTooSmall", buy.ErrorKey);
            Assert.Equal("AmountTooSmall", sell.ErrorKey);
            Assert.Equal(10m, await _h.BalanceAsync("TR0000000000000002"));
        }

        [Fact]
        public async Task A_stand_in_price_is_never_traded_at_and_no_account_is_created()
        {
            _h.Rates.Fallback = true;
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(tryAccount, "USD", 10m));

            Assert.Equal("RateUnavailable", result.ErrorKey);
            Assert.Equal(1, await _h.ReadAsync(c => c.Accounts.CountAsync()));
            Assert.Equal(1000m, await _h.BalanceAsync("TR0000000000000001"));
            Assert.Empty(await _h.ReadAsync(c => c.Transactions.ToListAsync()));
        }

        [Fact]
        public async Task A_missing_price_is_reported_before_anything_is_touched()
        {
            _h.Rates.Empty = true;
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(tryAccount, "USD", 10m));

            Assert.Equal("RateNotFound", result.ErrorKey);
            Assert.Equal(1, await _h.ReadAsync(c => c.Accounts.CountAsync()));
        }

        [Fact]
        public async Task A_buy_that_cannot_be_paid_leaves_no_new_wallet_behind()
        {
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 100m);

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(tryAccount, "USD", 10m)); // costs 310

            Assert.Equal("InsufficientFunds", result.ErrorKey);
            Assert.Equal(1, await _h.ReadAsync(c => c.Accounts.CountAsync()));
            Assert.Equal(100m, await _h.BalanceAsync("TR0000000000000001"));
        }

        [Fact]
        public async Task A_sale_cannot_exceed_the_wallet()
        {
            var user = await _h.AddUserAsync();
            await _h.AddAccountAsync(user, "TR0000000000000001", 0m);
            var usdAccount = await _h.AddAccountAsync(user, "TR0000000000000002", 5m, "USD");

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(usdAccount, "USD", 5.01m, "sell"));

            Assert.Equal("InsufficientFunds", result.ErrorKey);
            Assert.Equal(5m, await _h.BalanceAsync("TR0000000000000002"));
        }

        [Fact]
        public async Task A_sale_needs_a_try_account_to_receive_the_money()
        {
            var user = await _h.AddUserAsync();
            var usdAccount = await _h.AddAccountAsync(user, "TR0000000000000002", 5m, "USD");

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(usdAccount, "USD", 1m, "sell"));

            Assert.Equal("TryAccountNotFound", result.ErrorKey);
            Assert.Equal(5m, await _h.BalanceAsync("TR0000000000000002"));
        }

        [Fact]
        public async Task Buying_needs_a_try_source_and_selling_needs_a_source_in_the_asset()
        {
            var user = await _h.AddUserAsync();
            await _h.AddAccountAsync(user, "TR0000000000000001", 500m);
            var usdAccount = await _h.AddAccountAsync(user, "TR0000000000000002", 5m, "USD");

            var buyFromUsd = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(usdAccount, "EUR", 1m));
            var sellEurFromUsd = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(usdAccount, "EUR", 1m, "sell"));

            Assert.Equal("InvalidExchangeSource", buyFromUsd.ErrorKey);
            Assert.Equal("InvalidExchangeSource", sellEurFromUsd.ErrorKey);
        }

        [Fact]
        public async Task Somebody_elses_account_cannot_be_the_source()
        {
            var owner = await _h.AddUserAsync("owner");
            var thief = await _h.AddUserAsync("thief");
            var ownerAccount = await _h.AddAccountAsync(owner, "TR0000000000000001", 1000m);
            await _h.AddAccountAsync(thief, "TR0000000000000002", 0m);

            var result = await _h.Service.ExchangeMoneyAsync(thief.Id, Buy(ownerAccount, "USD", 1m));

            Assert.Equal("AccountNotFound", result.ErrorKey);
            Assert.Equal(1000m, await _h.BalanceAsync("TR0000000000000001"));
        }

        [Fact]
        public async Task A_bad_source_id_is_reported()
        {
            var user = await _h.AddUserAsync();

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, new ExchangeDto { SourceAccountId = "not-a-guid", Asset = "USD", Action = "buy", Amount = 1m });

            Assert.Equal("InvalidSourceAccount", result.ErrorKey);
        }

        [Fact]
        public async Task The_account_limit_also_applies_to_wallets_opened_by_a_buy()
        {
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);
            for (var i = 2; i <= 10; i++) await _h.AddAccountAsync(user, $"TR00000000000000{i:D2}", 0m, "EUR");

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(tryAccount, "USD", 1m));

            Assert.Equal("AccountLimitReached", result.ErrorKey);
            Assert.Equal(1000m, await _h.BalanceAsync("TR0000000000000001"));
        }

        [Fact]
        public async Task A_buy_and_a_sale_of_the_same_amount_lose_only_the_spread()
        {
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);

            var bought = await _h.Service.ExchangeMoneyAsync(user.Id, Buy(tryAccount, "XAU", 0.20m)); // 0.20 x 3100 = 620
            var goldAccount = await _h.ReadAsync(c => c.Accounts.AsNoTracking().SingleAsync(a => a.Currency == "XAU"));
            var sold = await _h.Service.ExchangeMoneyAsync(user.Id, new ExchangeDto { SourceAccountId = goldAccount.Id.ToString(), Asset = "XAU", Action = "sell", Amount = 0.20m }); // 0.20 x 3000 = 600

            Assert.True(bought.IsSuccess);
            Assert.True(sold.IsSuccess);
            Assert.Equal(980.00m, await _h.BalanceAsync("TR0000000000000001")); // 1000 - 620 + 600: the 20 TRY spread
            Assert.Equal(0m, await _h.BalanceAsync(goldAccount.AccountNumber));
        }
    }
}
