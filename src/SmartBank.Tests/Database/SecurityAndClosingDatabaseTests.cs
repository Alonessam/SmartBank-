using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Security;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Services;
using SmartBank.Tests.Support;

namespace SmartBank.Tests.Database
{
    /// <summary>
    /// Things that only a real database can prove: the Restrict foreign key and bulk updates when an account is closed, and the
    /// version token on users when requests collide (one-time codes, wrong-guess counters, registrations, unique indexes).
    /// </summary>
    // One collection: these classes each open dozens of connections, so they run one after another, not in parallel.
    [Collection("Database")]
    public class SecurityAndClosingDatabaseTests
    {
        private const string Pin = "123456";

        private static void EnsureEncryptionConfigured()
        {
            try { EncryptionHelper.Encrypt("probe"); }
            catch (InvalidOperationException) { EncryptionHelper.Configure(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))); }
        }

        private static AuthService NewAuth(SmartBankDbContext context) => new(
            context,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["JwtSettings:Key"] = "unit-test-signing-key-0123456789-abcdef" }).Build(),
            new FakeOtpDelivery());

        private static async Task<User> AddUserAsync(TestDb db, string name, bool twoFactor = false)
        {
            await using var context = db.NewContext();
            var user = new User
            {
                Username = name,
                Tckn = TestTckn.Next(),
                Email = name + "@test.example",
                FirstName = name,
                LastName = "Tester",
                FullName = name + " Tester",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Pin, 4),
                TwoFactorEnabled = twoFactor
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();
            return user;
        }

        private static async Task<Account> AddAccountAsync(TestDb db, User user, string number, decimal balance, string currency = "TRY")
        {
            await using var context = db.NewContext();
            var account = new Account { UserId = user.Id, AccountNumber = number, AccountCode = "ACC-" + number[^6..], Balance = balance, Currency = currency, EncryptedCardNumber = "x" };
            context.Accounts.Add(account);
            await context.SaveChangesAsync();
            return account;
        }

        // ---- closing an account ------------------------------------------------------------------------------

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Closing_an_account_with_history_works_with_the_restrict_foreign_key_and_keeps_every_row(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var alice = await AddUserAsync(db, "alice");
            var bob = await AddUserAsync(db, "bob");
            var closing = await AddAccountAsync(db, alice, "TR0000000000000001", 250m);
            var target = await AddAccountAsync(db, alice, "TR0000000000000002", 10m);
            var others = await AddAccountAsync(db, bob, "TR0000000000000003", 100m);
            await using (var seed = db.NewContext())
            {
                seed.Transactions.AddRange(
                    new Transaction { SourceAccountId = closing.Id, DestinationAccountId = others.Id, Amount = 5m, Type = TransactionType.Transfer, Description = "out" },
                    new Transaction { SourceAccountId = others.Id, DestinationAccountId = closing.Id, Amount = 7m, Type = TransactionType.Transfer, Description = "in" },
                    new Transaction { SourceAccountId = closing.Id, DestinationAccountId = closing.Id, Amount = 9m, Type = TransactionType.Deposit, Description = "deposit" });
                seed.StandingOrders.Add(new StandingOrder { UserId = alice.Id, SourceAccountNumber = closing.AccountNumber, DestinationAccountNumber = others.AccountNumber, Amount = 1m });
                seed.SavedContacts.AddRange(
                    new SavedContact { UserId = bob.Id, AccountNumber = closing.AccountNumber, Alias = "gone" },
                    new SavedContact { UserId = bob.Id, AccountNumber = target.AccountNumber, Alias = "stays" });
                await seed.SaveChangesAsync();
            }

            var result = await ConcurrencyHarness.InNewRequestAsync(db, s => s.DeleteAccountAsync(alice.Id, closing.Id, target.Id));

            Assert.True(result.IsSuccess, result.Message);
            await using var verify = db.NewContext();
            Assert.False(await verify.Accounts.AnyAsync(a => a.Id == closing.Id));
            Assert.Equal(260m, (await verify.Accounts.AsNoTracking().SingleAsync(a => a.Id == target.Id)).Balance);

            var rows = await verify.Transactions.AsNoTracking().ToListAsync();
            Assert.Equal(4, rows.Count); // three old rows and the closing row: nothing was deleted
            Assert.DoesNotContain(rows, r => r.SourceAccountId == closing.Id || r.DestinationAccountId == closing.Id);
            var closingRow = rows.Single(r => r.Description.StartsWith("Hesap Kapatma"));
            Assert.Null(closingRow.SourceAccountId);
            Assert.Equal(target.Id, closingRow.DestinationAccountId);
            Assert.Equal(250m, closingRow.Amount);

            Assert.False((await verify.StandingOrders.AsNoTracking().SingleAsync()).IsActive);
            Assert.Equal("stays", (await verify.SavedContacts.AsNoTracking().SingleAsync()).Alias);
            Assert.Contains(await verify.AuditLogs.AsNoTracking().ToListAsync(), a => a.Action == "DeleteAccount");
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Closing_a_foreign_currency_account_converts_and_a_missing_rate_changes_nothing(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var alice = await AddUserAsync(db, "alice");
            var usd = await AddAccountAsync(db, alice, "TR0000000000000001", 100.50m, "USD");
            var tryAccount = await AddAccountAsync(db, alice, "TR0000000000000002", 0m);
            var rates = new FakeMarketRates { Fallback = true };

            var refused = await ConcurrencyHarness.InNewRequestAsync(db, rates, new FakeOtpDelivery(), s => s.DeleteAccountAsync(alice.Id, usd.Id, tryAccount.Id));
            Assert.Equal("RateUnavailable", refused.ErrorKey);
            await using (var check = db.NewContext())
            {
                Assert.True(await check.Accounts.AnyAsync(a => a.Id == usd.Id));
                Assert.Empty(await check.Transactions.ToListAsync());
            }

            rates.Fallback = false;
            var closed = await ConcurrencyHarness.InNewRequestAsync(db, rates, new FakeOtpDelivery(), s => s.DeleteAccountAsync(alice.Id, usd.Id, tryAccount.Id));

            Assert.True(closed.IsSuccess);
            await using var verify = db.NewContext();
            Assert.Equal(3015.00m, (await verify.Accounts.AsNoTracking().SingleAsync(a => a.Id == tryAccount.Id)).Balance); // 100.50 x 30.00
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Deposits_racing_with_the_closing_of_an_account_never_lose_or_create_money(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var alice = await AddUserAsync(db, "alice");
            var closing = await AddAccountAsync(db, alice, "TR0000000000000001", 250m);
            var target = await AddAccountAsync(db, alice, "TR0000000000000002", 10m);

            var operations = new List<Func<Task<(string Kind, decimal Amount, bool Ok)>>>();
            for (var i = 1; i <= 8; i++)
            {
                var amount = i;
                operations.Add(async () => ("deposit", amount, (await ConcurrencyHarness.InNewRequestAsync(db, s => s.DepositMoneyAsync(alice.Id, closing.AccountNumber, amount))).IsSuccess));
            }

            operations.Add(async () => ("close", 0m, (await ConcurrencyHarness.InNewRequestAsync(db, s => s.DeleteAccountAsync(alice.Id, closing.Id, target.Id))).IsSuccess));

            var outcomes = await ConcurrencyHarness.RunTogetherAsync(operations);

            await using var verify = db.NewContext();
            var closed = outcomes.Single(o => o.Kind == "close").Ok;
            var deposited = outcomes.Where(o => o.Kind == "deposit" && o.Ok).Sum(o => o.Amount);
            var targetBalance = (await verify.Accounts.AsNoTracking().SingleAsync(a => a.Id == target.Id)).Balance;
            var closingStill = await verify.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == closing.Id);

            if (closed)
            {
                Assert.Null(closingStill);
                Assert.Equal(260m + deposited, targetBalance); // every deposit that was accepted is now on the target
            }
            else
            {
                Assert.NotNull(closingStill);
                Assert.Equal(250m + deposited, closingStill!.Balance);
                Assert.Equal(10m, targetBalance);
            }
        }

        // ---- one-time codes under collision ------------------------------------------------------------------

        private static TransferRequestDto Transfer(string from, string to, decimal amount, string? code = null) => new()
        {
            SourceAccountNumber = from, DestinationAccountNumber = to, Amount = amount, Description = "race", OtpCode = code
        };

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task One_code_cannot_approve_the_same_transfer_twice_even_when_requests_arrive_together(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var alice = await AddUserAsync(db, "alice");
            var bob = await AddUserAsync(db, "bob");
            await AddAccountAsync(db, alice, "TR0000000000000001", 100_000m);
            await AddAccountAsync(db, bob, "TR0000000000000002", 0m);
            var otp = new FakeOtpDelivery();
            var challenge = await ConcurrencyHarness.InNewRequestAsync(db, new FakeMarketRates(), otp, s => s.TransferMoneyAsync(alice.Id, Transfer("TR0000000000000001", "TR0000000000000002", 2500m)));
            Assert.Equal("SuspectedFraudHighValue", challenge.ErrorKey);
            var code = otp.LastCode;

            var operations = Enumerable.Range(0, 6).Select(_ => (Func<Task<ServiceResult<TransactionDto>>>)(() =>
                ConcurrencyHarness.InNewRequestAsync(db, new FakeMarketRates(), new FakeOtpDelivery(), s => s.TransferMoneyAsync(alice.Id, Transfer("TR0000000000000001", "TR0000000000000002", 2500m, code)))));
            var results = await ConcurrencyHarness.RunTogetherAsync(operations);

            Assert.Equal(1, results.Count(r => r.IsSuccess)); // exactly one transfer was approved by the one code
            Assert.All(results.Where(r => !r.IsSuccess), r => Assert.Contains(r.ErrorKey, new[] { "InvalidOtpCode", "TooManyOtpAttempts", "ConcurrentModification" }));
            await using var verify = db.NewContext();
            Assert.Equal(97_500m, (await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountNumber == "TR0000000000000001")).Balance);
            Assert.Equal(2_500m, (await verify.Accounts.AsNoTracking().SingleAsync(a => a.AccountNumber == "TR0000000000000002")).Balance);
            Assert.Equal(1, await verify.Transactions.CountAsync());
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Wrong_guesses_made_at_the_same_moment_are_all_counted(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var alice = await AddUserAsync(db, "alice");
            var bob = await AddUserAsync(db, "bob");
            await AddAccountAsync(db, alice, "TR0000000000000001", 100_000m);
            await AddAccountAsync(db, bob, "TR0000000000000002", 0m);
            var otp = new FakeOtpDelivery();
            await ConcurrencyHarness.InNewRequestAsync(db, new FakeMarketRates(), otp, s => s.TransferMoneyAsync(alice.Id, Transfer("TR0000000000000001", "TR0000000000000002", 2500m)));
            var wrong = otp.LastCode == "000000" ? "000001" : "000000";

            var operations = Enumerable.Range(0, 4).Select(_ => (Func<Task<ServiceResult<TransactionDto>>>)(() =>
                ConcurrencyHarness.InNewRequestAsync(db, new FakeMarketRates(), new FakeOtpDelivery(), s => s.TransferMoneyAsync(alice.Id, Transfer("TR0000000000000001", "TR0000000000000002", 2500m, wrong)))));
            var results = await ConcurrencyHarness.RunTogetherAsync(operations);

            Assert.All(results, r => Assert.Equal("InvalidOtpCode", r.ErrorKey));
            await using var verify = db.NewContext();
            // Four collisions, four guesses counted (a lost update would leave the counter at 1, 2 or 3).
            Assert.Equal(4, (await verify.Users.AsNoTracking().SingleAsync(u => u.Id == alice.Id)).OtpFailedCount);

            var fifth = await ConcurrencyHarness.InNewRequestAsync(db, new FakeMarketRates(), new FakeOtpDelivery(), s => s.TransferMoneyAsync(alice.Id, Transfer("TR0000000000000001", "TR0000000000000002", 2500m, wrong)));
            Assert.Equal("TooManyOtpAttempts", fifth.ErrorKey);
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Wrong_pins_entered_at_the_same_moment_are_all_counted_toward_the_lockout(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var alice = await AddUserAsync(db, "alice");

            var operations = Enumerable.Range(0, 4).Select(_ => (Func<Task<string?>>)(async () =>
            {
                await using var context = db.NewContext();
                return (await NewAuth(context).LoginAsync(new LoginDto { Tckn = alice.Tckn, Password = "000000" })).ErrorKey;
            }));
            var keys = await ConcurrencyHarness.RunTogetherAsync(operations);

            Assert.All(keys, k => Assert.Equal("InvalidCredentials", k));
            await using var verify = db.NewContext();
            Assert.Equal(4, (await verify.Users.AsNoTracking().SingleAsync(u => u.Id == alice.Id)).FailedLoginCount);

            await using (var context = db.NewContext()) await NewAuth(context).LoginAsync(new LoginDto { Tckn = alice.Tckn, Password = "000000" });
            await using var after = db.NewContext();
            Assert.True(LoginLockout.IsLocked(await after.Users.AsNoTracking().SingleAsync(u => u.Id == alice.Id), DateTime.UtcNow));
        }

        // ---- unique indexes -----------------------------------------------------------------------------------

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Registrations_with_the_same_username_race_cleanly_one_wins_the_rest_get_a_clear_error(TestProvider provider)
        {
            EnsureEncryptionConfigured();
            await using var db = await TestDatabase.CreateAsync(provider);

            var operations = Enumerable.Range(0, 6).Select(i => (Func<Task<ServiceResult<AuthResponseDto>>>)(async () =>
            {
                await using var context = db.NewContext();
                return await NewAuth(context).RegisterAsync(new RegisterDto
                {
                    Username = "samename", Tckn = TestTckn.Next(), Password = Pin, FirstName = "Same", LastName = "Name", Email = $"same{i}@test.example"
                });
            }));
            var results = await ConcurrencyHarness.RunTogetherAsync(operations);

            Assert.Equal(1, results.Count(r => r.IsSuccess));
            Assert.All(results.Where(r => !r.IsSuccess), r => Assert.Equal("UsernameAlreadyExists", r.ErrorKey));
            await using var verify = db.NewContext();
            Assert.Equal(1, await verify.Users.CountAsync());
            Assert.Equal(1, await verify.CreditCards.CountAsync());
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Registrations_with_the_same_email_or_tckn_race_cleanly_too(TestProvider provider)
        {
            EnsureEncryptionConfigured();
            await using var db = await TestDatabase.CreateAsync(provider);
            var sharedTckn = TestTckn.Next();

            var byEmail = Enumerable.Range(0, 4).Select(i => (Func<Task<ServiceResult<AuthResponseDto>>>)(async () =>
            {
                await using var context = db.NewContext();
                return await NewAuth(context).RegisterAsync(new RegisterDto { Username = "mail" + i, Tckn = TestTckn.Next(), Password = Pin, FirstName = "A", LastName = "B", Email = "shared@test.example" });
            }));
            var byTckn = Enumerable.Range(0, 4).Select(i => (Func<Task<ServiceResult<AuthResponseDto>>>)(async () =>
            {
                await using var context = db.NewContext();
                return await NewAuth(context).RegisterAsync(new RegisterDto { Username = "tckn" + i, Tckn = sharedTckn, Password = Pin, FirstName = "A", LastName = "B", Email = $"t{i}@test.example" });
            }));
            var results = await ConcurrencyHarness.RunTogetherAsync(byEmail.Concat(byTckn));

            var failures = results.Where(r => !r.IsSuccess).Select(r => r.ErrorKey).ToList();
            Assert.Equal(2, results.Count(r => r.IsSuccess)); // one per group
            Assert.All(failures, k => Assert.Contains(k, new[] { "EmailAlreadyExists", "TcknAlreadyExists" }));
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Two_credit_cards_cannot_be_issued_at_the_same_moment(TestProvider provider)
        {
            EnsureEncryptionConfigured();
            await using var db = await TestDatabase.CreateAsync(provider);
            var alice = await AddUserAsync(db, "alice");

            var operations = Enumerable.Range(0, 5).Select(_ => (Func<Task<ServiceResult<CreditCardDto>>>)(() => ConcurrencyHarness.InNewRequestAsync(db, s => s.CreateCreditCardAsync(alice.Id))));
            var results = await ConcurrencyHarness.RunTogetherAsync(operations);

            Assert.Equal(1, results.Count(r => r.IsSuccess));
            Assert.All(results.Where(r => !r.IsSuccess), r => Assert.Equal("MaxCreditCardsLimitReached", r.ErrorKey));
            await using var verify = db.NewContext();
            Assert.Equal(1, await verify.CreditCards.CountAsync());
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task The_same_recipient_saved_at_the_same_moment_is_stored_once(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var alice = await AddUserAsync(db, "alice");

            var operations = Enumerable.Range(0, 5).Select(i => (Func<Task<ServiceResult<SavedContactDto>>>)(() =>
                ConcurrencyHarness.InNewRequestAsync(db, s => s.SaveContactAsync(alice.Id, new CreateSavedContactDto { AccountNumber = "TR0000000000000009", Alias = "alias" + i }))));
            var results = await ConcurrencyHarness.RunTogetherAsync(operations);

            Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));
            await using var verify = db.NewContext();
            Assert.Equal(1, await verify.SavedContacts.CountAsync());
        }

        // ---- sessions (bulk updates) --------------------------------------------------------------------------

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Signing_out_and_resetting_the_password_revoke_tokens_with_a_bulk_update_that_bumps_the_version(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var alice = await AddUserAsync(db, "alice");

            async Task<AuthResponseDto> SignInAsync()
            {
                await using var context = db.NewContext();
                return (await NewAuth(context).LoginAsync(new LoginDto { Tckn = alice.Tckn, Password = Pin })).Data!;
            }

            var laptop = await SignInAsync();
            var phone = await SignInAsync();

            await using (var context = db.NewContext()) await NewAuth(context).LogoutAsync(laptop.RefreshToken);

            await using (var context = db.NewContext())
            {
                Assert.False((await NewAuth(context).RefreshAsync(laptop.RefreshToken)).IsSuccess);
            }

            await using (var context = db.NewContext())
            {
                var tokens = await context.RefreshTokens.AsNoTracking().ToListAsync();
                Assert.Equal(1, tokens.Count(t => t.RevokedAt != null));
                Assert.All(tokens.Where(t => t.RevokedAt != null), t => Assert.True(t.Version >= 1));
            }

            await using (var context = db.NewContext())
            {
                Assert.True((await NewAuth(context).RefreshAsync(phone.RefreshToken)).IsSuccess); // the other device is untouched
            }

            await using (var context = db.NewContext())
            {
                var auth = NewAuth(context);
                var otp = new FakeOtpDelivery();
                var service = new AuthService(context, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["JwtSettings:Key"] = "unit-test-signing-key-0123456789-abcdef" }).Build(), otp);
                await service.RequestPasswordResetAsync(new ForgotPasswordDto { Tckn = alice.Tckn });
                Assert.True((await service.ResetPasswordAsync(new ResetPasswordDto { Tckn = alice.Tckn, Code = otp.LastCode, NewPassword = "654321" })).IsSuccess);
            }

            await using var verify = db.NewContext();
            Assert.All(await verify.RefreshTokens.AsNoTracking().ToListAsync(), t => Assert.NotNull(t.RevokedAt));
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Parallel_wrong_pins_cannot_hide_from_a_correct_pin_sent_at_the_same_moment(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var accepted = 0;
            const int rounds = 5;

            // Every request reads the user before any failure is saved, so each one sees "not locked". A correct PIN used to change
            // nothing on the row, so it was accepted no matter how many guesses had already been spent. Now it has to collide with
            // the saved failures and read the row again, where the lockout is visible: with 25 wrong guesses racing it, it cannot
            // be accepted every time.
            for (var round = 0; round < rounds; round++)
            {
                var user = await AddUserAsync(db, "victim" + round);
                await using (var slow = db.NewContext())
                {
                    // A real-strength hash: each verification takes long enough that every request has read the user before the first one finishes.
                    var row = await slow.Users.SingleAsync(u => u.Id == user.Id);
                    row.PasswordHash = BCrypt.Net.BCrypt.HashPassword(Pin, 10);
                    await slow.SaveChangesAsync();
                }

                var operations = Enumerable.Range(0, 25)
                    .Select(i => (Func<Task<bool>>)(async () =>
                    {
                        await using var context = db.NewContext();
                        return (await NewAuth(context).LoginAsync(new LoginDto { Tckn = user.Tckn, Password = "99" + (1000 + i) })).IsSuccess;
                    }))
                    .Append(async () =>
                    {
                        await using var context = db.NewContext();
                        return (await NewAuth(context).LoginAsync(new LoginDto { Tckn = user.Tckn, Password = Pin })).IsSuccess;
                    })
                    .ToList();

                var results = await ConcurrencyHarness.RunTogetherAsync(operations);
                if (results[^1]) accepted++;

                await using var verify = db.NewContext();
                Assert.NotNull((await verify.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).LockoutEnd);
            }

            Assert.True(accepted < rounds, $"the correct PIN got through in all {rounds} rounds");
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task The_sign_in_housekeeping_deletes_old_used_tokens_with_a_bulk_delete(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            var alice = await AddUserAsync(db, "alice");
            await using (var seed = db.NewContext())
            {
                seed.RefreshTokens.AddRange(
                    new RefreshToken { Id = Guid.NewGuid(), UserId = alice.Id, FamilyId = Guid.NewGuid(), TokenHash = new string('a', 64), CreatedAt = DateTime.UtcNow.AddDays(-3), ExpiresAt = DateTime.UtcNow.AddDays(4), UsedAt = DateTime.UtcNow.AddDays(-2) },
                    new RefreshToken { Id = Guid.NewGuid(), UserId = alice.Id, FamilyId = Guid.NewGuid(), TokenHash = new string('b', 64), CreatedAt = DateTime.UtcNow.AddDays(-3), ExpiresAt = DateTime.UtcNow.AddDays(4), RevokedAt = DateTime.UtcNow.AddDays(-2) },
                    new RefreshToken { Id = Guid.NewGuid(), UserId = alice.Id, FamilyId = Guid.NewGuid(), TokenHash = new string('c', 64), CreatedAt = DateTime.UtcNow.AddHours(-1), ExpiresAt = DateTime.UtcNow.AddDays(7), UsedAt = DateTime.UtcNow.AddMinutes(-30) });
                await seed.SaveChangesAsync();
            }

            await using (var context = db.NewContext()) await NewAuth(context).LoginAsync(new LoginDto { Tckn = alice.Tckn, Password = Pin });

            await using var verify = db.NewContext();
            var left = await verify.RefreshTokens.AsNoTracking().Select(t => t.TokenHash).ToListAsync();
            Assert.DoesNotContain(new string('a', 64), left);
            Assert.DoesNotContain(new string('b', 64), left);
            Assert.Contains(new string('c', 64), left); // used 30 minutes ago: kept for the reuse check
            Assert.Equal(2, left.Count);
        }
    }
}
