using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using SmartBank.Core.Entities;

namespace SmartBank.Tests.Api
{
    // The factory configures the static EncryptionHelper when the application starts, so these tests share the collection
    // with the other tests that touch it and never run alongside them.
    [Collection("EncryptionHelper")]
    public class RoleTests : IClassFixture<ApiFactory>
    {
        private readonly ApiFactory _factory;

        public RoleTests(ApiFactory factory) => _factory = factory;

        private static string RoleClaim(string token) =>
            new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.Single(c => c.Type is "role" or System.Security.Claims.ClaimTypes.Role).Value;

        [Theory]
        [InlineData("agent")]
        [InlineData("Agent_Smith")]
        [InlineData("support_agent_1")]
        [InlineData("AGENT007")]
        public async Task A_username_containing_agent_does_not_make_anyone_a_support_agent(string username)
        {
            var user = await _factory.RegisterCustomerAsync(username);

            Assert.Equal(RoleNames.Customer, user.Role);
            Assert.Equal(RoleNames.Customer, RoleClaim(user.Token));
        }

        [Fact]
        public async Task A_promoted_account_gets_the_agent_role_in_its_token_after_signing_in_again()
        {
            var agent = await _factory.RegisterAgentAsync();

            Assert.Equal(RoleNames.Agent, agent.Role);
            Assert.Equal(RoleNames.Agent, RoleClaim(agent.Token));
        }

        [Fact]
        public async Task A_token_issued_before_the_promotion_does_not_carry_the_role()
        {
            // Roles live in the token, so a promotion only takes effect at the next sign-in.
            var customer = await _factory.RegisterCustomerAsync("soon_to_be_staff");

            Assert.Equal(RoleNames.Customer, RoleClaim(customer.Token));
        }

        [Fact]
        public async Task The_registration_request_has_no_way_to_ask_for_a_role()
        {
            using var client = _factory.CreateClient();

            var response = await client.PostAsJsonAsync("/api/auth/register", new
            {
                username = "sneaky_one",
                tckn = "77700000001",
                password = "123456",
                firstName = "Sneaky",
                lastName = "Person",
                email = "sneaky@example.com",
                role = "Agent",      // ignored: not part of the request model
                isAgent = true
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object?>>();
            Assert.Equal(RoleNames.Customer, body!["role"]?.ToString());
        }
    }
}
