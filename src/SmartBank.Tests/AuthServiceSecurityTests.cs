using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Security;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Services;

namespace SmartBank.Tests
{
    public class AuthServiceSecurityTests
    {
        private const string Tckn = "12345678901";
        private const string Pin = "123456";
        private const string ClientIp = "203.0.113.7";

        private readonly FakeOtpDelivery _otp = new();

        private static string WrongPin(string pin) => pin == "000000" ? "000001" : "000000";

        private static SmartBankDbContext NewContext() =>
            new(new DbContextOptionsBuilder<SmartBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        private AuthService NewService(SmartBankDbContext context)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JwtSettings:Key"] = "unit-test-signing-key-0123456789-abcdef"
                })
                .Build();

            return new AuthService(context, config, _otp);
        }

        private static async Task<User> AddUserAsync(SmartBankDbContext context, bool twoFactor = false)
        {
            var user = new User
            {
                Username = "auth-tester",
                Tckn = Tckn,
                // Work factor 4 keeps the tests fast; production uses the default.
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Pin, 4),
                FullName = "Auth Tester",
                Email = "auth@test.com",
                TwoFactorEnabled = twoFactor
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();
            return user;
        }

        private static LoginDto Login(string pin = Pin, string tckn = Tckn) => new() { Tckn = tckn, Password = pin };

        // ---- login ------------------------------------------------------------------------------------------

        [Fact]
        public async Task Login_With_The_Right_Pin_Returns_A_Token()
        {
            using var context = NewContext();
            await AddUserAsync(context);

            var result = await NewService(context).LoginAsync(Login(), ClientIp);

            Assert.True(result.IsSuccess);
            Assert.False(string.IsNullOrEmpty(result.Data!.Token));
        }

        [Fact]
        public async Task An_Unknown_Tckn_Gets_The_Same_Answer_As_A_Wrong_Pin()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);

            var unknown = await service.LoginAsync(Login(tckn: "99999999999"), ClientIp);
            var wrongPin = await service.LoginAsync(Login(WrongPin(Pin)), ClientIp);

            Assert.False(unknown.IsSuccess);
            Assert.Equal(wrongPin.ErrorKey, unknown.ErrorKey);
            Assert.Equal(wrongPin.Message, unknown.Message);
        }

        [Fact]
        public async Task Five_Wrong_Pins_Lock_The_Account_Even_Against_The_Right_Pin()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);

            for (var i = 0; i < LoginLockout.MaxFailedAttempts; i++)
            {
                var attempt = await service.LoginAsync(Login(WrongPin(Pin)), ClientIp);
                Assert.Equal("InvalidCredentials", attempt.ErrorKey);
            }

            var withRightPin = await service.LoginAsync(Login(), ClientIp);
            var wrongPin = await service.LoginAsync(Login(WrongPin(Pin)), ClientIp);
            var unknown = await service.LoginAsync(Login(tckn: "99999999999"), ClientIp);

            // A locked account answers exactly like a wrong PIN or an unknown T.C. number: nothing tells them apart.
            Assert.False(withRightPin.IsSuccess);
            Assert.Equal("InvalidCredentials", withRightPin.ErrorKey);
            Assert.Equal(AuthService.InvalidCredentialsMessage, withRightPin.Message);
            Assert.Equal(wrongPin.ErrorKey, withRightPin.ErrorKey);
            Assert.Equal(wrongPin.Message, withRightPin.Message);
            Assert.Equal(unknown.ErrorKey, withRightPin.ErrorKey);
            Assert.Equal(unknown.Message, withRightPin.Message);
        }

        [Fact]
        public async Task Attempts_During_A_Lock_Do_Not_Extend_It()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context);
            var service = NewService(context);
            for (var i = 0; i < LoginLockout.MaxFailedAttempts; i++) await service.LoginAsync(Login(WrongPin(Pin)), ClientIp);
            var lockEnd = (await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).LockoutEnd;

            await service.LoginAsync(Login(WrongPin(Pin)), ClientIp);
            await service.LoginAsync(Login(), ClientIp);

            Assert.Equal(lockEnd, (await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).LockoutEnd);
        }

        [Fact]
        public async Task A_Successful_Login_Resets_The_Failure_Counter()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);

            for (var i = 0; i < LoginLockout.MaxFailedAttempts - 1; i++) await service.LoginAsync(Login(WrongPin(Pin)), ClientIp);
            Assert.True((await service.LoginAsync(Login(), ClientIp)).IsSuccess);

            // Four more misses are again below the limit, so the account is not locked.
            for (var i = 0; i < LoginLockout.MaxFailedAttempts - 1; i++) await service.LoginAsync(Login(WrongPin(Pin)), ClientIp);

            Assert.True((await service.LoginAsync(Login(), ClientIp)).IsSuccess);
        }

        [Fact]
        public async Task A_Lock_That_Has_Ended_Lets_The_User_In_Again()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context);
            user.FailedLoginCount = LoginLockout.MaxFailedAttempts;
            user.LockoutEnd = DateTime.UtcNow.AddMinutes(-1);
            await context.SaveChangesAsync();

            var result = await NewService(context).LoginAsync(Login(), ClientIp);

            Assert.True(result.IsSuccess);
        }

        [Fact]
        public async Task Failed_Logins_And_The_Lock_Are_Audited_With_The_Client_Ip()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context);
            var service = NewService(context);

            for (var i = 0; i < LoginLockout.MaxFailedAttempts; i++) await service.LoginAsync(Login(WrongPin(Pin)), ClientIp);

            var logs = await context.AuditLogs.Where(a => a.UserId == user.Id).ToListAsync();
            Assert.Equal(LoginLockout.MaxFailedAttempts - 1, logs.Count(a => a.Action == "LoginFailed"));
            Assert.Single(logs, a => a.Action == "AccountLocked");
            Assert.All(logs, a => Assert.Equal(ClientIp, a.IpAddress));
        }

        // ---- two-factor login -------------------------------------------------------------------------------

        [Fact]
        public async Task Two_Factor_Login_Sends_A_Code_And_Does_Not_Reveal_It_In_The_Response()
        {
            using var context = NewContext();
            await AddUserAsync(context, twoFactor: true);

            var result = await NewService(context).LoginAsync(Login(), ClientIp);

            Assert.Equal("Requires2FA", result.ErrorKey);
            Assert.DoesNotContain("OTP:", result.Message);
            Assert.DoesNotContain(_otp.LastCode, result.Message);
            Assert.Single(_otp.Sent);
            Assert.Equal(OtpPurpose.Login, _otp.Sent[0].Purpose);
        }

        [Fact]
        public async Task The_Code_Is_Only_In_The_Response_When_Demo_Mode_Is_On()
        {
            using var context = NewContext();
            await AddUserAsync(context, twoFactor: true);
            _otp.ExposeCodeInResponse = true;

            var result = await NewService(context).LoginAsync(Login(), ClientIp);

            Assert.Equal("Requires2FA", result.ErrorKey);
            Assert.EndsWith($"|OTP:{_otp.LastCode}", result.Message);
        }

        [Fact]
        public async Task The_Right_Code_Completes_A_Two_Factor_Login_Once()
        {
            using var context = NewContext();
            await AddUserAsync(context, twoFactor: true);
            var service = NewService(context);
            await service.LoginAsync(Login(), ClientIp);
            var verify = new Verify2FaDto { Tckn = Tckn, Code = _otp.LastCode };

            var first = await service.Verify2FaAsync(verify, ClientIp);
            var replay = await service.Verify2FaAsync(verify, ClientIp);

            Assert.True(first.IsSuccess);
            Assert.False(string.IsNullOrEmpty(first.Data!.Token));
            Assert.False(replay.IsSuccess);
        }

        [Fact]
        public async Task Guessing_The_Code_Is_Cut_Off_After_Five_Tries_Even_If_The_Next_Guess_Is_Right()
        {
            using var context = NewContext();
            await AddUserAsync(context, twoFactor: true);
            var service = NewService(context);
            await service.LoginAsync(Login(), ClientIp);
            var real = _otp.LastCode;
            var wrong = real == "000000" ? "000001" : "000000";

            for (var i = 1; i < OtpManager.MaxFailedAttempts; i++)
            {
                var attempt = await service.Verify2FaAsync(new Verify2FaDto { Tckn = Tckn, Code = wrong }, ClientIp);
                Assert.Equal("InvalidOrExpiredCode", attempt.ErrorKey);
            }

            var last = await service.Verify2FaAsync(new Verify2FaDto { Tckn = Tckn, Code = wrong }, ClientIp);
            var afterwards = await service.Verify2FaAsync(new Verify2FaDto { Tckn = Tckn, Code = real }, ClientIp);

            Assert.Equal("TooManyOtpAttempts", last.ErrorKey);
            Assert.False(afterwards.IsSuccess);
        }

        [Fact]
        public async Task An_Unknown_Tckn_Cannot_Be_Told_Apart_From_A_Wrong_Code()
        {
            using var context = NewContext();
            await AddUserAsync(context, twoFactor: true);

            var result = await NewService(context).Verify2FaAsync(new Verify2FaDto { Tckn = "99999999999", Code = "123456" }, ClientIp);

            Assert.Equal("InvalidOrExpiredCode", result.ErrorKey);
        }

        // ---- password reset ---------------------------------------------------------------------------------

        [Fact]
        public async Task Requesting_A_Reset_For_An_Unknown_Tckn_Looks_The_Same_And_Sends_Nothing()
        {
            using var context = NewContext();
            await AddUserAsync(context);

            var result = await NewService(context).RequestPasswordResetAsync(new ForgotPasswordDto { Tckn = "99999999999" }, ClientIp);

            Assert.True(result.IsSuccess);
            Assert.Empty(_otp.Sent);
        }

        [Fact]
        public async Task Requesting_A_Reset_Sends_A_Code_And_Repeated_Requests_Are_Throttled()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);

            Assert.True((await service.RequestPasswordResetAsync(new ForgotPasswordDto { Tckn = Tckn }, ClientIp)).IsSuccess);
            Assert.True((await service.RequestPasswordResetAsync(new ForgotPasswordDto { Tckn = Tckn }, ClientIp)).IsSuccess);

            // The second answer looks identical, but no second e-mail goes out inside the cooldown.
            var sent = Assert.Single(_otp.Sent);
            Assert.Equal(OtpPurpose.PasswordReset, sent.Purpose);
        }

        [Fact]
        public async Task The_Reset_Code_Is_Never_Part_Of_The_Response()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            _otp.ExposeCodeInResponse = true; // demo mode must not leak reset codes either

            var result = await NewService(context).RequestPasswordResetAsync(new ForgotPasswordDto { Tckn = Tckn }, ClientIp);

            Assert.True(result.IsSuccess);
            Assert.True(result.Data);
            Assert.Null(result.Message);
        }

        [Fact]
        public async Task With_The_Right_Code_The_Pin_Changes_And_Any_Lock_Is_Lifted()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context);
            user.FailedLoginCount = 5;
            user.LockoutEnd = DateTime.UtcNow.AddMinutes(10);
            await context.SaveChangesAsync();
            var service = NewService(context);
            await service.RequestPasswordResetAsync(new ForgotPasswordDto { Tckn = Tckn }, ClientIp);

            var reset = await service.ResetPasswordAsync(
                new ResetPasswordDto { Tckn = Tckn, Code = _otp.LastCode, NewPassword = "654321" }, ClientIp);

            Assert.True(reset.IsSuccess);
            Assert.True((await service.LoginAsync(Login("654321"), ClientIp)).IsSuccess);
            Assert.Equal("InvalidCredentials", (await service.LoginAsync(Login(Pin), ClientIp)).ErrorKey);
        }

        [Fact]
        public async Task A_Reset_Code_Works_Only_Once()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);
            await service.RequestPasswordResetAsync(new ForgotPasswordDto { Tckn = Tckn }, ClientIp);
            var dto = new ResetPasswordDto { Tckn = Tckn, Code = _otp.LastCode, NewPassword = "654321" };

            Assert.True((await service.ResetPasswordAsync(dto, ClientIp)).IsSuccess);

            dto.NewPassword = "111111";
            Assert.False((await service.ResetPasswordAsync(dto, ClientIp)).IsSuccess);
        }

        [Fact]
        public async Task A_Wrong_Code_Leaves_The_Pin_Alone_And_Five_Wrong_Codes_Destroy_The_Real_One()
        {
            using var context = NewContext();
            await AddUserAsync(context);
            var service = NewService(context);
            await service.RequestPasswordResetAsync(new ForgotPasswordDto { Tckn = Tckn }, ClientIp);
            var real = _otp.LastCode;
            var wrong = real == "000000" ? "000001" : "000000";

            for (var i = 0; i < OtpManager.MaxFailedAttempts; i++)
            {
                var attempt = await service.ResetPasswordAsync(new ResetPasswordDto { Tckn = Tckn, Code = wrong, NewPassword = "654321" }, ClientIp);
                Assert.False(attempt.IsSuccess);
            }

            var withRealCode = await service.ResetPasswordAsync(new ResetPasswordDto { Tckn = Tckn, Code = real, NewPassword = "654321" }, ClientIp);

            Assert.False(withRealCode.IsSuccess);
            Assert.True((await service.LoginAsync(Login(Pin), ClientIp)).IsSuccess); // the old PIN still works
        }

        [Fact]
        public async Task A_Password_Cannot_Be_Reset_Without_Asking_For_A_Code_First()
        {
            using var context = NewContext();
            await AddUserAsync(context);

            var result = await NewService(context).ResetPasswordAsync(
                new ResetPasswordDto { Tckn = Tckn, Code = "123456", NewPassword = "654321" }, ClientIp);

            Assert.False(result.IsSuccess);
            Assert.Equal("InvalidOrExpiredCode", result.ErrorKey);
        }

        [Fact]
        public async Task A_Login_Code_Cannot_Be_Used_To_Reset_The_Password()
        {
            using var context = NewContext();
            await AddUserAsync(context, twoFactor: true);
            var service = NewService(context);
            await service.LoginAsync(Login(), ClientIp); // issues a Login-purpose code

            var result = await service.ResetPasswordAsync(
                new ResetPasswordDto { Tckn = Tckn, Code = _otp.LastCode, NewPassword = "654321" }, ClientIp);

            Assert.False(result.IsSuccess);
        }

        [Fact]
        public async Task A_Reset_Code_Cannot_Be_Used_To_Complete_A_Login()
        {
            using var context = NewContext();
            await AddUserAsync(context, twoFactor: true);
            var service = NewService(context);
            await service.RequestPasswordResetAsync(new ForgotPasswordDto { Tckn = Tckn }, ClientIp);

            var result = await service.Verify2FaAsync(new Verify2FaDto { Tckn = Tckn, Code = _otp.LastCode }, ClientIp);

            Assert.False(result.IsSuccess);
        }
    }
}
