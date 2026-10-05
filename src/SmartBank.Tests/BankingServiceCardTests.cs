using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartBank.Core.Common;
using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Services;
using Microsoft.Extensions.Configuration;

namespace SmartBank.Tests
{
    // Uses EncryptionHelper (static key), so it shares the collection with EncryptionHelperTests.
    [Collection("EncryptionHelper")]
    public class BankingServiceCardTests
    {
        public BankingServiceCardTests()
        {
            EncryptionHelper.Configure(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        }

        private static SmartBankDbContext NewContext() =>
            new(new DbContextOptionsBuilder<SmartBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options);

        private static BankingService NewService(SmartBankDbContext context) =>
            new(context, new FakeOtpDelivery(), new Mock<IMarketRateService>().Object);

        private static async Task<User> AddUserAsync(SmartBankDbContext context)
        {
            var user = new User
            {
                Username = "card-tester",
                Tckn = "11111111111",
                PasswordHash = "hash",
                FullName = "Card Tester",
                Email = "card@test.com"
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();
            return user;
        }

        [Fact]
        public async Task CreateAccount_Encrypts_The_Card_Number_And_Returns_The_Cvv_Only_Once()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context);
            var service = NewService(context);

            var created = await service.CreateAccountAsync(user.Id, "USD");

            Assert.True(created.IsSuccess);
            var dto = created.Data!;
            Assert.Matches("^4[0-9]{15}$", dto.CardNumber);
            Assert.Matches("^[0-9]{3}$", dto.CardCvv);

            var stored = await context.Accounts.SingleAsync(a => a.Id == dto.Id);
            Assert.StartsWith("v1:", stored.EncryptedCardNumber);
            Assert.DoesNotContain(dto.CardNumber, stored.EncryptedCardNumber);

            var read = (await service.GetAccountsAsync(user.Id)).Data!.Single();
            Assert.Equal(dto.CardNumber, read.CardNumber);
            Assert.Equal(string.Empty, read.CardCvv);
        }

        [Fact]
        public async Task CreateCreditCard_Stores_A_Keyed_Hash_And_Never_The_Cvv()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context);
            var service = NewService(context);

            var created = await service.CreateCreditCardAsync(user.Id);

            Assert.True(created.IsSuccess);
            var dto = created.Data!;
            Assert.Matches("^[0-9]{3}$", dto.CardCvv);

            var stored = await context.CreditCards.SingleAsync(c => c.Id == dto.Id);
            Assert.Equal(EncryptionHelper.HashCardNumber(dto.CardNumber), stored.CardNumberHash);
            Assert.Equal(dto.CardNumber, EncryptionHelper.Decrypt(stored.EncryptedCardNumber));

            var read = (await service.GetCreditCardsAsync(user.Id)).Data!.Single();
            Assert.Equal(dto.CardNumber, read.CardNumber);
            Assert.Equal(string.Empty, read.CardCvv);
        }

        [Fact]
        public async Task GetCreditCards_Survives_A_Legacy_Row_That_Cannot_Be_Decrypted()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context);

            // A row written before v1.1: old format, no hash.
            context.CreditCards.Add(new CreditCard
            {
                UserId = user.Id,
                EncryptedCardNumber = "bGVnYWN5LWNpcGhlcnRleHQ=",
                CardLimit = 10000m
            });
            await context.SaveChangesAsync();

            var result = await NewService(context).GetCreditCardsAsync(user.Id);

            Assert.True(result.IsSuccess);
            Assert.Equal(string.Empty, result.Data!.Single().CardNumber);
        }

        [Fact]
        public async Task GetStandingOrders_Exposes_Only_The_Last_Four_Digits_Of_The_Card()
        {
            using var context = NewContext();
            var user = await AddUserAsync(context);
            var service = NewService(context);

            var card = (await service.CreateCreditCardAsync(user.Id)).Data!;
            context.StandingOrders.Add(new StandingOrder
            {
                UserId = user.Id,
                SourceAccountNumber = "TR000000000000000000000000",
                OrderType = "CreditCardAutoPay",
                CreditCardId = card.Id
            });
            await context.SaveChangesAsync();

            var order = (await service.GetStandingOrdersAsync(user.Id)).Data!.Single();

            Assert.Equal(card.CardNumber[^4..], order.CreditCardLast4);
        }
    }
}
