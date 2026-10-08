using SmartBank.Tests.Support;

namespace SmartBank.Tests.Banking
{
    /// <summary>
    /// Closing an account into one of another currency credits a converted amount. It is rounded toward zero: rounding to the
    /// nearest cent turned 50 TRY into 0.02 XAU (worth 62 TRY) and let a customer print money by closing small accounts
    /// into a gold account and selling the gold.
    /// </summary>
    [Collection("EncryptionHelper")]
    public class ConversionRoundingTests : IDisposable
    {
        private readonly BankingHarness _h = new();

        public void Dispose() => _h.Dispose();

        private async Task<(decimal Credited, decimal Worth)> CloseIntoAsync(string targetCurrency, decimal tryBalance)
        {
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", tryBalance);
            var target = await _h.AddAccountAsync(user, "TR0000000000000002", 0m, targetCurrency);
            await _h.AddAccountAsync(user, "TR0000000000000003", 0m); // keeps "at least one account" satisfied either way

            var closed = await _h.Service.DeleteAccountAsync(user.Id, tryAccount.Id, target.Id);
            Assert.True(closed.IsSuccess, closed.ErrorKey);

            var accounts = (await _h.Service.GetAccountsAsync(user.Id)).Data!;
            var credited = accounts.Single(a => a.AccountNumber == target.AccountNumber).Balance;
            var sellPrice = targetCurrency == "XAU" ? 3100m : 31m; // what the bank charges for it (FakeMarketRates)
            return (credited, credited * sellPrice);
        }

        [Fact]
        public async Task The_converted_amount_never_buys_more_than_the_money_paid_in()
        {
            foreach (var (currency, tryBalance) in new[] { ("XAU", 50m), ("XAU", 98m), ("XAU", 200m), ("USD", 100m), ("USD", 31.99m) })
            {
                using var h = new BankingHarness();
                var user = await h.AddUserAsync();
                var tryAccount = await h.AddAccountAsync(user, "TR0000000000000001", tryBalance);
                var target = await h.AddAccountAsync(user, "TR0000000000000002", 0m, currency);
                await h.AddAccountAsync(user, "TR0000000000000003", 0m);

                var closed = await h.Service.DeleteAccountAsync(user.Id, tryAccount.Id, target.Id);
                Assert.True(closed.IsSuccess, $"{currency} {tryBalance}: {closed.ErrorKey}");

                var credited = (await h.Service.GetAccountsAsync(user.Id)).Data!.Single(a => a.AccountNumber == target.AccountNumber).Balance;
                var sell = currency == "XAU" ? 3100m : 31m;
                Assert.True(credited * sell <= tryBalance, $"{tryBalance} TRY became {credited} {currency}, worth {credited * sell} TRY");
            }
        }

        [Fact]
        public async Task Fifty_lira_into_gold_gives_one_hundredth_of_a_gram_not_two()
        {
            var (credited, _) = await CloseIntoAsync("XAU", 50m); // 50 / 3100 = 0.0161
            Assert.Equal(0.01m, credited);
        }

        [Fact]
        public async Task An_amount_that_rounds_down_to_nothing_is_refused_instead_of_rounded_up_to_a_cent()
        {
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 20m);
            var xau = await _h.AddAccountAsync(user, "TR0000000000000002", 0m, "XAU");

            var closed = await _h.Service.DeleteAccountAsync(user.Id, tryAccount.Id, xau.Id); // 20 / 3100 = 0.0065

            Assert.False(closed.IsSuccess);
            Assert.Equal("AmountTooSmall", closed.ErrorKey);
        }
    }
}
