using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Services;

namespace SmartBank.Tests.Database
{
    /// <summary>A refresh token is single-use even when the same token is presented many times at the same instant.</summary>
    // One collection: these classes each open dozens of connections, so they run one after another, not in parallel.
    [Collection("Database")]
    public class ConcurrentRefreshTests
    {
        private static AuthService NewService(SmartBankDbContext context) => new(
            context,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:Key"] = "unit-test-signing-key-0123456789-abcdef"
            }).Build(),
            new FakeOtpDelivery());

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task The_same_refresh_token_used_by_many_requests_at_once_succeeds_exactly_once(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);

            string refreshToken;
            await using (var setup = db.NewContext())
            {
                setup.Users.Add(new User
                {
                    Username = "racer", Tckn = "33333333333", Email = "racer@test.com", FullName = "Racer",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("123456", 4)
                });
                await setup.SaveChangesAsync();

                var login = await NewService(setup).LoginAsync(new LoginDto { Tckn = "33333333333", Password = "123456" });
                refreshToken = login.Data!.RefreshToken;
            }

            var attempts = Enumerable.Range(0, 8).Select(_ => (Func<Task<bool>>)(async () =>
            {
                await using var context = db.NewContext();
                return (await NewService(context).RefreshAsync(refreshToken)).IsSuccess;
            }));

            var outcomes = await ConcurrencyHarness.RunTogetherAsync(attempts);

            Assert.Equal(1, outcomes.Count(ok => ok));

            // The winner left exactly one new, unused token behind: the old one (used) and its replacement.
            await using var check = db.NewContext();
            var tokens = await check.RefreshTokens.AsNoTracking().ToListAsync();
            Assert.Equal(2, tokens.Count);
            Assert.Equal(1, tokens.Count(t => t.UsedAt == null));
        }
    }
}
