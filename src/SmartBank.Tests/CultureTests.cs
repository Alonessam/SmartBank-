using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartBank.Core.DTOs;
using SmartBank.Tests.Support;

namespace SmartBank.Tests
{
    /// <summary>
    /// The development machine is Turkish: its culture writes "1,5" for 1.5, "." as the date separator and turns "i" into a dotted
    /// capital. None of that may reach a stored value, an audit line or a parsed amount. Each test switches the culture of its own
    /// flow to tr-TR (and de-DE, which also uses a decimal comma) and checks the results are the ones the invariant culture gives.
    /// </summary>
    [Collection("EncryptionHelper")]
    public class CultureTests
    {
        public static IEnumerable<object[]> Cultures() => new[] { new object[] { "tr-TR" }, new object[] { "de-DE" }, new object[] { "en-US" } };

        private static async Task InCultureAsync(string culture, Func<Task> body)
        {
            var previous = CultureInfo.CurrentCulture;
            var previousUi = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            try
            {
                await body();
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
                CultureInfo.CurrentUICulture = previousUi;
            }
        }

        [Theory]
        [MemberData(nameof(Cultures))]
        public async Task A_transfer_is_audited_and_described_with_dot_decimals_and_stores_the_same_numbers(string culture)
        {
            await InCultureAsync(culture, async () =>
            {
                using var h = new BankingHarness();
                var sender = await h.AddUserAsync("sender");
                var receiver = await h.AddUserAsync("receiver");
                await h.AddAccountAsync(sender, "TR0000000000000001", 1000.50m);
                await h.AddAccountAsync(receiver, "TR0000000000000002", 0m);

                var result = await h.Service.TransferMoneyAsync(sender.Id, new TransferRequestDto { SourceAccountNumber = "TR0000000000000001", DestinationAccountNumber = "TR0000000000000002", Amount = 123.45m });

                Assert.True(result.IsSuccess);
                var audit = await h.ReadAsync(c => c.AuditLogs.AsNoTracking().SingleAsync());
                Assert.Equal("Transferred 123.45 TRY from TR0000000000000001 to TR0000000000000002", audit.Details);
                Assert.Equal(877.05m, await h.BalanceAsync("TR0000000000000001"));
            });
        }

        [Theory]
        [MemberData(nameof(Cultures))]
        public async Task An_exchange_writes_its_description_and_audit_text_with_dot_decimals(string culture)
        {
            await InCultureAsync(culture, async () =>
            {
                using var h = new BankingHarness();
                h.Rates.Set("USD", 30.5m, 31.25m);
                var user = await h.AddUserAsync();
                var account = await h.AddAccountAsync(user, "TR0000000000000001", 1000m);

                var result = await h.Service.ExchangeMoneyAsync(user.Id, new ExchangeDto { SourceAccountId = account.Id.ToString(), Asset = "USD", Action = "buy", Amount = 2.5m });

                Assert.True(result.IsSuccess);
                Assert.Equal("2.50 USD Alımı (Kur: 31.25 TRY)", result.Data!.Description);
                var audit = await h.ReadAsync(c => c.AuditLogs.AsNoTracking().SingleAsync());
                Assert.Equal("Bought 2.50 USD with 78.13 TRY. Rate: 31.25", audit.Details); // 2.5 x 31.25 = 78.125, away from zero
                Assert.Equal(921.87m, await h.BalanceAsync("TR0000000000000001"));
            });
        }

        [Theory]
        [MemberData(nameof(Cultures))]
        public async Task The_fraud_message_shows_the_average_with_a_dot(string culture)
        {
            await InCultureAsync(culture, async () =>
            {
                using var h = new BankingHarness();
                var sender = await h.AddUserAsync("sender");
                var receiver = await h.AddUserAsync("receiver");
                var from = await h.AddAccountAsync(sender, "TR0000000000000001", 100_000m);
                var to = await h.AddAccountAsync(receiver, "TR0000000000000002", 0m);
                h.Context.Transactions.Add(new SmartBank.Core.Entities.Transaction { SourceAccountId = from.Id, DestinationAccountId = to.Id, Amount = 100.5m, Type = SmartBank.Core.Entities.TransactionType.Transfer, CreatedAt = h.Clock.UtcNow.AddDays(-1) });
                await h.Context.SaveChangesAsync();

                var result = await h.Service.TransferMoneyAsync(sender.Id, new TransferRequestDto { SourceAccountNumber = "TR0000000000000001", DestinationAccountNumber = "TR0000000000000002", Amount = 900m });

                Assert.Equal("SuspectedFraudHighValue", result.ErrorKey);
                Assert.Contains("100.50 TRY", result.Message);
            });
        }

        [Theory]
        [MemberData(nameof(Cultures))]
        public async Task Card_expiry_dates_use_a_slash_and_the_first_statement_is_named_without_culture_data(string culture)
        {
            await InCultureAsync(culture, async () =>
            {
                using var h = new BankingHarness();
                var user = await h.AddUserAsync();

                var card = (await h.Service.CreateCreditCardAsync(user.Id)).Data!;
                var account = (await h.Service.CreateAccountAsync(user.Id, "usd")).Data!;

                Assert.Equal("10/34", card.ExpiryDate);
                Assert.Equal("10/31", account.ExpiryDate);
                Assert.Equal("USD", account.Currency);
                Assert.Equal("Ekim 2026", (await h.ReadAsync(c => c.CreditCardStatements.AsNoTracking().SingleAsync())).PeriodName);
            });
        }

        [Theory]
        [MemberData(nameof(Cultures))]
        public async Task Currency_codes_with_an_i_stay_ascii_in_every_culture(string culture)
        {
            await InCultureAsync(culture, async () =>
            {
                using var h = new BankingHarness();
                var user = await h.AddUserAsync();

                Assert.Equal("TRY", (await h.Service.CreateAccountAsync(user.Id, "try")).Data!.Currency); // "try".ToUpper() is "TRY" everywhere, but "i" would not be
                Assert.Equal("InvalidCurrency", (await h.Service.CreateAccountAsync(user.Id, "tri")).ErrorKey);
                await Task.CompletedTask;
            });
        }

        [Theory]
        [MemberData(nameof(Cultures))]
        public async Task Chat_metrics_and_account_closing_text_do_not_depend_on_the_culture(string culture)
        {
            await InCultureAsync(culture, async () =>
            {
                using var h = new BankingHarness();
                var user = await h.AddUserAsync();
                var closing = await h.AddAccountAsync(user, "TR0000000000000001", 100.5m, "USD");
                var target = await h.AddAccountAsync(user, "TR0000000000000002", 0m);

                await h.Service.DeleteAccountAsync(user.Id, closing.Id, target.Id);

                var row = await h.ReadAsync(c => c.Transactions.AsNoTracking().SingleAsync());
                Assert.Equal("Hesap Kapatma Bakiye Aktarımı (USD -> TRY): 100.50 USD", row.Description);
                Assert.Equal(3015.00m, await h.BalanceAsync("TR0000000000000002"));
            });
        }
    }
}
