using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SmartBank.Tests.Api
{
    /// <summary>The sign-in / refresh / sign-out cycle over real HTTP, including a protected endpoint with the refreshed token.</summary>
    [Collection("EncryptionHelper")]
    public class SessionEndpointTests : IClassFixture<ApiFactory>
    {
        private readonly ApiFactory _factory;

        public SessionEndpointTests(ApiFactory factory) => _factory = factory;

        private sealed record Session(string Token, string RefreshToken, DateTime AccessTokenExpiresAt, string Username);

        private async Task<Session> RegisterAsync()
        {
            var tag = Guid.NewGuid().ToString("N")[..8];
            using var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/register", new
            {
                username = "sess" + tag,
                tckn = "5" + Math.Abs(tag.GetHashCode()).ToString("D10")[^10..],
                password = "123456",
                firstName = "Sess",
                lastName = "Ion",
                email = $"sess{tag}@example.com"
            });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<Session>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        }

        private async Task<HttpResponseMessage> RefreshAsync(string refreshToken)
        {
            using var client = _factory.CreateClient();
            return await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        }

        [Fact]
        public async Task Registration_returns_a_refresh_token_and_the_access_token_expires_in_minutes_not_days()
        {
            var session = await RegisterAsync();

            Assert.False(string.IsNullOrEmpty(session.RefreshToken));
            Assert.True(session.AccessTokenExpiresAt - DateTime.UtcNow < TimeSpan.FromMinutes(16));
        }

        [Fact]
        public async Task The_refreshed_access_token_works_on_a_protected_endpoint_and_the_old_refresh_token_does_not()
        {
            var session = await RegisterAsync();

            var refreshed = await RefreshAsync(session.RefreshToken);
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            var next = (await refreshed.Content.ReadFromJsonAsync<Session>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

            using var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", next.Token);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/banking/accounts")).StatusCode);

            Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(session.RefreshToken)).StatusCode);
        }

        [Fact]
        public async Task Refreshing_with_a_bad_token_is_401_with_an_error_key_the_client_can_act_on()
        {
            var response = await RefreshAsync("this-token-was-never-issued-by-anyone");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("InvalidRefreshToken", json.RootElement.GetProperty("errorKey").GetString());
        }

        [Fact]
        public async Task A_malformed_refresh_request_is_a_400()
        {
            using var client = _factory.CreateClient();

            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/refresh", new { })).StatusCode);
        }

        [Fact]
        public async Task Logout_is_204_and_ends_the_session_but_a_made_up_token_gets_the_same_answer()
        {
            var session = await RegisterAsync();
            using var client = _factory.CreateClient();

            var real = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = session.RefreshToken });
            var fake = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = "this-token-was-never-issued-by-anyone" });

            Assert.Equal(HttpStatusCode.NoContent, real.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, fake.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(session.RefreshToken)).StatusCode);
        }
    }
}
