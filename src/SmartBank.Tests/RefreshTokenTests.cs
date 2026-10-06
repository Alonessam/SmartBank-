using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Security;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Services;

namespace SmartBank.Tests
{
    /// <summary>
    /// Access tokens are short-lived because they cannot be revoked. Refresh tokens renew them, are single-use, and are
    /// revoked when the account is locked, the password is reset or the user signs out.
    /// </summary>
    public class RefreshTokenTests
    {
        private const string Tckn = "12345678901";
        private const string Pin = "123456";

        private readonly FakeOtpDelivery _otp = new();

        private static SmartBankDbContext NewContext() =>
            new(new DbContextOptionsBuilder<SmartBankDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        private AuthService NewService(SmartBankDbContext context, params (string Key, string Value)[] extra)
        {
            var values = new Dictionary<string, string?> { ["JwtSettings:Key"] = "unit-test-signing-key-0123456789-abcdef" };
            foreach (var (key, value) in extra) values[key] = value;

            return new AuthService(context, new ConfigurationBuilder().AddInMemoryCollection(values).Build(), _otp);
        }

        private static async Task<User> AddUserAsync(SmartBankDbContext context)
        {
            var user = new User
            {
                Username = "session-tester",
                Tckn = Tckn,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Pin, 4),
                FullName = "Session Tester",
                Email = "session@test.com"
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();
            return user;
        }

        private static async Task<AuthResponseDto> SignInAsync(AuthService service)
        {
            var result = await service.LoginAsync(new LoginDto { Tckn = Tckn, Password = Pin }, "203.0.113.7");
            Assert.True(result.IsSuccess);
            return result.Data!;
        }

        [Fact]
        public async Task Sign_in_returns_a_short_lived_access_token_and_a_refresh_token()
        {
            using var context = NewContext();
            await AddUserAsync(context);

            var session = await SignInAsync(NewService(context));

            Assert.False(string.IsNullOrEmpty(session.RefreshToken));
            var lifetime = session.AccessTokenExpiresAt - DateTime.UtcNow;
            Assert.InRange(lifetime, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15.1));
        }

        [Fact]
        public async Task The_database_stores_a_hash_never_the_token_itself()
        {
            using var context = NewContext();
            await AddUserAsync(context);

            var session = await SignInAsync(NewService(context));

            var stored = await context.RefreshTokens.SingleAsync();
            Assert.NotEqual(session.RefreshToken, stored.TokenHash);
            Assert.DoesNotContain(session.RefreshToken, stored.TokenHash);
            Assert.Equal(64, stored.TokenHash.Length);
        }

        [Fact]
        public async Task Lifetimes_come_from_configuration()
        {
            using var context = NewContext();
            await AddUserAsync(context);

            var session = await SignInAsync(NewService(context, ("JwtSettings:AccessTokenMinutes", "5"), ("JwtSettings:RefreshTokenDays", "2")));

            Assert.InRange(session.AccessTokenExpiresAt - DateTime.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5.1));
            var stored = await context.RefreshTokens.SingleAsync();
            Assert.InRange(stored.ExpiresAt - DateTime.UtcNow, TimeSpan.FromDays(1.99), TimeSpan.FromDays(2.01));
        }

        [Theory]
        [InlineData("JwtSettings:AccessTokenMinutes", "0")]
        [InlineData("JwtSettings:AccessTokenMinutes", "100000")]
        [InlineData("JwtSettings:AccessTokenMinutes", "abc")]
        [InlineData("JwtSettings:RefreshTokenDays", "0")]
        [InlineData("JwtSettings:RefreshTokenDays", "365")]
        public void Nonsense_lifetimes_stop_the_app_from_starting(string key, string value)
        {
            using var context = NewContext();

            Assert.Throws<InvalidOperationException>(() => NewService(context, (key, value)));
        }

        [Fact]
        public async Task Refreshing_returns_a_new_pair_and_the_old_refresh_token_stops_working()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);
            var first = await SignInAsync(service);

            var second = await service.RefreshAsync(first.RefreshToken, "203.0.113.7");

            Assert.True(second.IsSuccess);
            Assert.NotEqual(first.RefreshToken, second.Data!.RefreshToken);
            Assert.False(string.IsNullOrEmpty(second.Data.Token));
            Assert.Equal("session-tester", second.Data.Username);

            var again = await service.RefreshAsync(first.RefreshToken, "203.0.113.7");
            Assert.False(again.IsSuccess);
            Assert.Equal("InvalidRefreshToken", again.ErrorKey);
        }

        [Fact]
        public async Task A_second_use_within_seconds_is_refused_but_does_not_end_the_session()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);
            var first = await SignInAsync(service);
            var second = (await service.RefreshAsync(first.RefreshToken)).Data!;

            // Two tabs refreshing at once: the loser gets a refusal, the winner's new token still works.
            Assert.False((await service.RefreshAsync(first.RefreshToken)).IsSuccess);
            Assert.True((await service.RefreshAsync(second.RefreshToken)).IsSuccess);
        }

        [Fact]
        public async Task Replaying_an_old_token_later_revokes_the_whole_session_family()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);
            var first = await SignInAsync(service);
            var second = (await service.RefreshAsync(first.RefreshToken)).Data!;

            // Pretend the rotation happened a minute ago, so the second use is not a harmless double click.
            var rotated = await context.RefreshTokens.OrderBy(t => t.CreatedAt).FirstAsync(t => t.UsedAt != null);
            rotated.UsedAt = DateTime.UtcNow.AddMinutes(-1);
            await context.SaveChangesAsync();

            Assert.False((await service.RefreshAsync(first.RefreshToken)).IsSuccess);

            // The thief and the real user both lose the session, including the newest token.
            Assert.False((await service.RefreshAsync(second.RefreshToken)).IsSuccess);
            Assert.All(await context.RefreshTokens.ToListAsync(), t => Assert.NotNull(t.RevokedAt));
            Assert.Contains(await context.AuditLogs.ToListAsync(), a => a.Action == "RefreshTokenReuse");
        }

        [Fact]
        public async Task An_expired_refresh_token_is_refused()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);
            var session = await SignInAsync(service);

            (await context.RefreshTokens.SingleAsync()).ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await context.SaveChangesAsync();

            Assert.False((await service.RefreshAsync(session.RefreshToken)).IsSuccess);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not-a-real-token")]
        [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
        public async Task An_unknown_refresh_token_is_refused(string token)
        {
            using var context = NewContext();
            await AddUserAsync(context);

            var result = await NewService(context).RefreshAsync(token);

            Assert.False(result.IsSuccess);
            Assert.Equal("InvalidRefreshToken", result.ErrorKey);
        }

        [Fact]
        public async Task Logging_out_ends_the_session()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);
            var first = await SignInAsync(service);
            var second = (await service.RefreshAsync(first.RefreshToken)).Data!;

            await service.LogoutAsync(second.RefreshToken);

            Assert.False((await service.RefreshAsync(second.RefreshToken)).IsSuccess);
        }

        [Fact]
        public async Task Logging_out_with_a_made_up_token_is_harmless()
        {
            using var context = NewContext();
            await AddUserAsync(context);

            await NewService(context).LogoutAsync("made-up-token-that-does-not-exist");
        }

        [Fact]
        public async Task Logging_out_one_device_leaves_other_sign_ins_alone()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);
            var laptop = await SignInAsync(service);
            var phone = await SignInAsync(service);

            await service.LogoutAsync(laptop.RefreshToken);

            Assert.False((await service.RefreshAsync(laptop.RefreshToken)).IsSuccess);
            Assert.True((await service.RefreshAsync(phone.RefreshToken)).IsSuccess);
        }

        [Fact]
        public async Task A_password_reset_signs_out_every_session()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);
            var laptop = await SignInAsync(service);
            var phone = await SignInAsync(service);

            await service.RequestPasswordResetAsync(new ForgotPasswordDto { Tckn = Tckn });
            var reset = await service.ResetPasswordAsync(new ResetPasswordDto { Tckn = Tckn, Code = _otp.LastCode, NewPassword = "654321" });
            Assert.True(reset.IsSuccess);

            Assert.False((await service.RefreshAsync(laptop.RefreshToken)).IsSuccess);
            Assert.False((await service.RefreshAsync(phone.RefreshToken)).IsSuccess);
        }

        [Fact]
        public async Task Wrong_pins_and_a_lockout_do_not_sign_anybody_out()
        {
            // Anybody who knows a T.C. number can send wrong PINs. If that ended the owner's sessions, it would be a way to
            // log a stranger out at will, so the lockout only stops PIN guessing.
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);
            var session = await SignInAsync(service);

            for (var i = 0; i < LoginLockout.MaxFailedAttempts; i++)
            {
                await service.LoginAsync(new LoginDto { Tckn = Tckn, Password = "000000" });
            }

            var locked = await context.Users.AsNoTracking().SingleAsync();
            Assert.True(LoginLockout.IsLocked(locked, DateTime.UtcNow));

            var refreshed = await service.RefreshAsync(session.RefreshToken);

            Assert.True(refreshed.IsSuccess);
            Assert.All(await context.RefreshTokens.Where(t => t.UsedAt == null).ToListAsync(), t => Assert.Null(t.RevokedAt));
        }

        [Fact]
        public async Task Old_expired_tokens_of_the_user_are_cleaned_up_at_the_next_sign_in()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context);
            context.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(), UserId = user.Id, FamilyId = Guid.NewGuid(), TokenHash = new string('a', 64),
                CreatedAt = DateTime.UtcNow.AddDays(-30), ExpiresAt = DateTime.UtcNow.AddDays(-20)
            });
            await context.SaveChangesAsync();

            await SignInAsync(NewService(context));

            Assert.Single(await context.RefreshTokens.ToListAsync());
        }
    }
}
