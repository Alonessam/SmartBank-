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

            // The same answer as for an account that does not exist; the owner's own request (above) is the positive control.
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("AccountNotFound", body);
            Assert.DoesNotContain("123.45", body);

            var missing = await attackerClient.GetAsync($"/api/banking/transactions/{Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(body, await missing.Content.ReadAsStringAsync());
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

            // Positive control: the same request from the attacker's OWN account to the victim's account goes through.
            var own = await client.PostAsJsonAsync("/api/banking/transfer", new
            {
                sourceAccountNumber = attackerAccount,
                destinationAccountNumber = victimAccount,
                amount = 100m,
                description = "gift"
            });
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            Assert.Equal(victimBefore + 100m, await BalanceOfAsync(victimAccount));
        }

        [Fact]
        public async Task Money_cannot_be_deposited_into_another_customers_account()
        {
            var (_, attacker, victimAccount, _, _) = await TwoCustomersAsync();
            var before = await BalanceOfAsync(victimAccount);

            using var client = _factory.ClientFor(attacker);
            var response = await client.PostAsJsonAsync("/api/banking/deposit", new { accountNumber = victimAccount, amount = 500m });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("AccountNotFound", await response.Content.ReadAsStringAsync());
            Assert.Equal(before, await BalanceOfAsync(victimAccount));

            // Positive control: the same request for the caller's own account works.
            var ownAccount = (await _factory.GetFirstAccountAsync(attacker)).AccountNumber;
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/banking/deposit", new { accountNumber = ownAccount, amount = 5m })).StatusCode);
        }

        [Fact]
        public async Task Another_customers_account_cannot_be_closed()
        {
            var (_, attacker, victimAccount, victimAccountId, _) = await TwoCustomersAsync();

            using var client = _factory.ClientFor(attacker);
            var response = await client.DeleteAsync($"/api/banking/accounts/{victimAccountId}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("AccountNotFound", await response.Content.ReadAsStringAsync());
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
            Assert.Equal(HttpStatusCode.NotFound, statements.StatusCode);
            Assert.Contains("CreditCardNotFound", await statements.Content.ReadAsStringAsync());

            var pay = await client.PostAsJsonAsync($"/api/banking/credit-cards/{victimCard}/pay", new { sourceAccountNumber = attackerAccount, amount = 10m });
            Assert.Equal(HttpStatusCode.NotFound, pay.StatusCode);
            Assert.Contains("CreditCardNotFound", await pay.Content.ReadAsStringAsync());

            var charge = await client.PostAsync($"/api/banking/credit-cards/{victimCard}/charge?amount=10&description=steal", null);
            Assert.Equal(HttpStatusCode.NotFound, charge.StatusCode);
            Assert.Contains("CreditCardNotFound", await charge.Content.ReadAsStringAsync());

            var advance = await client.PostAsync($"/api/banking/credit-cards/{victimCard}/advance-period", null);
            Assert.Equal(HttpStatusCode.NotFound, advance.StatusCode);
            Assert.Contains("CreditCardNotFound", await advance.Content.ReadAsStringAsync());

            Assert.Equal(attackerBefore, await BalanceOfAsync(attackerAccount));

            // Positive controls: the owner reaches the same four endpoints with the same kind of request.
            using var owner = _factory.ClientFor(victim);
            var ownerAccount = (await _factory.GetFirstAccountAsync(victim)).AccountNumber;
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/banking/credit-cards/{victimCard}/statements")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/banking/credit-cards/{victimCard}/pay", new { sourceAccountNumber = ownerAccount, amount = 10m })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/banking/credit-cards/{victimCard}/charge?amount=10&description=ok", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/banking/credit-cards/{victimCard}/advance-period", null)).StatusCode);
        }

        [Fact]
        public async Task A_customer_cannot_pay_their_card_from_someone_elses_account()
        {
            var (victim, attacker, victimAccount, _, _) = await TwoCustomersAsync();
            var attackerCard = await _factory.GetCreditCardIdAsync(attacker);
            var before = await BalanceOfAsync(victimAccount);

            using var client = _factory.ClientFor(attacker);
            var response = await client.PostAsJsonAsync($"/api/banking/credit-cards/{attackerCard}/pay", new { sourceAccountNumber = victimAccount, amount = 10m });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("SourceAccountNotFound", await response.Content.ReadAsStringAsync());
            Assert.Equal(before, await BalanceOfAsync(victimAccount));

            // Positive control: from the attacker's own account the same payment works.
            var ownAccount = (await _factory.GetFirstAccountAsync(attacker)).AccountNumber;
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/banking/credit-cards/{attackerCard}/pay", new { sourceAccountNumber = ownAccount, amount = 10m })).StatusCode);
        }
    }
}
