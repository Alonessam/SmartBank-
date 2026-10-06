using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SmartBank.Tests.Api
{
    [Collection("EncryptionHelper")]
    public class RegistrationTests : IClassFixture<ApiFactory>
    {
        private readonly ApiFactory _factory;

        public RegistrationTests(ApiFactory factory) => _factory = factory;

        private static object Payload(string username, string tckn, string email) => new
        {
            username,
            tckn,
            password = "123456",
            firstName = "Test",
            lastName = "User",
            email
        };

        private static async Task<string?> ErrorKeyAsync(HttpResponseMessage response)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.TryGetProperty("errorKey", out var key) ? key.GetString() : null;
        }

        [Fact]
        public async Task A_number_with_wrong_check_digits_is_refused_with_a_clear_message()
        {
            using var client = _factory.CreateClient();

            var response = await client.PostAsJsonAsync("/api/auth/register", Payload("badtckn" + Guid.NewGuid().ToString("N")[..6], "12345678901", "bad@example.com"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Contains("check digits", text);
            Assert.Contains("ValidationError", text);
        }

        [Theory]
        [InlineData("reg.same@example.com", "REG.SAME@example.com")]
        [InlineData("reg.exact@example.com", "reg.exact@example.com")]
        [InlineData("reg.spaced@example.com", " reg.spaced@example.com ")]
        public async Task Registering_an_e_mail_address_twice_is_a_clean_400_not_a_500(string first, string second)
        {
            using var client = _factory.CreateClient();
            var tag = Guid.NewGuid().ToString("N")[..6];

            var one = await client.PostAsJsonAsync("/api/auth/register", Payload("first" + tag, TestTckn.Next(), first));
            Assert.Equal(HttpStatusCode.OK, one.StatusCode);

            var two = await client.PostAsJsonAsync("/api/auth/register", Payload("second" + tag, TestTckn.Next(), second));

            Assert.Equal(HttpStatusCode.BadRequest, two.StatusCode);
            Assert.Equal("EmailAlreadyExists", await ErrorKeyAsync(two));
        }
    }
}
