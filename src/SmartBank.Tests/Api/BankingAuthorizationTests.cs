using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartBank.Infrastructure.Data;

namespace SmartBank.Tests.Api
{
    /// <summary>
    /// Insecure direct object references (IDOR): a signed-in customer must not be able to read or change another
    /// customer's accounts, transactions or cards just by putting that customer's identifiers into a request.
    /// </summary>
    [Collection("EncryptionHelper")]
    public class BankingAuthorizationTests : IClassFixture<ApiFactory>
    {
        private readonly ApiFactory _factory;

        public BankingAuthorizationTests(ApiFactory factory) => _factory = factory;

        private async Task<decimal> BalanceOfAsync(string accountNumber)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartBankDbContext>();
            return (await db.Accounts.AsNoTracking().SingleAsync(a => a.AccountNumber == accountNumber)).Balance;
        }

        private async Task<(ApiFactory.TestUser Victim, ApiFactory.TestUser Attacker, string VictimAccount, Guid VictimAccountId, string AttackerAccount)> TwoCustomersAsync()
        {
            var victim = await _factory.RegisterCustomerAsync();
            var attacker = await _factory.RegisterCustomerAsync();
            var victimAccount = await _factory.GetFirstAccountAsync(victim);
            var attackerAccount = await _factory.GetFirstAccountAsync(attacker);
            return (victim, attacker, victimAccount.AccountNumber, victimAccount.Id, attackerAccount.AccountNumber);
        }

        [Theory]
        [InlineData("GET", "/api/banking/accounts")]
        [InlineData("GET", "/api/banking/credit-cards")]
        [InlineData("GET", "/api/banking/standing-orders")]
        [InlineData("GET", "/api/banking/contacts")]
        [InlineData("POST", "/api/banking/transfer")]
        [InlineData("POST", "/api/banking/deposit")]
        public async Task Anonymous_callers_get_nothing_from_the_banking_api(string method, string url)
        {
            using var client = _factory.ClientFor(null);

            var response = method == "POST"
                ? await client.PostAsJsonAsync(url, new { })
                : await client.GetAsync(url);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task The_account_list_contains_only_the_callers_own_accounts()
        {
            var (victim, attacker, victimAccount, _, attackerAccount) = await TwoCustomersAsync();

            using var client = _factory.ClientFor(attacker);
            var body = await client.GetStringAsync("/api/banking/accounts");

            Assert.Contains(attackerAccount, body);
            Assert.DoesNotContain(victimAccount, body);
        }

        [Fact]
        public async Task Another_customers_transactions_cannot_be_read()
        {
            var (victim, attacker, victimAccount, victimAccountId, _) = await TwoCustomersAsync();

            // Give the victim a transaction worth stealing a look at.
            using (var victimClient = _factory.ClientFor(victim))
            {
                var deposit = await victimClient.PostAsJsonAsync("/api/banking/deposit", new { accountNumber = victimAccount, amount = 123.45m });
                Assert.Equal(HttpStatusCode.OK, deposit.StatusCode);

                var own = await victimClient.GetAsync($"/api/banking/transactions/{victimAccountId}");
                Assert.Equal(HttpStatusCode.OK, own.StatusCode);
                Assert.Contains("123.45", await own.Content.ReadAsStringAsync());
            }

            using var attackerClient = _factory.ClientFor(attacker);
            var response = await attackerClient.GetAsync($"/api/banking/transactions/{victimAccountId}");

            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("123.45", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Money_cannot_be_transferred_out_of_another_customers_account()
        {
            var (_, attacker, victimAccount, _, attackerAccount) = await TwoCustomersAsync();
            var victimBefore = await BalanceOfAsync(victimAccount);
            var attackerBefore = await BalanceOfAsync(attackerAccount);

            using var client = _factory.ClientFor(attacker);
            var response = await client.PostAsJsonAsync("/api/banking/transfer", new
            {
                sourceAccountNumber = victimAccount,
                destinationAccountNumber = attackerAccount,
                amount = 100m,
                description = "steal"
            });

            // "Not yours" answers exactly like "does not exist": the call cannot be used to find out which account numbers exist.
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("SourceAccountNotFound", await response.Content.ReadAsStringAsync());
            Assert.Equal(victimBefore, await BalanceOfAsync(victimAccount));
            Assert.Equal(attackerBefore, await BalanceOfAsync(attackerAccount));
        }

        [Fact]
        public async Task Money_cannot_be_deposited_into_another_customers_account()
        {
            var (_, attacker, victimAccount, _, _) = await TwoCustomersAsync();
            var before = await BalanceOfAsync(victimAccount);

            using var client = _factory.ClientFor(attacker);
            var response = await client.PostAsJsonAsync("/api/banking/deposit", new { accountNumber = victimAccount, amount = 500m });

            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(before, await BalanceOfAsync(victimAccount));
        }

        [Fact]
        public async Task Another_customers_account_cannot_be_closed()
        {
            var (_, attacker, victimAccount, victimAccountId, _) = await TwoCustomersAsync();

            using var client = _factory.ClientFor(attacker);
            var response = await client.DeleteAsync($"/api/banking/accounts/{victimAccountId}");

            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartBankDbContext>();
            Assert.True(await db.Accounts.AnyAsync(a => a.AccountNumber == victimAccount), "The victim's account must still exist.");
        }

        [Fact]
        public async Task Another_customers_credit_card_cannot_be_read_paid_or_charged()
        {
            var (victim, attacker, victimAccount, _, attackerAccount) = await TwoCustomersAsync();
            var victimCard = await _factory.GetCreditCardIdAsync(victim);
            var attackerBefore = await BalanceOfAsync(attackerAccount);

            using var client = _factory.ClientFor(attacker);

            var statements = await client.GetAsync($"/api/banking/credit-cards/{victimCard}/statements");
            Assert.NotEqual(HttpStatusCode.OK, statements.StatusCode);

            var pay = await client.PostAsJsonAsync($"/api/banking/credit-cards/{victimCard}/pay", new { sourceAccountNumber = attackerAccount, amount = 10m });
            Assert.NotEqual(HttpStatusCode.OK, pay.StatusCode);

            var charge = await client.PostAsync($"/api/banking/credit-cards/{victimCard}/charge?amount=10&description=steal", null);
            Assert.NotEqual(HttpStatusCode.OK, charge.StatusCode);

            var advance = await client.PostAsync($"/api/banking/credit-cards/{victimCard}/advance-period", null);
            Assert.NotEqual(HttpStatusCode.OK, advance.StatusCode);

            Assert.Equal(attackerBefore, await BalanceOfAsync(attackerAccount));
        }

        [Fact]
        public async Task A_customer_cannot_pay_their_card_from_someone_elses_account()
        {
            var (victim, attacker, victimAccount, _, _) = await TwoCustomersAsync();
            var attackerCard = await _factory.GetCreditCardIdAsync(attacker);
            var before = await BalanceOfAsync(victimAccount);

            using var client = _factory.ClientFor(attacker);
            var response = await client.PostAsJsonAsync($"/api/banking/credit-cards/{attackerCard}/pay", new { sourceAccountNumber = victimAccount, amount = 10m });

            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(before, await BalanceOfAsync(victimAccount));
        }
    }
}
