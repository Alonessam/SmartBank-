using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Security;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Services;
using SmartBank.Tests.Support;

namespace SmartBank.Tests
{
    /// <summary>The v1.3 rules of sign-in: the two-factor switch needs the PIN, codes do not trample each other, time comes from the clock.</summary>
    [Collection("EncryptionHelper")]
    public class AuthV13Tests
    {
        private const string Tckn = "12345678901";
        private const string Pin = "123456";

        private readonly FakeOtpDelivery _otp = new();
        private readonly TestClock _clock = new();
        private readonly string _database = Guid.NewGuid().ToString();

        public AuthV13Tests() => EncryptionHelper_Configure();

        private static void EncryptionHelper_Configure() =>
            SmartBank.Core.Common.EncryptionHelper.Configure(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        private SmartBankDbContext NewContext() => new(new DbContextOptionsBuilder<SmartBankDbContext>().UseInMemoryDatabase(_database).Options);

        private AuthService NewService(SmartBankDbContext context)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["JwtSettings:Key"] = "unit-test-signing-key-0123456789-abcdef" }).Build();
            return new AuthService(context, config, _otp, _clock);
        }

        private async Task<User> AddUserAsync(SmartBankDbContext context, bool twoFactor = false)
        {
            var user = new User
            {
                Username = "auth-tester",
                Tckn = Tckn,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Pin, 4),
                FirstName = "Auth",
                LastName = "Tester",
                FullName = "Auth Tester",
                Email = "auth@test.com",
                TwoFactorEnabled = twoFactor
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();
            return user;
        }

        private static string Wrong(string pin) => pin == "000000" ? "000001" : "000000";

        // ---- the two-factor switch needs the PIN -------------------------------------------------------------

        [Fact]
        public async Task Turning_two_factor_on_needs_the_pin_and_is_audited()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context);
            var service = NewService(context);

            var result = await service.Toggle2FaAsync(user.Id, true, Pin, "203.0.113.5");

            Assert.True(result.IsSuccess);
            Assert.True(result.Data);
            Assert.True((await NewContext().Users.AsNoTracking().SingleAsync()).TwoFactorEnabled);
            var audit = await NewContext().AuditLogs.AsNoTracking().SingleAsync();
            Assert.Equal("TwoFactorEnabled", audit.Action);
            Assert.Equal("203.0.113.5", audit.IpAddress);
        }

        [Fact]
        public async Task Turning_it_off_needs_the_pin_too()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context, twoFactor: true);
            var service = NewService(context);

            Assert.Equal("PinRequired", (await service.Toggle2FaAsync(user.Id, false, null)).ErrorKey);
            Assert.Equal("PinRequired", (await service.Toggle2FaAsync(user.Id, false, "")).ErrorKey);
            Assert.Equal("InvalidCredentials", (await service.Toggle2FaAsync(user.Id, false, Wrong(Pin))).ErrorKey);
            Assert.True((await NewContext().Users.AsNoTracking().SingleAsync()).TwoFactorEnabled); // a stolen token cannot switch it off

            var off = await service.Toggle2FaAsync(user.Id, false, Pin);
            Assert.True(off.IsSuccess);
            Assert.False(off.Data);
            Assert.Contains(await NewContext().AuditLogs.AsNoTracking().ToListAsync(), a => a.Action == "TwoFactorDisabled");
        }

        [Fact]
        public async Task A_wrong_pin_here_counts_toward_the_lockout_and_a_locked_account_cannot_change_the_setting()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context);
            var service = NewService(context);

            for (var i = 0; i < LoginLockout.MaxFailedAttempts; i++)
            {
                var attempt = await service.Toggle2FaAsync(user.Id, true, Wrong(Pin));
                Assert.Equal("InvalidCredentials", attempt.ErrorKey);
                Assert.Equal(AuthService.InvalidCredentialsMessage, attempt.Message);
            }

            var stored = await NewContext().Users.AsNoTracking().SingleAsync();
            Assert.True(LoginLockout.IsLocked(stored, _clock.UtcNow));

            // Even the right PIN is refused while locked, with the same answer; and so is signing in.
            Assert.Equal("InvalidCredentials", (await service.Toggle2FaAsync(user.Id, true, Pin)).ErrorKey);
            Assert.Equal("InvalidCredentials", (await service.LoginAsync(new LoginDto { Tckn = Tckn, Password = Pin })).ErrorKey);
            Assert.False((await NewContext().Users.AsNoTracking().SingleAsync()).TwoFactorEnabled);

            _clock.Advance(LoginLockout.Duration + TimeSpan.FromSeconds(1));
            Assert.True((await service.Toggle2FaAsync(user.Id, true, Pin)).IsSuccess);
        }

        [Fact]
        public async Task A_right_pin_clears_earlier_wrong_ones()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context);
            var service = NewService(context);
            for (var i = 0; i < LoginLockout.MaxFailedAttempts - 1; i++) await service.Toggle2FaAsync(user.Id, true, Wrong(Pin));

            Assert.True((await service.Toggle2FaAsync(user.Id, true, Pin)).IsSuccess);

            Assert.Equal(0, (await NewContext().Users.AsNoTracking().SingleAsync()).FailedLoginCount);
        }

        [Fact]
        public async Task An_unknown_user_gets_a_clean_error_and_the_status_endpoint_reads_the_flag()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context, twoFactor: true);
            var service = NewService(context);

            Assert.Equal("UserNotFound", (await service.Toggle2FaAsync(Guid.NewGuid(), true, Pin)).ErrorKey);
            Assert.True((await service.Get2FaStatusAsync(user.Id)).Data);
            Assert.Equal("UserNotFound", (await service.Get2FaStatusAsync(Guid.NewGuid())).ErrorKey);
        }

        // ---- the single pending-code slot --------------------------------------------------------------------

        [Fact]
        public async Task A_stranger_asking_for_a_reset_cannot_destroy_the_login_code_you_are_typing()
        {
            using var context = NewContext();
            await AddUserAsync(context, twoFactor: true);
            var service = NewService(context);
            await service.LoginAsync(new LoginDto { Tckn = Tckn, Password = Pin });
            var loginCode = _otp.LastCode;

            var forgot = await service.RequestPasswordResetAsync(new ForgotPasswordDto { Tckn = Tckn });

            Assert.True(forgot.IsSuccess); // the answer is the same as always
            Assert.Single(_otp.Sent);      // but nothing was sent...
            var verified = await service.Verify2FaAsync(new Verify2FaDto { Tckn = Tckn, Code = loginCode });
            Assert.True(verified.IsSuccess); // ...and the login code still works
        }

        [Fact]
        public async Task A_reset_code_can_be_requested_once_the_other_code_has_expired()
        {
            using var context = NewContext();
            await AddUserAsync(context, twoFactor: true);
            var service = NewService(context);
            await service.LoginAsync(new LoginDto { Tckn = Tckn, Password = Pin });

            _clock.Advance(OtpManager.Lifetime + TimeSpan.FromSeconds(1));
            await service.RequestPasswordResetAsync(new ForgotPasswordDto { Tckn = Tckn });

            Assert.Equal(OtpPurpose.PasswordReset, _otp.Sent[^1].Purpose);
        }

        [Fact]
        public async Task Reset_requests_have_a_cooldown_measured_by_the_clock()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);

            await service.RequestPasswordResetAsync(new ForgotPasswordDto { Tckn = Tckn });
            _clock.Advance(TimeSpan.FromSeconds(59));
            await service.RequestPasswordResetAsync(new ForgotPasswordDto { Tckn = Tckn });
            Assert.Single(_otp.Sent);

            _clock.Advance(TimeSpan.FromSeconds(2));
            await service.RequestPasswordResetAsync(new ForgotPasswordDto { Tckn = Tckn });
            Assert.Equal(2, _otp.Sent.Count);
        }

        [Fact]
        public async Task A_login_code_expires_after_five_minutes_by_the_clock()
        {
            using var context = NewContext();
            await AddUserAsync(context, twoFactor: true);
            var service = NewService(context);
            await service.LoginAsync(new LoginDto { Tckn = Tckn, Password = Pin });
            var code = _otp.LastCode;

            _clock.Advance(OtpManager.Lifetime + TimeSpan.FromSeconds(1));
            var late = await service.Verify2FaAsync(new Verify2FaDto { Tckn = Tckn, Code = code });

            Assert.Equal("InvalidOrExpiredCode", late.ErrorKey);
        }

        [Fact]
        public async Task While_locked_a_correct_two_factor_code_gets_the_same_answer_as_a_wrong_one()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context, twoFactor: true);
            var service = NewService(context);
            await service.LoginAsync(new LoginDto { Tckn = Tckn, Password = Pin });
            var code = _otp.LastCode;
            var tracked = await context.Users.SingleAsync(u => u.Id == user.Id);
            tracked.LockoutEnd = _clock.UtcNow.AddMinutes(5);
            await context.SaveChangesAsync();

            var result = await service.Verify2FaAsync(new Verify2FaDto { Tckn = Tckn, Code = code });

            Assert.Equal("InvalidOrExpiredCode", result.ErrorKey);
        }

        // ---- the lockout -------------------------------------------------------------------------------------

        [Fact]
        public async Task The_lockout_lasts_fifteen_minutes_by_the_clock()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);
            for (var i = 0; i < LoginLockout.MaxFailedAttempts; i++) await service.LoginAsync(new LoginDto { Tckn = Tckn, Password = Wrong(Pin) });

            _clock.Advance(TimeSpan.FromMinutes(14));
            Assert.False((await service.LoginAsync(new LoginDto { Tckn = Tckn, Password = Pin })).IsSuccess);

            _clock.Advance(TimeSpan.FromMinutes(1.1));
            Assert.True((await service.LoginAsync(new LoginDto { Tckn = Tckn, Password = Pin })).IsSuccess);
        }

        [Fact]
        public async Task A_stale_write_to_the_user_row_is_noticed()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context);
            await using var a = NewContext();
            await using var b = NewContext();
            var userA = await a.Users.SingleAsync(u => u.Id == user.Id);
            var userB = await b.Users.SingleAsync(u => u.Id == user.Id);

            userA.FailedLoginCount++;
            userB.FailedLoginCount++;
            await a.SaveChangesAsync();

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
            Assert.Equal(1, (await NewContext().Users.AsNoTracking().SingleAsync()).Version);
        }

        // ---- sessions and tokens -----------------------------------------------------------------------------

        [Fact]
        public async Task The_access_token_lifetime_and_refresh_expiry_follow_the_clock()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);

            var session = (await service.LoginAsync(new LoginDto { Tckn = Tckn, Password = Pin })).Data!;

            Assert.Equal(_clock.UtcNow.AddMinutes(15), session.AccessTokenExpiresAt);
            var stored = await NewContext().RefreshTokens.AsNoTracking().SingleAsync();
            Assert.Equal(_clock.UtcNow.AddDays(7), stored.ExpiresAt);

            _clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
            Assert.Equal("InvalidRefreshToken", (await service.RefreshAsync(session.RefreshToken)).ErrorKey);
        }

        [Fact]
        public async Task Used_and_revoked_tokens_older_than_a_day_are_removed_at_the_next_sign_in_but_recent_ones_stay()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context);
            var service = NewService(context);
            var first = (await service.LoginAsync(new LoginDto { Tckn = Tckn, Password = Pin })).Data!;
            await service.RefreshAsync(first.RefreshToken); // leaves one used token and one live one

            _clock.Advance(TimeSpan.FromHours(23));
            await service.LoginAsync(new LoginDto { Tckn = Tckn, Password = Pin });
            Assert.Equal(3, await NewContext().RefreshTokens.CountAsync()); // nothing old enough yet

            _clock.Advance(TimeSpan.FromHours(2)); // the used token is now older than a day
            await service.LoginAsync(new LoginDto { Tckn = Tckn, Password = Pin });

            var left = await NewContext().RefreshTokens.AsNoTracking().ToListAsync();
            Assert.DoesNotContain(left, t => t.UsedAt != null);
            Assert.Equal(3, left.Count);
        }

        [Fact]
        public async Task The_access_token_carries_no_tckn_and_the_response_still_does()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);

            var session = (await service.LoginAsync(new LoginDto { Tckn = Tckn, Password = Pin })).Data!;

            var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(session.Token);
            Assert.DoesNotContain(jwt.Claims, c => c.Type == "tckn" || c.Value == Tckn);
        }

        // ---- registration ------------------------------------------------------------------------------------

        private static RegisterDto Register(string username = "newbie", string email = "New.User@Example.com", string? tckn = null) => new()
        {
            Username = username,
            Tckn = tckn ?? TestTckn.Next(),
            Password = "123456",
            FirstName = "New",
            LastName = "User",
            Email = email
        };

        [Fact]
        public async Task Registration_stores_the_email_in_lower_case_and_masks_the_tckn_in_the_audit_trail()
        {
            using var context = NewContext();
            var service = NewService(context);
            var dto = Register();

            var result = await service.RegisterAsync(dto, "203.0.113.9");

            Assert.True(result.IsSuccess);
            var user = await NewContext().Users.AsNoTracking().SingleAsync();
            Assert.Equal("new.user@example.com", user.Email);
            var audit = await NewContext().AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "UserRegistered");
            Assert.DoesNotContain(dto.Tckn, audit.Details);
            Assert.Contains($"{dto.Tckn[..3]}****{dto.Tckn[^2..]}", audit.Details);
        }

        [Fact]
        public async Task Registration_gives_the_new_customer_an_account_a_card_and_a_statement_named_from_the_clock()
        {
            using var context = NewContext();
            var service = NewService(context);

            await service.RegisterAsync(Register());

            var read = NewContext();
            var account = await read.Accounts.AsNoTracking().SingleAsync();
            Assert.Equal(1000m, account.Balance);
            Assert.Equal("10/31", account.ExpiryDate);
            var card = await read.CreditCards.AsNoTracking().Include(c => c.Statements).SingleAsync();
            Assert.Equal(1250m, card.CurrentDebt);
            var statement = Assert.Single(card.Statements);
            Assert.Equal("Eylül 2026", statement.PeriodName); // cut off 5 days ago (1 Oct): the period began in September
            Assert.Equal(375m, statement.MinimumPayment);
        }

        [Theory]
        [InlineData("taken", "other@example.com", "UsernameAlreadyExists")]
        [InlineData("other", "TAKEN@example.com", "EmailAlreadyExists")]
        public async Task A_taken_username_or_email_is_reported(string username, string email, string errorKey)
        {
            using var context = NewContext();
            var service = NewService(context);
            await service.RegisterAsync(Register("taken", "taken@example.com"));

            var again = await service.RegisterAsync(Register(username, email));

            Assert.Equal(errorKey, again.ErrorKey);
        }

        [Fact]
        public async Task A_taken_tckn_is_reported()
        {
            using var context = NewContext();
            var service = NewService(context);
            var tckn = TestTckn.Next();
            await service.RegisterAsync(Register("one", "one@example.com", tckn));

            var again = await service.RegisterAsync(Register("two", "two@example.com", tckn));

            Assert.Equal("TcknAlreadyExists", again.ErrorKey);
        }

        // ---- culture -----------------------------------------------------------------------------------------

        [Fact]
        public async Task Under_a_turkish_culture_an_email_with_a_capital_i_is_lowered_correctly_and_still_found()
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            try
            {
                using var context = NewContext();
                var service = NewService(context);

                var first = await service.RegisterAsync(Register("istanbul", "INFO@ISTANBUL.COM"));
                var second = await service.RegisterAsync(Register("izmir", "info@istanbul.com"));

                Assert.True(first.IsSuccess);
                Assert.Equal("info@istanbul.com", (await NewContext().Users.AsNoTracking().SingleAsync()).Email); // not "ınfo@ıstanbul.com"
                Assert.Equal("EmailAlreadyExists", second.ErrorKey);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }
}
