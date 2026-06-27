using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Services;
using System;
using System.Threading.Tasks;
using Xunit;

namespace SmartBank.Tests
{
    public class BankingServiceTests
    {
        private readonly Mock<IConfiguration> _configMock;
        private readonly Mock<IMarketRateService> _marketRateMock;

        public BankingServiceTests()
        {
            _configMock = new Mock<IConfiguration>();
            _marketRateMock = new Mock<IMarketRateService>();
        }

        private SmartBankDbContext CreateInMemoryDbContext()
        {
            var options = new DbContextOptionsBuilder<SmartBankDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            return new SmartBankDbContext(options);
        }

        [Fact]
        public async Task TransferMoney_Should_Succeed_When_Balance_Is_Sufficient()
        {
            // Arrange
            using var context = CreateInMemoryDbContext();
            
            var sourceUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "sourceuser",
                Tckn = "11111111111",
                PasswordHash = "hash",
                FullName = "Source User",
                Email = "source@test.com"
            };

            var destUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "destuser",
                Tckn = "22222222222",
                PasswordHash = "hash",
                FullName = "Dest User",
                Email = "dest@test.com"
            };

            context.Users.Add(sourceUser);
            context.Users.Add(destUser);

            var sourceAccount = new Account
            {
                Id = Guid.NewGuid(),
                UserId = sourceUser.Id,
                AccountNumber = "TR111111111111111111111111",
                Balance = 1500.00m,
                Currency = "TRY",
                AccountCode = "ACC-SOURCE"
            };

            var destinationAccount = new Account
            {
                Id = Guid.NewGuid(),
                UserId = destUser.Id,
                AccountNumber = "TR222222222222222222222222",
                Balance = 500.00m,
                Currency = "TRY",
                AccountCode = "ACC-DEST"
            };

            context.Accounts.Add(sourceAccount);
            context.Accounts.Add(destinationAccount);
            await context.SaveChangesAsync();

            var service = new BankingService(context, _configMock.Object, _marketRateMock.Object);

            var request = new TransferRequestDto
            {
                SourceAccountNumber = sourceAccount.AccountNumber,
                DestinationAccountNumber = destinationAccount.AccountNumber,
                Amount = 500.00m,
                Description = "Gift"
            };

            // Act
            var result = await service.TransferMoneyAsync(sourceUser.Id, request);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Data);
            Assert.Equal(500.00m, result.Data.Amount);
            
            // Check balances in database
            var updatedSource = await context.Accounts.FindAsync(sourceAccount.Id);
            var updatedDest = await context.Accounts.FindAsync(destinationAccount.Id);
            Assert.Equal(1000.00m, updatedSource!.Balance);
            Assert.Equal(1000.00m, updatedDest!.Balance);
        }

        [Fact]
        public async Task TransferMoney_Should_Fail_When_Balance_Is_Insufficient()
        {
            // Arrange
            using var context = CreateInMemoryDbContext();
            
            var sourceUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "sourceuser",
                Tckn = "11111111111",
                PasswordHash = "hash",
                FullName = "Source User",
                Email = "source@test.com"
            };

            var destUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "destuser",
                Tckn = "22222222222",
                PasswordHash = "hash",
                FullName = "Dest User",
                Email = "dest@test.com"
            };

            context.Users.Add(sourceUser);
            context.Users.Add(destUser);

            var sourceAccount = new Account
            {
                Id = Guid.NewGuid(),
                UserId = sourceUser.Id,
                AccountNumber = "TR111111111111111111111111",
                Balance = 200.00m,
                Currency = "TRY",
                AccountCode = "ACC-SOURCE"
            };

            var destinationAccount = new Account
            {
                Id = Guid.NewGuid(),
                UserId = destUser.Id,
                AccountNumber = "TR222222222222222222222222",
                Balance = 500.00m,
                Currency = "TRY",
                AccountCode = "ACC-DEST"
            };

            context.Accounts.Add(sourceAccount);
            context.Accounts.Add(destinationAccount);
            await context.SaveChangesAsync();

            var service = new BankingService(context, _configMock.Object, _marketRateMock.Object);

            var request = new TransferRequestDto
            {
                SourceAccountNumber = sourceAccount.AccountNumber,
                DestinationAccountNumber = destinationAccount.AccountNumber,
                Amount = 500.00m,
                Description = "Rent"
            };

            // Act
            var result = await service.TransferMoneyAsync(sourceUser.Id, request);

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Equal("InsufficientFunds", result.ErrorKey);
        }

        [Fact]
        public async Task TransferMoney_Should_Fail_When_Transferring_To_Self()
        {
            // Arrange
            using var context = CreateInMemoryDbContext();
            
            var sourceUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "sourceuser",
                Tckn = "11111111111",
                PasswordHash = "hash",
                FullName = "Source User",
                Email = "source@test.com"
            };

            context.Users.Add(sourceUser);

            var account = new Account
            {
                Id = Guid.NewGuid(),
                UserId = sourceUser.Id,
                AccountNumber = "TR111111111111111111111111",
                Balance = 1000.00m,
                Currency = "TRY",
                AccountCode = "ACC-SOURCE"
            };

            context.Accounts.Add(account);
            await context.SaveChangesAsync();

            var service = new BankingService(context, _configMock.Object, _marketRateMock.Object);

            var request = new TransferRequestDto
            {
                SourceAccountNumber = account.AccountNumber,
                DestinationAccountNumber = account.AccountNumber,
                Amount = 100.00m,
                Description = "Self transfer"
            };

            // Act
            var result = await service.TransferMoneyAsync(sourceUser.Id, request);

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Equal("CannotTransferToSelf", result.ErrorKey);
        }
    }
}
