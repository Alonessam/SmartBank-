using Microsoft.EntityFrameworkCore;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Security;
using SmartBank.Tests.Support;

namespace SmartBank.Tests.Banking
{
    /// <summary>The rules of a transfer: amounts, who may be involved, and when a one-time code is demanded.</summary>
    [Collection("EncryptionHelper")]
    public class TransferRulesTests : IDisposable
    {
        private const string Source = "TR0000000000000001";
        private const string Destination = "TR0000000000000002";

        private readonly BankingHarness _h = new();

        public void Dispose() => _h.Dispose();

        private static TransferRequestDto Transfer(decimal amount, string source = Source, string destination = Destination, string? code = null) => new()
        {
            SourceAccountNumber = source,
            DestinationAccountNumber = destination,
            Amount = amount,
            Description = "test",
            OtpCode = code
        };

        private async Task<(User Sender, Account From, Account To)> SeedAsync(decimal balance = 100_000m, string currency = "TRY", bool twoFactor = false)
        {
            var sender = await _h.AddUserAsync("sender", twoFactor);
            var receiver = await _h.AddUserAsync("receiver");
            var from = await _h.AddAccountAsync(sender, Source, balance, currency);
            var to = await _h.AddAccountAsync(receiver, Destination, 0m, currency);
            return (sender, from, to);
        }

        private async Task AddOutgoingHistoryAsync(Account from, Account to, decimal amount, int count, TimeSpan age)
        {
            for (var i = 0; i < count; i++)
            {
                _h.Context.Transactions.Add(new Transaction
                {
                    SourceAccountId = from.Id,
                    DestinationAccountId = to.Id,
                    Amount = amount,
                    Type = TransactionType.Transfer,
                    Description = "history",
                    CreatedAt = _h.Clock.UtcNow - age - TimeSpan.FromMinutes(i)
                });
            }

            await _h.Context.SaveChangesAsync();
        }

        // ---- amounts -----------------------------------------------------------------------------------------

        [Theory]
        [InlineData(0, "InvalidAmount")]
        [InlineData(-10, "InvalidAmount")]
        [InlineData(10_000_000.01, "InvalidAmount")]
        [InlineData(1.005, "InvalidAmountScale")]
        [InlineData(0.001, "InvalidAmountScale")]
        [InlineData(99.999, "InvalidAmountScale")]
        public async Task A_transfer_needs_a_positive_amount_of_at_most_ten_million_with_two_decimals(double amount, string errorKey)
        {
            var (sender, _, _) = await SeedAsync();

            var result = await _h.Service.TransferMoneyAsync(sender.Id, Transfer((decimal)amount));

            Assert.False(result.IsSuccess);
            Assert.Equal(errorKey, result.ErrorKey);
            Assert.Equal(100_000m, await _h.BalanceAsync(Source));
        }

        [Fact]
        public async Task The_smallest_amount_and_whole_and_one_decimal_amounts_are_fine()
        {
            var (sender, _, _) = await SeedAsync();

            Assert.True((await _h.Service.TransferMoneyAsync(sender.Id, Transfer(0.01m))).IsSuccess);
            Assert.True((await _h.Service.TransferMoneyAsync(sender.Id, Transfer(5m))).IsSuccess);
            Assert.True((await _h.Service.TransferMoneyAsync(sender.Id, Transfer(7.5m))).IsSuccess);
            Assert.Equal(12.51m, await _h.BalanceAsync(Destination));
        }

        [Fact]
        public async Task The_audit_entry_names_the_real_currency_and_uses_a_dot_decimal_point()
        {
            var (sender, _, _) = await SeedAsync(currency: "USD");

            await _h.Service.TransferMoneyAsync(sender.Id, Transfer(12.5m));

            var audit = await _h.ReadAsync(c => c.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "TransferMoney"));
            Assert.Equal($"Transferred 12.50 USD from {Source} to {Destination}", audit.Details);
        }

        // ---- who ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_foreign_source_and_a_missing_source_get_the_same_answer()
        {
            var (sender, _, _) = await SeedAsync();
            var stranger = await _h.AddUserAsync("stranger");
            var strangerAccount = await _h.AddAccountAsync(stranger, "TR0000000000000009", 500m);

            var foreign = await _h.Service.TransferMoneyAsync(sender.Id, Transfer(10m, source: strangerAccount.AccountNumber));
            var missing = await _h.Service.TransferMoneyAsync(sender.Id, Transfer(10m, source: "TR0000000000009999"));

            Assert.Equal("SourceAccountNotFound", foreign.ErrorKey);
            Assert.Equal(missing.ErrorKey, foreign.ErrorKey);
            Assert.Equal(missing.Message, foreign.Message);
            Assert.Equal(500m, await _h.BalanceAsync(strangerAccount.AccountNumber));
        }

        [Fact]
        public async Task The_destination_must_exist_the_currency_must_match_and_the_balance_must_cover_it()
        {
            var (sender, _, _) = await SeedAsync(balance: 50m);
            var receiver = await _h.AddUserAsync("other");
            await _h.AddAccountAsync(receiver, "TR0000000000000003", 0m, "USD");

            Assert.Equal("DestinationAccountNotFound", (await _h.Service.TransferMoneyAsync(sender.Id, Transfer(10m, destination: "TR0000000000009999"))).ErrorKey);
            Assert.Equal("CurrencyMismatch", (await _h.Service.TransferMoneyAsync(sender.Id, Transfer(10m, destination: "TR0000000000000003"))).ErrorKey);
            Assert.Equal("InsufficientFunds", (await _h.Service.TransferMoneyAsync(sender.Id, Transfer(50.01m))).ErrorKey);
            Assert.Equal("CannotTransferToSelf", (await _h.Service.TransferMoneyAsync(sender.Id, Transfer(10m, destination: Source))).ErrorKey);
        }

        // ---- the one-time-code rules -------------------------------------------------------------------------

        [Fact]
        public async Task A_big_deposit_no_longer_switches_the_unusual_amount_check_off()
        {
            // The average of what the customer usually sends is 100. A deposit of a million has the account on both sides of
            // the row and used to be counted as "spending", which pushed the average so high that nothing looked unusual.
            var (sender, from, to) = await SeedAsync();
            await AddOutgoingHistoryAsync(from, to, 100m, 3, TimeSpan.FromDays(1));
            await _h.Service.DepositMoneyAsync(sender.Id, Source, 1_000_000m);

            var result = await _h.Service.TransferMoneyAsync(sender.Id, Transfer(800m));

            Assert.Equal("SuspectedFraudHighValue", result.ErrorKey);
            Assert.Contains("100.00 TRY", result.Message);
            Assert.Equal(100_000m + 1_000_000m, await _h.BalanceAsync(Source));
        }

        [Fact]
        public async Task A_transfer_close_to_the_usual_amount_needs_no_code()
        {
            var (sender, from, to) = await SeedAsync();
            await AddOutgoingHistoryAsync(from, to, 100m, 3, TimeSpan.FromDays(1));

            var result = await _h.Service.TransferMoneyAsync(sender.Id, Transfer(400m));

            Assert.True(result.IsSuccess);
        }

        [Fact]
        public async Task The_five_times_rule_has_a_floor_of_500_try()
        {
            var (sender, from, to) = await SeedAsync();
            await AddOutgoingHistoryAsync(from, to, 10m, 3, TimeSpan.FromDays(1));

            Assert.True((await _h.Service.TransferMoneyAsync(sender.Id, Transfer(60m))).IsSuccess);   // 6 x the average, but small
            Assert.Equal("SuspectedFraudHighValue", (await _h.Service.TransferMoneyAsync(sender.Id, Transfer(600m))).ErrorKey);
        }

        [Theory]
        [InlineData(1500, null)]
        [InlineData(2500, "SuspectedFraudHighValue")]
        public async Task Only_the_last_90_days_count_so_old_history_is_treated_as_a_new_account(double amount, string? errorKey)
        {
            var (sender, from, to) = await SeedAsync();
            await AddOutgoingHistoryAsync(from, to, 100m, 3, TimeSpan.FromDays(120));

            // No recent history: the 2000 TRY limit of a new account applies, not "5 x 100".
            var result = await _h.Service.TransferMoneyAsync(sender.Id, Transfer((decimal)amount));

            Assert.Equal(errorKey, result.ErrorKey);
        }

        [Fact]
        public async Task The_clock_decides_what_counts_as_the_last_90_days()
        {
            var (sender, from, to) = await SeedAsync();
            await AddOutgoingHistoryAsync(from, to, 100m, 3, TimeSpan.FromDays(80));

            Assert.Equal("SuspectedFraudHighValue", (await _h.Service.TransferMoneyAsync(sender.Id, Transfer(800m))).ErrorKey);

            _h.Clock.Advance(TimeSpan.FromDays(20)); // the history is now 100 days old

            Assert.True((await _h.Service.TransferMoneyAsync(sender.Id, Transfer(800m))).IsSuccess);
        }

        [Fact]
        public async Task The_same_transfer_within_thirty_seconds_needs_a_code_and_after_that_does_not()
        {
            var (sender, _, _) = await SeedAsync();
            Assert.True((await _h.Service.TransferMoneyAsync(sender.Id, Transfer(50m))).IsSuccess);

            var again = await _h.Service.TransferMoneyAsync(sender.Id, Transfer(50m));
            Assert.Equal("SuspectedFraudDuplicate", again.ErrorKey);

            _h.Clock.Advance(TimeSpan.FromSeconds(31));
            Assert.True((await _h.Service.TransferMoneyAsync(sender.Id, Transfer(50m))).IsSuccess);
        }

        [Theory]
        [InlineData(2000, null)]
        [InlineData(2000.01, "SuspectedFraudHighValue")]
        public async Task A_new_account_may_send_up_to_2000_try_without_a_code(double amount, string? errorKey)
        {
            var (sender, _, _) = await SeedAsync();

            var result = await _h.Service.TransferMoneyAsync(sender.Id, Transfer((decimal)amount));

            Assert.Equal(errorKey, result.ErrorKey);
        }

        [Fact]
        public async Task Two_factor_asks_for_a_code_above_1000_try()
        {
            var (sender, _, _) = await SeedAsync(twoFactor: true);

            Assert.True((await _h.Service.TransferMoneyAsync(sender.Id, Transfer(1000m))).IsSuccess);
            var above = await _h.Service.TransferMoneyAsync(sender.Id, Transfer(1000.01m));
            Assert.Equal("Requires2FA", above.ErrorKey);
        }

        [Theory]
        [InlineData(20, null)]                      // 620 TRY: below every limit
        [InlineData(50, "Requires2FA")]             // 1550 TRY: above the 1000 TRY two-factor limit, below the 2000 new-account limit
        [InlineData(70, "SuspectedFraudHighValue")] // 2170 TRY: above the new-account limit
        public async Task Limits_in_try_apply_to_the_try_value_of_a_foreign_currency_amount(double amount, string? errorKey)
        {
            var (sender, _, _) = await SeedAsync(currency: "USD", twoFactor: true);

            var result = await _h.Service.TransferMoneyAsync(sender.Id, Transfer((decimal)amount));

            Assert.Equal(errorKey, result.ErrorKey);
        }

        [Fact]
        public async Task A_large_amount_of_gold_is_not_waved_through_because_the_number_is_small()
        {
            var (sender, _, _) = await SeedAsync(balance: 5000m, currency: "XAU");

            // 400 gram of gold is more than a million TRY, however small the number looks next to "2000 TRY".
            var result = await _h.Service.TransferMoneyAsync(sender.Id, Transfer(400m));

            Assert.Equal("SuspectedFraudHighValue", result.ErrorKey);
        }

        [Fact]
        public async Task Without_a_live_rate_a_foreign_amount_counts_as_above_every_limit()
        {
            _h.Rates.Fallback = true;
            var (sender, _, _) = await SeedAsync(currency: "USD");

            var result = await _h.Service.TransferMoneyAsync(sender.Id, Transfer(1m)); // would be 31 TRY with a live rate

            Assert.Equal("SuspectedFraudHighValue", result.ErrorKey);
        }

        [Fact]
        public async Task Try_transfers_never_need_a_rate()
        {
            _h.Rates.Fallback = true;
            _h.Rates.Empty = true;
            var (sender, _, _) = await SeedAsync();

            Assert.True((await _h.Service.TransferMoneyAsync(sender.Id, Transfer(10m))).IsSuccess);
            Assert.Equal(0, _h.Rates.Calls);
        }

        [Fact]
        public async Task The_code_mail_says_what_it_approves()
        {
            var (sender, _, _) = await SeedAsync();

            await _h.Service.TransferMoneyAsync(sender.Id, Transfer(2500m));

            Assert.Equal($"2500.00 TRY -> {Destination}", _h.Otp.LastDetail);
        }

        [Fact]
        public async Task The_code_expires_after_five_minutes_by_the_clock()
        {
            var (sender, _, _) = await SeedAsync();
            await _h.Service.TransferMoneyAsync(sender.Id, Transfer(2500m));
            var code = _h.Otp.LastCode;

            _h.Clock.Advance(OtpManager.Lifetime + TimeSpan.FromSeconds(1));
            var late = await _h.Service.TransferMoneyAsync(sender.Id, Transfer(2500m, code: code));

            Assert.Equal("InvalidOtpCode", late.ErrorKey);
            Assert.Equal(100_000m, await _h.BalanceAsync(Source));
        }

        [Fact]
        public async Task A_code_inside_its_lifetime_works()
        {
            var (sender, _, _) = await SeedAsync();
            await _h.Service.TransferMoneyAsync(sender.Id, Transfer(2500m));
            var code = _h.Otp.LastCode;

            _h.Clock.Advance(OtpManager.Lifetime - TimeSpan.FromSeconds(1));
            var inTime = await _h.Service.TransferMoneyAsync(sender.Id, Transfer(2500m, code: code));

            Assert.True(inTime.IsSuccess);
        }

        [Fact]
        public async Task A_stale_write_to_the_user_row_is_noticed_so_one_code_cannot_approve_two_transfers()
        {
            // Two requests read the same pending code. Whichever saves second must fail instead of overwriting the first.
            var (sender, _, _) = await SeedAsync();
            await _h.Service.TransferMoneyAsync(sender.Id, Transfer(2500m));

            await using var a = _h.NewContext();
            await using var b = _h.NewContext();
            var userA = await a.Users.SingleAsync(u => u.Id == sender.Id);
            var userB = await b.Users.SingleAsync(u => u.Id == sender.Id);

            OtpManager.Verify(userA, OtpPurpose.Transfer, "000000", _h.Clock.UtcNow, "x");
            OtpManager.Verify(userB, OtpPurpose.Transfer, "000000", _h.Clock.UtcNow, "x");
            await a.SaveChangesAsync();

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
        }
    }
}
