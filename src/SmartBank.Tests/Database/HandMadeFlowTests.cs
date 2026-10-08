using Microsoft.EntityFrameworkCore;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Tests.Support;

namespace SmartBank.Tests.Database
{
    /// <summary>
    /// Every money flow on a database that carries the foreign key of the hand-made production schema (AuditLogs.UserId to Users):
    /// the first v1.3 registration failed on it because two rows that belong together were saved in one step in an order EF chose.
    /// These flows each save an audit row with other rows; run on a real server they show that none of them depends on that order,
    /// and that the reads that only a real provider can translate (history with its account join, statements, the agent numbers) work.
    /// </summary>
    [Collection("Database")]
    public class HandMadeFlowTests
    {
        private static void EnsureEncryptionConfigured()
        {
            try { SmartBank.Core.Common.EncryptionHelper.Encrypt("probe"); }
            catch (InvalidOperationException) { SmartBank.Core.Common.EncryptionHelper.Configure(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))); }
        }

        private static async Task AddAuditForeignKeyAsync(TestDb db, TestProvider provider)
        {
            await using var context = db.NewContext();
            var sql = provider == TestProvider.PostgreSql
                ? "ALTER TABLE \"AuditLogs\" ADD CONSTRAINT \"FK_AuditLogs_Users_UserId\" FOREIGN KEY (\"UserId\") REFERENCES \"Users\" (\"Id\")"
                : "ALTER TABLE [AuditLogs] ADD CONSTRAINT [FK_AuditLogs_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id])";
            await context.Database.ExecuteSqlRawAsync(sql);
        }

        private static async Task<Account> AddAccountAsync(TestDb db, User user, string number, decimal balance, string currency = "TRY")
        {
            await using var context = db.NewContext();
            var account = new Account { UserId = user.Id, AccountNumber = number, AccountCode = "ACC-" + number[^6..], Balance = balance, Currency = currency, EncryptedCardNumber = "x" };
            context.Accounts.Add(account);
            await context.SaveChangesAsync();
            return account;
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task The_money_flows_work_when_the_audit_trail_has_a_foreign_key_to_the_users(TestProvider provider)
        {
            EnsureEncryptionConfigured();
            await using var db = await TestDatabase.CreateAsync(provider);
            await AddAuditForeignKeyAsync(db, provider);

            User alice;
            await using (var context = db.NewContext())
            {
                alice = new User
                {
                    Username = "alice", Tckn = TestTckn.Next(), Email = "alice@test.example", FirstName = "Alice", LastName = "Tester",
                    FullName = "Alice Tester", PasswordHash = "x"
                };
                context.Users.Add(alice);
                await context.SaveChangesAsync();
            }

            var a = await AddAccountAsync(db, alice, "TR0000000000000001", 5000m);
            var b = await AddAccountAsync(db, alice, "TR0000000000000002", 0m);
            var rates = new FakeMarketRates(); // USD 30.00 / 31.00
            var otp = new FakeOtpDelivery();

            Task<T> Run<T>(Func<SmartBank.Infrastructure.Services.BankingService, Task<T>> action) => ConcurrencyHarness.InNewRequestAsync(db, rates, otp, action);

            Assert.True((await Run(s => s.DepositMoneyAsync(alice.Id, a.AccountNumber, 100m))).IsSuccess);
            Assert.True((await Run(s => s.TransferMoneyAsync(alice.Id, new TransferRequestDto { SourceAccountNumber = a.AccountNumber, DestinationAccountNumber = b.AccountNumber, Amount = 50m }))).IsSuccess);

            var bought = await Run(s => s.ExchangeMoneyAsync(alice.Id, new ExchangeDto { SourceAccountId = a.Id.ToString(), Asset = "USD", Action = "buy", Amount = 10m }));
            Assert.True(bought.IsSuccess, bought.ErrorKey);
            Assert.Equal(("TRY", 310.00m, "USD", 10.00m), (bought.Data!.SourceCurrency, bought.Data.SourceAmount, bought.Data.DestinationCurrency, bought.Data.DestinationAmount));

            var card = await Run(s => s.CreateCreditCardAsync(alice.Id));
            Assert.True(card.IsSuccess, card.ErrorKey);
            var cardId = card.Data!.Id;
            var statements = await Run(s => s.GetStatementsAsync(cardId, alice.Id)); // a "from the beginning" date goes into the query
            Assert.True(statements.IsSuccess, statements.ErrorKey);
            Assert.True((await Run(s => s.ChargeCreditCardAsync(alice.Id, cardId, 100m, "shop"))).IsSuccess);
            Assert.True((await Run(s => s.AdvanceStatementPeriodAsync(alice.Id, card.Data.Id))).IsSuccess);
            Assert.True((await Run(s => s.PayCreditCardDebtAsync(alice.Id, cardId, new PayCreditCardDebtDto { SourceAccountNumber = a.AccountNumber, Amount = 50m }))).IsSuccess);

            var order = await Run(s => s.CreateStandingOrderAsync(alice.Id, new CreateStandingOrderDto { SourceAccountNumber = a.AccountNumber, DestinationAccountNumber = b.AccountNumber, Amount = 10m, Frequency = "Daily", OrderType = "Transfer" }));
            Assert.True(order.IsSuccess, order.ErrorKey);
            Assert.True((await Run(s => s.GetStandingOrdersAsync(alice.Id))).IsSuccess);
            Assert.True((await Run(s => s.DeleteStandingOrderAsync(alice.Id, order.Data!.Id))).IsSuccess);

            var contact = await Run(s => s.SaveContactAsync(alice.Id, new CreateSavedContactDto { AccountNumber = "TR0000000000000009", Alias = "mum" }));
            Assert.True(contact.IsSuccess);
            Assert.True((await Run(s => s.DeleteContactAsync(alice.Id, contact.Data!.Id))).IsSuccess);

            var history = await Run(s => s.GetTransactionsAsync(a.Id, alice.Id));
            Assert.True(history.IsSuccess, history.ErrorKey);
            Assert.Contains(history.Data!, t => t.DestinationCurrency == "USD" && t.DestinationAmount == 10.00m && t.SourceAmount == 310.00m);

            Account usd;
            await using (var context = db.NewContext())
            {
                usd = await context.Accounts.AsNoTracking().SingleAsync(x => x.Currency == "USD");
            }

            // Closing: the foreign-currency account (converted at the bank's price), then one with history.
            Assert.True((await Run(s => s.DeleteAccountAsync(alice.Id, usd.Id, a.Id))).IsSuccess);
            Assert.True((await Run(s => s.DeleteAccountAsync(alice.Id, b.Id, a.Id))).IsSuccess);

            await using var verify = db.NewContext();
            Assert.Equal(1, await verify.Accounts.CountAsync());
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Two_accounts_of_one_customer_closed_at_the_same_moment_leave_one_account(TestProvider provider)
        {
            await using var db = await TestDatabase.CreateAsync(provider);
            User alice;
            await using (var context = db.NewContext())
            {
                alice = new User
                {
                    Username = "alice", Tckn = TestTckn.Next(), Email = "alice@test.example", FirstName = "Alice", LastName = "Tester",
                    FullName = "Alice Tester", PasswordHash = "x"
                };
                context.Users.Add(alice);
                await context.SaveChangesAsync();
            }

            var first = await AddAccountAsync(db, alice, "TR0000000000000001", 0m);
            var second = await AddAccountAsync(db, alice, "TR0000000000000002", 0m);

            var results = await ConcurrencyHarness.RunTogetherAsync(new[]
            {
                new Func<Task<SmartBank.Core.Common.ServiceResult<bool>>>(() => ConcurrencyHarness.InNewRequestAsync(db, s => s.DeleteAccountAsync(alice.Id, first.Id))),
                new Func<Task<SmartBank.Core.Common.ServiceResult<bool>>>(() => ConcurrencyHarness.InNewRequestAsync(db, s => s.DeleteAccountAsync(alice.Id, second.Id)))
            });

            await using var verify = db.NewContext();
            Assert.Equal(1, await verify.Accounts.CountAsync()); // never zero
            Assert.Equal(1, results.Count(r => r.IsSuccess));
            Assert.Contains(results, r => r.ErrorKey == "CannotDeleteLastAccount");
        }

        [DatabaseTheory]
        [MemberData(nameof(TestDatabase.Providers), MemberType = typeof(TestDatabase))]
        public async Task Two_accounts_closed_at_the_same_moment_never_leave_the_customer_without_an_account_round_after_round(TestProvider provider)
        {
            // The race is a matter of microseconds (one request reads the customer just after the other has committed), so a single
            // attempt proves little. Many rounds, each with its own customer.
            await using var db = await TestDatabase.CreateAsync(provider);

            for (var round = 0; round < 40; round++)
            {
                User customer;
                await using (var context = db.NewContext())
                {
                    customer = new User
                    {
                        Username = "round" + round, Tckn = TestTckn.Next(), Email = $"round{round}@test.example", FirstName = "Round", LastName = "Tester",
                        FullName = "Round Tester", PasswordHash = "x"
                    };
                    context.Users.Add(customer);
                    await context.SaveChangesAsync();
                }

                var first = await AddAccountAsync(db, customer, $"TR0000000000{round:D4}01", 0m);
                var second = await AddAccountAsync(db, customer, $"TR0000000000{round:D4}02", 0m);

                await ConcurrencyHarness.RunTogetherAsync(new[]
                {
                    new Func<Task<SmartBank.Core.Common.ServiceResult<bool>>>(() => ConcurrencyHarness.InNewRequestAsync(db, s => s.DeleteAccountAsync(customer.Id, first.Id))),
                    new Func<Task<SmartBank.Core.Common.ServiceResult<bool>>>(() => ConcurrencyHarness.InNewRequestAsync(db, s => s.DeleteAccountAsync(customer.Id, second.Id)))
                });

                await using var verify = db.NewContext();
                Assert.True(await verify.Accounts.CountAsync(a => a.UserId == customer.Id) == 1, $"round {round}: the customer was left with no account");
            }
        }
    }
}
