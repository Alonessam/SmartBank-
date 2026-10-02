using Microsoft.EntityFrameworkCore;
using Moq;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Services;

namespace SmartBank.Tests
{
    /// <summary>The audit trail records who called, and internal errors stay out of what the client sees.</summary>
    public class AuditAndErrorTests
    {
        private const string Source = "TR0000000000000001";
        private const string Destination = "TR0000000000000002";

        /// <summary>A context whose saves can be made to fail with a message that must never reach a customer.</summary>
        private sealed class FailingContext : SmartBankDbContext
        {
            public bool Fail { get; set; }

            public FailingContext(DbContextOptions<SmartBankDbContext> options) : base(options) { }

            public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
                Fail
                    ? throw new InvalidOperationException("connection string: Server=prod-db;Password=hunter2")
                    : base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        private static FailingContext NewContext() =>
            new(new DbContextOptionsBuilder<SmartBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options);

        private static async Task<User> SeedAsync(SmartBankDbContext context)
        {
            var sender = new User { Username = "sender", Tckn = "11111111111", PasswordHash = "x", FullName = "Sender", Email = "s@test.com" };
            var receiver = new User { Username = "receiver", Tckn = "22222222222", PasswordHash = "x", FullName = "Receiver", Email = "r@test.com" };
            context.Users.AddRange(sender, receiver);
            context.Accounts.AddRange(
                new Account { UserId = sender.Id, AccountNumber = Source, AccountCode = "ACC-1", Balance = 1000m, Currency = "TRY" },
                new Account { UserId = receiver.Id, AccountNumber = Destination, AccountCode = "ACC-2", Balance = 0m, Currency = "TRY" });
            await context.SaveChangesAsync();
            return sender;
        }

        private static BankingService NewService(SmartBankDbContext context, IClientInfo? clientInfo) =>
            new(context, new FakeOtpDelivery(), new Mock<IMarketRateService>().Object, clientInfo);

        private static TransferRequestDto SmallTransfer() => new()
        {
            SourceAccountNumber = Source,
            DestinationAccountNumber = Destination,
            Amount = 100m,
            Description = "audit test"
        };

        [Fact]
        public async Task A_transfer_is_audited_with_the_callers_address()
        {
            await using var context = NewContext();
            var sender = await SeedAsync(context);
            var clientInfo = new Mock<IClientInfo>();
            clientInfo.SetupGet(c => c.IpAddress).Returns("203.0.113.9");

            var result = await NewService(context, clientInfo.Object).TransferMoneyAsync(sender.Id, SmallTransfer());

            Assert.True(result.IsSuccess);
            var audit = await context.AuditLogs.SingleAsync(a => a.Action == "TransferMoney");
            Assert.Equal("203.0.113.9", audit.IpAddress);
        }

        [Fact]
        public async Task Without_a_request_the_audit_says_unknown_instead_of_pretending_to_be_localhost()
        {
            await using var context = NewContext();
            var sender = await SeedAsync(context);

            await NewService(context, clientInfo: null).TransferMoneyAsync(sender.Id, SmallTransfer());

            var audit = await context.AuditLogs.SingleAsync(a => a.Action == "TransferMoney");
            Assert.Equal("unknown", audit.IpAddress);
        }

        [Fact]
        public async Task An_internal_failure_during_a_transfer_does_not_leak_its_message_to_the_client()
        {
            await using var context = NewContext();
            var sender = await SeedAsync(context);
            context.Fail = true;

            var result = await NewService(context, clientInfo: null).TransferMoneyAsync(sender.Id, SmallTransfer());

            Assert.False(result.IsSuccess);
            Assert.Equal("TransactionFailed", result.ErrorKey);
            Assert.DoesNotContain("hunter2", result.Message);
            Assert.DoesNotContain("prod-db", result.Message);
        }
    }
}
