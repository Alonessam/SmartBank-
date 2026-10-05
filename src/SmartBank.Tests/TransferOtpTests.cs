using Microsoft.EntityFrameworkCore;
using Moq;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;
using SmartBank.Core.Security;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Services;

namespace SmartBank.Tests
{
    /// <summary>
    /// A transfer above 2000 TRY from a fresh account needs a one-time code. These tests pin down what that code
    /// is allowed to approve: only that exact transfer, only once, and only for a limited number of guesses.
    /// </summary>
    public class TransferOtpTests
    {
        private const string Source = "TR000000000000000000000001";
        private const string DestinationA = "TR000000000000000000000002";
        private const string DestinationB = "TR000000000000000000000003";

        private readonly FakeOtpDelivery _otp = new();

        private static SmartBankDbContext NewContext() =>
            new(new DbContextOptionsBuilder<SmartBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options);

        private BankingService NewService(SmartBankDbContext context) =>
            new(context, _otp, new Mock<IMarketRateService>().Object);

        private static async Task<User> SeedAsync(SmartBankDbContext context)
        {
            var sender = new User { Username = "sender", Tckn = "11111111111", PasswordHash = "x", FullName = "Sender", Email = "s@test.com" };
            var receiver = new User { Username = "receiver", Tckn = "22222222222", PasswordHash = "x", FullName = "Receiver", Email = "r@test.com" };
            context.Users.AddRange(sender, receiver);

            context.Accounts.AddRange(
                new Account { UserId = sender.Id, AccountNumber = Source, AccountCode = "ACC-1", Balance = 10000m, Currency = "TRY" },
                new Account { UserId = receiver.Id, AccountNumber = DestinationA, AccountCode = "ACC-2", Balance = 0m, Currency = "TRY" },
                new Account { UserId = receiver.Id, AccountNumber = DestinationB, AccountCode = "ACC-3", Balance = 0m, Currency = "TRY" });

            await context.SaveChangesAsync();
            return sender;
        }

        private static TransferRequestDto Transfer(decimal amount, string destination = DestinationA, string? code = null) => new()
        {
            SourceAccountNumber = Source,
            DestinationAccountNumber = destination,
            Amount = amount,
            Description = "test",
            OtpCode = code
        };

        private static async Task<decimal> BalanceAsync(SmartBankDbContext context, string accountNumber) =>
            (await context.Accounts.AsNoTracking().SingleAsync(a => a.AccountNumber == accountNumber)).Balance;

        [Fact]
        public async Task A_Large_Transfer_Asks_For_A_Code_Moves_No_Money_And_Hides_The_Code()
        {
            using var context = NewContext();
            var sender = await SeedAsync(context);

            var result = await NewService(context).TransferMoneyAsync(sender.Id, Transfer(2500m));

            Assert.False(result.IsSuccess);
            Assert.Equal("SuspectedFraudHighValue", result.ErrorKey);
            Assert.DoesNotContain("OTP:", result.Message);
            Assert.Equal(OtpPurpose.Transfer, Assert.Single(_otp.Sent).Purpose);
            Assert.Equal(10000m, await BalanceAsync(context, Source));
        }

        [Fact]
        public async Task The_Code_Is_Only_In_The_Response_In_Demo_Mode()
        {
            using var context = NewContext();
            var sender = await SeedAsync(context);
            _otp.ExposeCodeInResponse = true;

            var result = await NewService(context).TransferMoneyAsync(sender.Id, Transfer(2500m));

            Assert.EndsWith($"|OTP:{_otp.LastCode}", result.Message);
        }

        [Fact]
        public async Task The_Right_Code_Approves_The_Same_Transfer_Exactly_Once()
        {
            using var context = NewContext();
            var sender = await SeedAsync(context);
            var service = NewService(context);
            await service.TransferMoneyAsync(sender.Id, Transfer(2500m));
            var code = _otp.LastCode;

            var approved = await service.TransferMoneyAsync(sender.Id, Transfer(2500m, code: code));
            var replay = await service.TransferMoneyAsync(sender.Id, Transfer(2500m, code: code));

            Assert.True(approved.IsSuccess);
            Assert.Equal(7500m, await BalanceAsync(context, Source));
            Assert.Equal(2500m, await BalanceAsync(context, DestinationA));

            Assert.False(replay.IsSuccess);
            Assert.Equal("InvalidOtpCode", replay.ErrorKey);
            Assert.Equal(7500m, await BalanceAsync(context, Source)); // the replay moved nothing
        }

        [Fact]
        public async Task A_Code_Issued_For_One_Amount_Cannot_Approve_A_Larger_One()
        {
            using var context = NewContext();
            var sender = await SeedAsync(context);
            var service = NewService(context);
            await service.TransferMoneyAsync(sender.Id, Transfer(2500m));

            var result = await service.TransferMoneyAsync(sender.Id, Transfer(9000m, code: _otp.LastCode));

            Assert.False(result.IsSuccess);
            Assert.Equal("InvalidOtpCode", result.ErrorKey);
            Assert.Equal(10000m, await BalanceAsync(context, Source));
        }

        [Fact]
        public async Task A_Code_Issued_For_One_Recipient_Cannot_Approve_A_Transfer_To_Another()
        {
            using var context = NewContext();
            var sender = await SeedAsync(context);
            var service = NewService(context);
            await service.TransferMoneyAsync(sender.Id, Transfer(2500m, DestinationA));

            var result = await service.TransferMoneyAsync(sender.Id, Transfer(2500m, DestinationB, _otp.LastCode));

            Assert.False(result.IsSuccess);
            Assert.Equal(10000m, await BalanceAsync(context, Source));
            Assert.Equal(0m, await BalanceAsync(context, DestinationB));
        }

        [Fact]
        public async Task Guessing_The_Code_Is_Cut_Off_After_Five_Tries()
        {
            using var context = NewContext();
            var sender = await SeedAsync(context);
            var service = NewService(context);
            await service.TransferMoneyAsync(sender.Id, Transfer(2500m));
            var real = _otp.LastCode;
            var wrong = real == "000000" ? "000001" : "000000";

            for (var i = 1; i < OtpManager.MaxFailedAttempts; i++)
            {
                var attempt = await service.TransferMoneyAsync(sender.Id, Transfer(2500m, code: wrong));
                Assert.Equal("InvalidOtpCode", attempt.ErrorKey);
            }

            var cutOff = await service.TransferMoneyAsync(sender.Id, Transfer(2500m, code: wrong));
            var withRealCode = await service.TransferMoneyAsync(sender.Id, Transfer(2500m, code: real));

            Assert.Equal("TooManyOtpAttempts", cutOff.ErrorKey);
            Assert.False(withRealCode.IsSuccess);
            Assert.Equal(10000m, await BalanceAsync(context, Source));
        }

        [Fact]
        public async Task A_Login_Code_Cannot_Approve_A_Transfer()
        {
            using var context = NewContext();
            var sender = await SeedAsync(context);
            var tracked = await context.Users.SingleAsync(u => u.Id == sender.Id);
            var loginCode = OtpManager.Issue(tracked, OtpPurpose.Login, DateTime.UtcNow);
            await context.SaveChangesAsync();

            var result = await NewService(context).TransferMoneyAsync(sender.Id, Transfer(2500m, code: loginCode));

            Assert.False(result.IsSuccess);
            Assert.Equal("InvalidOtpCode", result.ErrorKey);
            Assert.Equal(10000m, await BalanceAsync(context, Source));
        }

        [Fact]
        public async Task A_Small_Transfer_Needs_No_Code()
        {
            using var context = NewContext();
            var sender = await SeedAsync(context);

            var result = await NewService(context).TransferMoneyAsync(sender.Id, Transfer(100m));

            Assert.True(result.IsSuccess);
            Assert.Empty(_otp.Sent);
        }
    }
}
