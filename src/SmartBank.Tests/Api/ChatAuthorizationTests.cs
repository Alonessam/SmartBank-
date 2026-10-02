using System.Net;
using System.Net.Http.Json;

namespace SmartBank.Tests.Api
{
    /// <summary>
    /// Who may touch the support chat over HTTP. These endpoints used to rely on "the username contains 'agent'" (and
    /// several had no check at all), so any customer could list every conversation and read other people's.
    /// </summary>
    [Collection("EncryptionHelper")]
    public class ChatAuthorizationTests : IClassFixture<ApiFactory>
    {
        private const string Secret = "my card number is 4111 and my PIN is 1234";

        private readonly ApiFactory _factory;

        public ChatAuthorizationTests(ApiFactory factory) => _factory = factory;

        public static IEnumerable<object[]> AgentOnlyEndpoints()
        {
            var someSession = Guid.NewGuid();
            yield return new object[] { "GET", "/api/chat/active-sessions" };
            yield return new object[] { "GET", "/api/chat/agent-metrics" };
            yield return new object[] { "GET", $"/api/chat/suggest-response/{someSession}" };
            yield return new object[] { "POST", $"/api/chat/transfer-session/{someSession}" };
        }

        private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url) =>
            method == "POST"
                ? await client.PostAsJsonAsync(url, new { department = "Cards" })
                : await client.GetAsync(url);

        [Theory]
        [MemberData(nameof(AgentOnlyEndpoints))]
        public async Task Anonymous_callers_are_turned_away_from_agent_endpoints(string method, string url)
        {
            using var client = _factory.ClientFor(null);

            var response = await SendAsync(client, method, url);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Theory]
        [MemberData(nameof(AgentOnlyEndpoints))]
        public async Task A_customer_is_forbidden_from_agent_endpoints(string method, string url)
        {
            var customer = await _factory.RegisterCustomerAsync();
            using var client = _factory.ClientFor(customer);

            var response = await SendAsync(client, method, url);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Theory]
        [MemberData(nameof(AgentOnlyEndpoints))]
        public async Task Calling_oneself_agent_in_the_username_changes_nothing(string method, string url)
        {
            var pretender = await _factory.RegisterCustomerAsync("agent_pretender_" + Guid.NewGuid().ToString("N")[..8]);
            using var client = _factory.ClientFor(pretender);

            var response = await SendAsync(client, method, url);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task A_customer_cannot_read_another_customers_conversation()
        {
            var victim = await _factory.RegisterCustomerAsync();
            var attacker = await _factory.RegisterCustomerAsync("agent_attacker_" + Guid.NewGuid().ToString("N")[..8]);
            var session = await _factory.CreateChatSessionAsync(victim, Secret);

            using var client = _factory.ClientFor(attacker);
            var response = await client.GetAsync($"/api/chat/messages/{session}");

            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("4111", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task A_customer_can_read_their_own_conversation()
        {
            var owner = await _factory.RegisterCustomerAsync();
            var session = await _factory.CreateChatSessionAsync(owner, Secret);

            using var client = _factory.ClientFor(owner);
            var response = await client.GetAsync($"/api/chat/messages/{session}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("4111", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task An_agent_can_see_the_queue_and_read_any_conversation()
        {
            var customer = await _factory.RegisterCustomerAsync();
            var agent = await _factory.RegisterAgentAsync();
            var session = await _factory.CreateChatSessionAsync(customer, Secret);

            using var client = _factory.ClientFor(agent);

            var queue = await client.GetAsync("/api/chat/active-sessions");
            Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
            Assert.Contains(session.ToString(), await queue.Content.ReadAsStringAsync());

            var messages = await client.GetAsync($"/api/chat/messages/{session}");
            Assert.Equal(HttpStatusCode.OK, messages.StatusCode);
            Assert.Contains("4111", await messages.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/chat/agent-metrics")).StatusCode);
        }

        [Theory]
        [InlineData("POST", "/api/chat/test-setup")]
        [InlineData("POST", "/api/chat/test-ai/00000000-0000-0000-0000-000000000001")]
        [InlineData("POST", "/api/chat/messages/test-send-message/00000000-0000-0000-0000-000000000001")]
        [InlineData("GET", "/api/chat/test-rag?query=anything")]
        public async Task The_developer_test_endpoints_are_gone(string method, string url)
        {
            // They were not used by the UI, and test-ai let any signed-in user spend the AI quota with arbitrary prompts.
            var customer = await _factory.RegisterCustomerAsync();
            using var client = _factory.ClientFor(customer);

            var response = method == "POST"
                ? await client.PostAsJsonAsync(url, "hello")
                : await client.GetAsync(url);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
