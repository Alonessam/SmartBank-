using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Infrastructure.Services;
using SmartBank.Tests.Support;

namespace SmartBank.Tests.Database
{
    /// <summary>
    /// The production tables were created by hand in Supabase, not from the EF model, and they may carry constraints the model does
    /// not have. These tests add such constraints to a model-built database and check the real flows still work. The first
    /// version of the v1.3 registration inserted the audit row in the same SaveChanges as the new user; a foreign key on
    /// AuditLogs.UserId then rejected it and registration failed for everybody on the live site (after 18 seconds of retries),
    /// while every test passed because the test databases are built from the model.
    /// </summary>
    // One collection: these classes each open dozens of connections, so they run one after another, not in parallel.
    [Collection("Database")]
    public class HandMadeSchemaTests
    {
        private static void EnsureEncryptionConfigured()
        {
            try { EncryptionHelper.Encrypt("probe"); }
            catch (InvalidOperationException) { EncryptionHelper.Configure(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))); }
        }

        private static AuthService NewAuth(SmartBank.Infrastructure.Data.SmartBankDbContext context) => new(
            context,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["JwtSettings:Key"] = "unit-test-signing-key-0123456789-abcdef" }).Build(),
            new FakeOtpDelivery());

        /// <summary>A foreign key from the audit trail to the users, as a hand-written production schema may have it.</summary>
        private static async Task AddAuditForeignKeyAsync(TestDb db, TestProvider provider)
        {
            await using var context = db.NewContext();
            var sql = provider == TestProvider.PostgreSql
                ? "ALTER TABLE \"AuditLogs\" ADD CONSTRAINT \"FK_AuditLogs_Users_UserId\" FOREIGN KEY (\"UserId\") REFERENCES \"Users\" (\"Id\")"
                : "ALTER TABLE [AuditLogs] ADD CONSTRAINT [FK_AuditLogs_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id])";
            await context.Database.ExecuteSqlRawAsync(sql);
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Registration_works_when_the_audit_trail_has_a_foreign_key_to_the_users(TestProvider provider)
        {
            EnsureEncryptionConfigured();
            await using var db = await TestDatabase.CreateAsync(provider);
            await AddAuditForeignKeyAsync(db, provider);

            ServiceResult<AuthResponseDto> result;
            await using (var context = db.NewContext())
            {
                var started = DateTime.UtcNow;
                result = await NewAuth(context).RegisterAsync(new RegisterDto
                {
                    Username = "handmade", Tckn = TestTckn.Next(), Password = "123456", FirstName = "Hand", LastName = "Made", Email = "handmade@test.example"
                }, "203.0.113.9");

                // No ten retries with growing pauses: the call either works at once or fails at once.
                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8), "registration took far too long");
            }

            Assert.True(result.IsSuccess, result.ErrorKey + ": " + result.Message);
            Assert.False(string.IsNullOrEmpty(result.Data!.RefreshToken));

            await using var verify = db.NewContext();
            Assert.Equal(1, await verify.Users.CountAsync());
            Assert.Equal(1, await verify.AuditLogs.CountAsync(a => a.Action == "UserRegistered" && a.UserId == result.Data.UserId));
            Assert.Equal(1, await verify.CreditCards.CountAsync());
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Sign_in_and_failed_sign_in_keep_working_with_that_foreign_key(TestProvider provider)
        {
            EnsureEncryptionConfigured();
            await using var db = await TestDatabase.CreateAsync(provider);
            await AddAuditForeignKeyAsync(db, provider);
            var tckn = TestTckn.Next();

            await using (var context = db.NewContext())
            {
                var registered = await NewAuth(context).RegisterAsync(new RegisterDto
                {
                    Username = "handmade2", Tckn = tckn, Password = "123456", FirstName = "Hand", LastName = "Made", Email = "handmade2@test.example"
                });
                Assert.True(registered.IsSuccess, registered.ErrorKey);
            }

            await using (var context = db.NewContext())
            {
                var wrong = await NewAuth(context).LoginAsync(new LoginDto { Tckn = tckn, Password = "000000" });
                Assert.Equal("InvalidCredentials", wrong.ErrorKey);
            }

            await using (var context = db.NewContext())
            {
                var right = await NewAuth(context).LoginAsync(new LoginDto { Tckn = tckn, Password = "123456" });
                Assert.True(right.IsSuccess, right.ErrorKey);
            }
        }
    }
}
