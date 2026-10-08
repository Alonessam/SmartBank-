using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Data;
using SmartBank.Tests.Support;

namespace SmartBank.Tests.Api
{
    /// <summary>The HTTP surface: error bodies and status codes, validation, paging, switches, health, CORS and OpenAPI, through the real pipeline.</summary>
    [Collection("EncryptionHelper")]
    public class ApiContractTests : IClassFixture<ApiFactory>
    {
        private readonly ApiFactory _factory;

        public ApiContractTests(ApiFactory factory) => _factory = factory;

        private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.Clone();
        }

        // ---- the error contract ------------------------------------------------------------------------------

        [Fact]
        public async Task Every_error_has_the_same_three_fields()
        {
            var user = await _factory.RegisterCustomerAsync();
            using var client = _factory.ClientFor(user);

            var response = await client.GetAsync($"/api/banking/credit-cards/{Guid.NewGuid()}/statements");
            var body = await JsonOf(response);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.False(body.GetProperty("isSuccess").GetBoolean());
            Assert.Equal("CreditCardNotFound", body.GetProperty("errorKey").GetString());
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        }

        [Fact]
        public async Task A_request_that_fails_its_attribute_checks_gets_the_same_shape_plus_the_field_list()
        {
            var user = await _factory.RegisterCustomerAsync();
            var account = await _factory.GetFirstAccountAsync(user);
            using var client = _factory.ClientFor(user);

            var response = await client.PostAsJsonAsync("/api/banking/deposit", new { accountNumber = account.AccountNumber, amount = 1.005m });
            var body = await JsonOf(response);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.False(body.GetProperty("isSuccess").GetBoolean());
            Assert.Equal("ValidationError", body.GetProperty("errorKey").GetString());
            Assert.Contains("2 decimal", body.GetProperty("message").GetString());
            Assert.True(body.GetProperty("errors").TryGetProperty("Amount", out _));
        }

        [Theory]
        [InlineData("/api/banking/deposit", "{\"accountNumber\":\"\",\"amount\":5}")]
        [InlineData("/api/banking/deposit", "{\"accountNumber\":\"TR1\",\"amount\":0}")]
        [InlineData("/api/banking/deposit", "{\"accountNumber\":\"TR1\",\"amount\":10000001}")]
        [InlineData("/api/banking/exchange", "{\"sourceAccountId\":\"x\",\"asset\":\"USD\",\"action\":\"buy\",\"amount\":0.001}")]
        [InlineData("/api/banking/standing-orders", "{\"sourceAccountNumber\":\"TR1\",\"frequency\":\"Daily\",\"orderType\":\"Transfer\",\"amount\":1000001}")]
        [InlineData("/api/banking/contacts", "{\"accountNumber\":\"short\",\"alias\":\"a\"}")]
        public async Task Bad_bodies_are_400_with_the_validation_error_key(string url, string json)
        {
            var user = await _factory.RegisterCustomerAsync();
            using var client = _factory.ClientFor(user);

            var response = await client.PostAsync(url, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("ValidationError", (await JsonOf(response)).GetProperty("errorKey").GetString());
        }

        [Fact]
        public async Task The_transfer_endpoint_still_validates_with_its_fluent_validator()
        {
            var user = await _factory.RegisterCustomerAsync();
            using var client = _factory.ClientFor(user);

            var response = await client.PostAsJsonAsync("/api/banking/transfer", new { sourceAccountNumber = "TR1", destinationAccountNumber = "short", amount = 5m });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("ValidationError", (await JsonOf(response)).GetProperty("errorKey").GetString());
        }

        [Fact]
        public async Task A_charge_with_bad_query_values_is_refused_before_the_service_sees_it()
        {
            var user = await _factory.RegisterCustomerAsync();
            var card = await _factory.GetCreditCardIdAsync(user);
            using var client = _factory.ClientFor(user);

            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/banking/credit-cards/{card}/charge?amount=1.234", null)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/banking/credit-cards/{card}/charge?amount=0", null)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/banking/credit-cards/{card}/charge?amount=5&description={new string('d', 201)}", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/banking/credit-cards/{card}/charge?amount=5.25&description=fine", null)).StatusCode);
        }

        [Fact]
        public async Task The_status_follows_the_kind_of_error()
        {
            var user = await _factory.RegisterCustomerAsync();
            var account = await _factory.GetFirstAccountAsync(user);
            using var client = _factory.ClientFor(user);

            // 404: the thing named does not exist
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/banking/contacts/{Guid.NewGuid()}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/banking/standing-orders/{Guid.NewGuid()}")).StatusCode);
            // 400: a rule of the business
            var lastAccount = await client.DeleteAsync($"/api/banking/accounts/{account.Id}");
            Assert.Equal(HttpStatusCode.BadRequest, lastAccount.StatusCode);
            Assert.Equal("CannotDeleteLastAccount", (await JsonOf(lastAccount)).GetProperty("errorKey").GetString());
            // 400: a one-time-code challenge (the web app depends on this)
            var challenge = await client.PostAsJsonAsync("/api/banking/transfer", new { sourceAccountNumber = account.AccountNumber, destinationAccountNumber = "TR9999999999999999", amount = 5m });
            Assert.Equal("DestinationAccountNotFound", (await JsonOf(challenge)).GetProperty("errorKey").GetString());
            Assert.Equal(HttpStatusCode.NotFound, challenge.StatusCode);
        }

        // ---- paging ------------------------------------------------------------------------------------------

        [Fact]
        public async Task The_history_takes_a_limit_and_comes_newest_first()
        {
            var user = await _factory.RegisterCustomerAsync();
            var account = await _factory.GetFirstAccountAsync(user);
            using var client = _factory.ClientFor(user);
            foreach (var amount in new[] { 1m, 2m, 3m, 4m }) await client.PostAsJsonAsync("/api/banking/deposit", new { accountNumber = account.AccountNumber, amount });

            var two = await JsonOf(await client.GetAsync($"/api/banking/transactions/{account.Id}?take=2"));
            var all = await JsonOf(await client.GetAsync($"/api/banking/transactions/{account.Id}"));
            var zero = await JsonOf(await client.GetAsync($"/api/banking/transactions/{account.Id}?take=0"));

            Assert.Equal(new[] { 4m, 3m }, two.EnumerateArray().Select(t => t.GetProperty("amount").GetDecimal()).ToArray());
            Assert.Equal(4, all.GetArrayLength());
            Assert.Equal(1, zero.GetArrayLength()); // a limit below 1 means 1
        }

        // ---- money endpoints ---------------------------------------------------------------------------------

        [Fact]
        public async Task Opening_an_account_checks_the_currency_and_the_type()
        {
            var user = await _factory.RegisterCustomerAsync();
            using var client = _factory.ClientFor(user);

            var bad = await client.PostAsync("/api/banking/accounts?currency=GBP", null);
            var badType = await client.PostAsync("/api/banking/accounts?currency=USD&accountType=Savings", null);
            var good = await client.PostAsync("/api/banking/accounts?currency=usd", null);

            Assert.Equal("InvalidCurrency", (await JsonOf(bad)).GetProperty("errorKey").GetString());
            Assert.Equal("InvalidAccountType", (await JsonOf(badType)).GetProperty("errorKey").GetString());
            var created = await JsonOf(good);
            Assert.Equal(HttpStatusCode.OK, good.StatusCode);
            Assert.Equal("USD", created.GetProperty("currency").GetString());
            Assert.Matches("^[0-9]{3}$", created.GetProperty("cardCvv").GetString());
        }

        [Fact]
        public async Task An_exchange_works_at_live_prices_and_is_refused_at_stand_in_prices()
        {
            var user = await _factory.RegisterCustomerAsync();
            var account = await _factory.GetFirstAccountAsync(user);
            using var client = _factory.ClientFor(user);
            try
            {
                var buy = await client.PostAsJsonAsync("/api/banking/exchange", new { sourceAccountId = account.Id, asset = "usd", action = "buy", amount = 2m });
                Assert.Equal(HttpStatusCode.OK, buy.StatusCode);
                Assert.Equal(62.00m, (await JsonOf(buy)).GetProperty("amount").GetDecimal());

                _factory.Rates.Fallback = true;
                var refused = await client.PostAsJsonAsync("/api/banking/exchange", new { sourceAccountId = account.Id, asset = "usd", action = "buy", amount = 2m });
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                Assert.Equal("RateUnavailable", (await JsonOf(refused)).GetProperty("errorKey").GetString());
            }
            finally
            {
                _factory.Rates.Fallback = false;
            }
        }

        [Fact]
        public async Task Paying_more_than_the_debt_is_refused_over_http()
        {
            var user = await _factory.RegisterCustomerAsync();
            var account = await _factory.GetFirstAccountAsync(user);
            var card = await _factory.GetCreditCardIdAsync(user);
            using var client = _factory.ClientFor(user);

            await client.PostAsJsonAsync("/api/banking/deposit", new { accountNumber = account.AccountNumber, amount = 500m }); // the debt is 1250, the account holds 1000
            var over = await client.PostAsJsonAsync($"/api/banking/credit-cards/{card}/pay", new { sourceAccountNumber = account.AccountNumber, amount = 1250.01m });
            var exact = await client.PostAsJsonAsync($"/api/banking/credit-cards/{card}/pay", new { sourceAccountNumber = account.AccountNumber, amount = 1250m });

            Assert.Equal("PaymentExceedsDebt", (await JsonOf(over)).GetProperty("errorKey").GetString());
            Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
            Assert.True((await JsonOf(exact)).GetProperty("success").GetBoolean());
        }

        [Fact]
        public async Task A_standing_order_goes_through_validation_creation_listing_and_removal()
        {
            var user = await _factory.RegisterCustomerAsync();
            var account = await _factory.GetFirstAccountAsync(user);
            using var client = _factory.ClientFor(user);
            var second = await JsonOf(await client.PostAsync("/api/banking/accounts?currency=TRY", null));
            var destination = second.GetProperty("accountNumber").GetString();

            var badFrequency = await client.PostAsJsonAsync("/api/banking/standing-orders", new { sourceAccountNumber = account.AccountNumber, destinationAccountNumber = destination, amount = 10m, frequency = "Yearly", orderType = "Transfer" });
            Assert.Equal("InvalidFrequency", (await JsonOf(badFrequency)).GetProperty("errorKey").GetString());

            var created = await client.PostAsJsonAsync("/api/banking/standing-orders", new { sourceAccountNumber = account.AccountNumber, destinationAccountNumber = destination, amount = 10m, frequency = "Weekly", orderType = "Transfer" });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var id = (await JsonOf(created)).GetProperty("id").GetGuid();

            var list = await JsonOf(await client.GetAsync("/api/banking/standing-orders"));
            Assert.Equal(id, Assert.Single(list.EnumerateArray()).GetProperty("id").GetGuid());

            Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/banking/standing-orders/{id}")).StatusCode);
            Assert.Equal(0, (await JsonOf(await client.GetAsync("/api/banking/standing-orders"))).GetArrayLength());
        }

        // ---- the two-factor switch needs the PIN -------------------------------------------------------------

        [Fact]
        public async Task The_two_factor_switch_over_http_needs_the_pin()
        {
            var user = await _factory.RegisterCustomerAsync();
            using var client = _factory.ClientFor(user);

            var missing = await client.PostAsJsonAsync("/api/auth/toggle-2fa", new { enable = true });
            var wrong = await client.PostAsJsonAsync("/api/auth/toggle-2fa", new { enable = true, password = "000000" });
            var malformed = await client.PostAsJsonAsync("/api/auth/toggle-2fa", new { enable = true, password = "abc" });
            var good = await client.PostAsJsonAsync("/api/auth/toggle-2fa", new { enable = true, password = "123456" });
            var status = await JsonOf(await client.GetAsync("/api/auth/2fa-status"));

            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal("PinRequired", (await JsonOf(missing)).GetProperty("errorKey").GetString());
            Assert.Equal("InvalidCredentials", (await JsonOf(wrong)).GetProperty("errorKey").GetString());
            Assert.Equal("ValidationError", (await JsonOf(malformed)).GetProperty("errorKey").GetString());
            Assert.Equal(HttpStatusCode.OK, good.StatusCode);
            Assert.True((await JsonOf(good)).GetProperty("enabled").GetBoolean());
            Assert.True(status.GetProperty("enabled").GetBoolean());
        }

        [Fact]
        public async Task The_two_factor_switch_is_closed_to_anonymous_callers()
        {
            using var client = _factory.CreateClient();

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/toggle-2fa", new { enable = true, password = "123456" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/2fa-status")).StatusCode);
        }

        // ---- signing in --------------------------------------------------------------------------------------

        [Fact]
        public async Task A_locked_account_gets_exactly_the_answer_of_a_wrong_pin_and_of_an_unknown_tckn()
        {
            var user = await _factory.RegisterCustomerAsync();
            using var client = _factory.CreateClient();

            string? lastWrong = null;
            for (var i = 0; i < 5; i++)
            {
                var attempt = await client.PostAsJsonAsync("/api/auth/login", new { tckn = user.Tckn, password = "000000" });
                lastWrong = await attempt.Content.ReadAsStringAsync();
            }

            var rightPinButLocked = await client.PostAsJsonAsync("/api/auth/login", new { tckn = user.Tckn, password = "123456" });
            var unknown = await client.PostAsJsonAsync("/api/auth/login", new { tckn = TestTckn.Next(), password = "123456" });

            Assert.Equal(HttpStatusCode.BadRequest, rightPinButLocked.StatusCode);
            Assert.Equal(await unknown.Content.ReadAsStringAsync(), await rightPinButLocked.Content.ReadAsStringAsync());
            Assert.Equal(lastWrong, await rightPinButLocked.Content.ReadAsStringAsync());
            Assert.Equal("InvalidCredentials", (await JsonOf(rightPinButLocked)).GetProperty("errorKey").GetString());
        }

        [Fact]
        public async Task A_name_that_would_not_fit_the_full_name_column_is_refused_cleanly()
        {
            using var client = _factory.CreateClient();

            var response = await client.PostAsJsonAsync("/api/auth/register", new
            {
                username = "longnames" + Guid.NewGuid().ToString("N")[..6],
                tckn = TestTckn.Next(),
                password = "123456",
                firstName = new string('A', 50),
                lastName = new string('B', 50),
                email = $"long{Guid.NewGuid():N}@example.com"[..30] + ".com"
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("ValidationError", (await JsonOf(response)).GetProperty("errorKey").GetString());
        }

        [Fact]
        public async Task Neither_the_token_nor_the_token_response_carries_the_tckn()
        {
            var user = await _factory.RegisterCustomerAsync();
            using var client = _factory.CreateClient();

            var login = await JsonOf(await client.PostAsJsonAsync("/api/auth/login", new { tckn = user.Tckn, password = "123456" }));

            Assert.False(login.TryGetProperty("tckn", out _));
            var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(login.GetProperty("token").GetString());
            Assert.DoesNotContain(jwt.Claims, c => c.Value == user.Tckn);
        }

        // ---- the public market list --------------------------------------------------------------------------

        [Fact]
        public async Task The_public_rate_list_flags_stand_in_prices()
        {
            using var client = _factory.CreateClient();
            try
            {
                var live = await JsonOf(await client.GetAsync("/api/market/rates"));
                Assert.All(live.EnumerateArray(), r => Assert.False(r.GetProperty("isFallback").GetBoolean()));

                _factory.Rates.Fallback = true;
                var standIn = await JsonOf(await client.GetAsync("/api/market/rates"));
                Assert.All(standIn.EnumerateArray(), r => Assert.True(r.GetProperty("isFallback").GetBoolean()));
            }
            finally
            {
                _factory.Rates.Fallback = false;
            }
        }

        // ---- the support dashboard ---------------------------------------------------------------------------

        [Fact]
        public async Task The_agent_metrics_say_null_for_what_there_is_no_data_for()
        {
            var agent = await _factory.RegisterAgentAsync();
            using var client = _factory.ClientFor(agent);

            var metrics = await JsonOf(await client.GetAsync("/api/chat/agent-metrics"));

            Assert.Equal(JsonValueKind.Null, metrics.GetProperty("csatScore").ValueKind);
            Assert.True(metrics.TryGetProperty("avgResponseTime", out _));
            Assert.True(metrics.TryGetProperty("resolvedCount", out _));
        }

        [Fact]
        public async Task A_hand_over_keeps_the_title_validates_the_department_and_refuses_a_closed_conversation()
        {
            var customer = await _factory.RegisterCustomerAsync();
            var agent = await _factory.RegisterAgentAsync();
            var session = await _factory.CreateChatSessionAsync(customer, "hello");
            using var client = _factory.ClientFor(agent);

            var bad = await client.PostAsJsonAsync($"/api/chat/transfer-session/{session}", new { department = "Cards] [CONFIRM_TRANSFER: x" });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Equal("InvalidDepartment", (await JsonOf(bad)).GetProperty("errorKey").GetString());

            var empty = await client.PostAsJsonAsync($"/api/chat/transfer-session/{session}", new { department = "" });
            Assert.Equal("ValidationError", (await JsonOf(empty)).GetProperty("errorKey").GetString());

            var ok = await client.PostAsJsonAsync($"/api/chat/transfer-session/{session}", new { department = "Cards" });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

            using (var scope = _factory.Services.CreateScope())
            {
                var queue = (await scope.ServiceProvider.GetRequiredService<IChatService>().GetActiveSessionsAsync()).Data!;
                Assert.Equal("Question about my card", queue.Single(s => s.Id == session).Title);
                var messages = (await scope.ServiceProvider.GetRequiredService<IChatService>().GetSessionMessagesForAgentAsync(session)).Data!;
                Assert.Contains(messages, m => m.Sender == "System" && m.Content == "[SESSION_TRANSFERRED: to=Cards]");

                await scope.ServiceProvider.GetRequiredService<IChatService>().CloseSessionAsync(session);
            }

            var closed = await client.PostAsJsonAsync($"/api/chat/transfer-session/{session}", new { department = "Cards" });
            Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
            Assert.Equal("SessionClosed", (await JsonOf(closed)).GetProperty("errorKey").GetString());

            var missing = await client.PostAsJsonAsync($"/api/chat/transfer-session/{Guid.NewGuid()}", new { department = "Cards" });
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        [Fact]
        public async Task The_co_pilot_suggestion_comes_from_the_ai_and_never_contains_an_action_tag()
        {
            var customer = await _factory.RegisterCustomerAsync();
            var agent = await _factory.RegisterAgentAsync();
            var session = await _factory.CreateChatSessionAsync(customer, "I lost my card");
            using var client = _factory.ClientFor(agent);
            _factory.Ai.Reply = _ => "Please block the card [ACTION:GET_BALANCES] now.";

            try
            {
                var suggestion = await JsonOf(await client.GetAsync($"/api/chat/suggest-response/{session}"));

                Assert.Equal("Please block the card  now.", suggestion.GetProperty("suggestion").GetString());
                var asked = _factory.Ai.CallsSnapshot().Last();
                Assert.Contains(asked, m => m.Content.Contains("I lost my card"));
                Assert.Contains("Co-Pilot", asked[^1].Content);
            }
            finally
            {
                _factory.Ai.Reply = _ => "Fake AI reply.";
            }
        }

        // ---- switches, health, CORS, OpenAPI ----------------------------------------------------------------

        [Fact]
        public async Task The_simulation_endpoints_work_by_default()
        {
            var user = await _factory.RegisterCustomerAsync();
            var card = await _factory.GetCreditCardIdAsync(user);
            using var client = _factory.ClientFor(user);

            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/banking/credit-cards/{card}/charge?amount=10", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/banking/credit-cards/{card}/advance-period", null)).StatusCode);
        }

        [Fact]
        public async Task The_health_endpoints_answer_and_say_nothing_else()
        {
            using var client = _factory.CreateClient();

            var live = await client.GetAsync("/health");
            var ready = await client.GetAsync("/health/ready");

            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task The_api_description_is_not_published_outside_development()
        {
            using var client = _factory.CreateClient();

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        }

        [Fact]
        public async Task A_preflight_from_the_configured_web_app_is_answered_with_its_origin_and_credentials()
        {
            using var client = _factory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Options, "/api/banking/accounts");
            request.Headers.Add("Origin", "https://alonessam.github.io");
            request.Headers.Add("Access-Control-Request-Method", "GET");
            request.Headers.Add("Access-Control-Request-Headers", "authorization");

            var response = await client.SendAsync(request);

            Assert.True(response.IsSuccessStatusCode);
            Assert.Equal("https://alonessam.github.io", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
            Assert.Contains("authorization", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("https://evil.example")]
        [InlineData("http://localhost:3000")]  // local pages are allowed in Development only
        [InlineData("null")]
        public async Task A_preflight_from_any_other_origin_gets_no_permission(string origin)
        {
            using var client = _factory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Options, "/api/banking/accounts");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "GET");

            var response = await client.SendAsync(request);

            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }

        [Fact]
        public async Task A_real_request_from_a_foreign_origin_gets_no_cors_headers()
        {
            var user = await _factory.RegisterCustomerAsync();
            using var client = _factory.ClientFor(user);
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/banking/accounts");
            request.Headers.Add("Origin", "https://evil.example");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);

            var response = await client.SendAsync(request);

            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }

        [Fact]
        public async Task Nothing_in_the_test_application_can_reach_the_network_for_ai_or_rates()
        {
            using var scope = _factory.Services.CreateScope();

            Assert.IsType<FakeAiChatbot>(scope.ServiceProvider.GetRequiredService<IAIChatbotService>());
            Assert.IsType<FakeMarketRates>(scope.ServiceProvider.GetRequiredService<IMarketRateService>());
            Assert.IsType<FakeRagService>(scope.ServiceProvider.GetRequiredService<IRAGService>());
            Assert.Null(scope.ServiceProvider.GetService<SmartBank.Infrastructure.Services.OllamaService>());
            Assert.Null(scope.ServiceProvider.GetService<SmartBank.Infrastructure.Services.GeminiService>());
            await Task.CompletedTask;
        }
    }

    [Collection("EncryptionHelper")]
    public class SimulationSwitchTests : IClassFixture<SimulationOffApiFactory>
    {
        private readonly SimulationOffApiFactory _factory;

        public SimulationSwitchTests(SimulationOffApiFactory factory) => _factory = factory;

        [Fact]
        public async Task With_the_switch_off_the_charge_and_advance_endpoints_are_404_but_everything_else_works()
        {
            var user = await _factory.RegisterCustomerAsync();
            var card = await _factory.GetCreditCardIdAsync(user);
            var account = await _factory.GetFirstAccountAsync(user);
            using var client = _factory.ClientFor(user);

            var charge = await client.PostAsync($"/api/banking/credit-cards/{card}/charge?amount=10", null);
            var advance = await client.PostAsync($"/api/banking/credit-cards/{card}/advance-period", null);
            Assert.Equal(HttpStatusCode.NotFound, charge.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, advance.StatusCode);
            foreach (var response in new[] { charge, advance })
            {
                using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.False(body.RootElement.GetProperty("isSuccess").GetBoolean());
                Assert.Equal("SimulationDisabled", body.RootElement.GetProperty("errorKey").GetString());
            }

            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/banking/deposit", new { accountNumber = account.AccountNumber, amount = 5m })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/banking/credit-cards/{card}/statements")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/banking/credit-cards/{card}/pay", new { sourceAccountNumber = account.AccountNumber, amount = 10m })).StatusCode);

            using var scope = _factory.Services.CreateScope();
            var debt = (await scope.ServiceProvider.GetRequiredService<SmartBankDbContext>().CreditCards.AsNoTracking().SingleAsync(c => c.Id == card)).CurrentDebt;
            Assert.Equal(1240m, debt); // the refused charge changed nothing
        }
    }

    [Collection("EncryptionHelper")]
    public class BankingRateLimitTests : IClassFixture<BankingLimitedApiFactory>
    {
        private readonly BankingLimitedApiFactory _factory;

        public BankingRateLimitTests(BankingLimitedApiFactory factory) => _factory = factory;

        [Fact]
        public async Task The_public_rate_list_is_limited_per_address_with_a_retry_after_and_the_usual_body()
        {
            using var client = _factory.CreateClient();

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/market/rates")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/market/rates")).StatusCode);
            var limited = await client.GetAsync("/api/market/rates");

            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero, "Retry-After must say how long to wait.");
            using var body = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("isSuccess").GetBoolean());
            Assert.Equal("TooManyRequests", body.RootElement.GetProperty("errorKey").GetString());
        }

        [Fact]
        public async Task Money_moving_endpoints_have_their_own_stricter_limit_per_user()
        {
            var busy = await _factory.RegisterCustomerAsync();
            var calm = await _factory.RegisterCustomerAsync();
            var busyAccount = (await _factory.GetFirstAccountAsync(busy)).AccountNumber;
            var calmAccount = (await _factory.GetFirstAccountAsync(calm)).AccountNumber;
            using var busyClient = _factory.ClientFor(busy);
            using var calmClient = _factory.ClientFor(calm);

            Assert.Equal(HttpStatusCode.OK, (await busyClient.PostAsJsonAsync("/api/banking/deposit", new { accountNumber = busyAccount, amount = 1m })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await busyClient.PostAsJsonAsync("/api/banking/deposit", new { accountNumber = busyAccount, amount = 1m })).StatusCode);
            var third = await busyClient.PostAsJsonAsync("/api/banking/deposit", new { accountNumber = busyAccount, amount = 1m });

            Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
            Assert.NotNull(third.Headers.RetryAfter);
            // The ordinary banking limit is separate and still open for the same user, and nobody else is affected.
            Assert.Equal(HttpStatusCode.OK, (await busyClient.GetAsync("/api/banking/accounts")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await calmClient.PostAsJsonAsync("/api/banking/deposit", new { accountNumber = calmAccount, amount = 1m })).StatusCode);
        }

        [Fact]
        public async Task The_ordinary_banking_limit_counts_every_request_of_the_user()
        {
            var user = await _factory.RegisterCustomerAsync();
            using var client = _factory.ClientFor(user);

            for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/banking/accounts")).StatusCode);
            var sixth = await client.GetAsync("/api/banking/standing-orders");

            Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        }
    }

    [Collection("EncryptionHelper")]
    public class AuthRateLimitTests : IClassFixture<AuthLimitedApiFactory>
    {
        private readonly AuthLimitedApiFactory _factory;

        public AuthRateLimitTests(AuthLimitedApiFactory factory) => _factory = factory;

        [Fact]
        public async Task The_auth_endpoints_are_limited_per_address()
        {
            using var client = _factory.CreateClient();
            var bad = new { tckn = TestTckn.Next(), password = "123456" };

            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/login", bad)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/login", bad)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/login", bad)).StatusCode);
            var limited = await client.PostAsJsonAsync("/api/auth/login", bad);

            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.NotNull(limited.Headers.RetryAfter);
            // forgot-password shares the same window
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/auth/forgot-password", new { tckn = TestTckn.Next() })).StatusCode);
        }

        [Fact]
        public async Task Refresh_and_logout_have_their_own_wider_window()
        {
            using var client = _factory.CreateClient();
            var token = new { refreshToken = new string('x', 30) };

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", token)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", token)).StatusCode);
            var limited = await client.PostAsJsonAsync("/api/auth/refresh", token);

            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.NotNull(limited.Headers.RetryAfter);
        }
    }
}
