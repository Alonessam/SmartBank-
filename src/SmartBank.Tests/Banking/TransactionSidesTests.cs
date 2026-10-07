using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Tests.Support;

namespace SmartBank.Tests.Banking
{
    /// <summary>Every transaction says what each side moved and in which currency (sourceCurrency, destinationAmount, ...).</summary>
    [Collection("EncryptionHelper")]
    public class TransactionSidesTests : IDisposable
    {
        private readonly BankingHarness _h = new();

        public void Dispose() => _h.Dispose();

        private static ExchangeDto Exchange(Account source, string asset, decimal amount, string action) => new()
        {
            SourceAccountId = source.Id.ToString(),
            Asset = asset,
            Action = action,
            Amount = amount
        };

        private async Task<TransactionDto> LatestAsync(User user, Account account)
        {
            var history = await _h.Service.GetTransactionsAsync(account.Id, user.Id);
            Assert.True(history.IsSuccess);
            return history.Data!.First();
        }

        [Fact]
        public async Task A_purchase_debits_the_try_cost_and_credits_the_quantity_in_the_answer_and_in_the_history()
        {
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 1000m);

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Exchange(tryAccount, "USD", 10m, "buy")); // 10 x 31.00

            Assert.True(result.IsSuccess);
            foreach (var row in new[] { result.Data!, await LatestAsync(user, tryAccount) })
            {
                Assert.Equal(310.00m, row.Amount);
                Assert.Equal("TRY", row.SourceCurrency);
                Assert.Equal(310.00m, row.SourceAmount);
                Assert.Equal("USD", row.DestinationCurrency);
                Assert.Equal(10.00m, row.DestinationAmount);
            }
        }

        [Fact]
        public async Task A_sale_debits_the_quantity_and_credits_the_rounded_proceeds_in_the_answer_and_in_the_history()
        {
            _h.Rates.Set("USD", 30.0049m, 31.00m);
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 0m);
            var usdAccount = await _h.AddAccountAsync(user, "TR0000000000000002", 10m, "USD");

            var result = await _h.Service.ExchangeMoneyAsync(user.Id, Exchange(usdAccount, "USD", 1.10m, "sell")); // 1.10 x 30.0049 = 33.00539

            Assert.True(result.IsSuccess);
            foreach (var row in new[] { result.Data!, await LatestAsync(user, usdAccount) })
            {
                Assert.Equal("USD", row.SourceCurrency);
                Assert.Equal(1.10m, row.SourceAmount);
                Assert.Equal("TRY", row.DestinationCurrency);
                Assert.Equal(33.01m, row.DestinationAmount);
            }

            Assert.Equal(33.01m, await _h.BalanceAsync("TR0000000000000001")); // the shown proceeds are what was credited
        }

        [Fact]
        public async Task A_plain_transfer_carries_the_amount_on_both_sides_with_each_accounts_currency()
        {
            var user = await _h.AddUserAsync();
            var from = await _h.AddAccountAsync(user, "TR0000000000000001", 500m);
            await _h.AddAccountAsync(user, "TR0000000000000002", 0m);

            var result = await _h.Service.TransferMoneyAsync(user.Id, new TransferRequestDto
            {
                SourceAccountNumber = "TR0000000000000001",
                DestinationAccountNumber = "TR0000000000000002",
                Amount = 25m
            });

            Assert.True(result.IsSuccess, result.ErrorKey);
            foreach (var row in new[] { result.Data!, await LatestAsync(user, from) })
            {
                Assert.Equal(("TRY", 25m, "TRY", 25m), (row.SourceCurrency, row.SourceAmount, row.DestinationCurrency, row.DestinationAmount));
            }
        }

        [Fact]
        public async Task A_deposit_has_only_a_destination_side()
        {
            var user = await _h.AddUserAsync();
            var account = await _h.AddAccountAsync(user, "TR0000000000000001", 0m, "USD");

            var result = await _h.Service.DepositMoneyAsync(user.Id, "TR0000000000000001", 40m);

            Assert.True(result.IsSuccess);
            foreach (var row in new[] { result.Data!, await LatestAsync(user, account) })
            {
                Assert.Null(row.SourceCurrency);
                Assert.Null(row.SourceAmount);
                Assert.Equal("USD", row.DestinationCurrency);
                Assert.Equal(40m, row.DestinationAmount);
            }
        }

        [Fact]
        public async Task A_card_payment_has_only_a_source_side()
        {
            var user = await _h.AddUserAsync();
            var account = await _h.AddAccountAsync(user, "TR0000000000000001", 500m);
            var card = await _h.AddCardAsync(user, debt: 100m);
            await _h.AddStatementAsync(card, 100m);

            var paid = await _h.Service.PayCreditCardDebtAsync(user.Id, card.Id, new PayCreditCardDebtDto { SourceAccountNumber = "TR0000000000000001", Amount = 60m });

            Assert.True(paid.IsSuccess, paid.ErrorKey);
            var row = await LatestAsync(user, account);
            Assert.Equal(("TRY", 60m), (row.SourceCurrency, row.SourceAmount));
            Assert.Null(row.DestinationCurrency);
            Assert.Null(row.DestinationAmount);
        }

        [Fact]
        public async Task The_closing_transfer_of_a_foreign_currency_account_shows_both_amounts_although_the_closed_account_is_gone()
        {
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 0m);
            var usd = await _h.AddAccountAsync(user, "TR0000000000000002", 10m, "USD");

            var closed = await _h.Service.DeleteAccountAsync(user.Id, usd.Id, tryAccount.Id);

            Assert.True(closed.IsSuccess, closed.ErrorKey);
            var row = await LatestAsync(user, tryAccount);
            Assert.Null(row.SourceAccountNumber); // the closed account lost its link ...
            Assert.Equal(("USD", 10.00m), (row.SourceCurrency, row.SourceAmount)); // ... but the row still says what left it
            Assert.Equal(("TRY", 300.00m), (row.DestinationCurrency, row.DestinationAmount)); // 10 x 30.00 (the bank pays the buy price)
        }

        [Fact]
        public async Task A_row_whose_text_does_not_match_its_amount_is_shown_plainly_not_guessed()
        {
            var user = await _h.AddUserAsync();
            var tryAccount = await _h.AddAccountAsync(user, "TR0000000000000001", 0m);
            var usd = await _h.AddAccountAsync(user, "TR0000000000000002", 0m, "USD");
            await using (var context = _h.NewContext())
            {
                context.Transactions.Add(new Transaction
                {
                    SourceAccountId = usd.Id,
                    DestinationAccountId = tryAccount.Id,
                    Amount = 7m, // the text says 5.00 USD were sold: the two do not agree
                    Description = "5.00 USD Satışı (Kur: 30 TRY)",
                    Type = TransactionType.Transfer,
                    CreatedAt = DateTime.UtcNow
                });
                await context.SaveChangesAsync();
            }

            var row = await LatestAsync(user, tryAccount);

            Assert.Equal(("USD", 7m, "TRY", 7m), (row.SourceCurrency, row.SourceAmount, row.DestinationCurrency, row.DestinationAmount));
        }
    }
}
