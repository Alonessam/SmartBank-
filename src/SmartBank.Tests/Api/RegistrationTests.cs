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

        [Fact]
        public async Task A_text_with_the_nul_character_is_a_clean_400_on_every_database()
        {
            using var client = _factory.CreateClient();
            var body = new StringContent("{\"username\":\"nu\u0000ll\",\"tckn\":\"" + TestTckn.Next() + "\",\"password\":\"123456\",\"firstName\":\"Test\",\"lastName\":\"User\",\"email\":\"nul@example.com\"}", System.Text.Encoding.UTF8, "application/json");

            var response = await client.PostAsync("/api/auth/register", body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("ValidationError", await ErrorKeyAsync(response));
        }

        [Fact]
        public async Task A_body_that_is_not_the_expected_shape_does_not_name_internal_types()
        {
            using var client = _factory.CreateClient();

            var response = await client.PostAsync("/api/auth/login", new StringContent("[1,2,3]", System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("SmartBank.Core", text);
            Assert.DoesNotContain("LineNumber", text);
        }

        [Fact]
        public async Task A_pin_written_with_non_ascii_digits_is_refused()
        {
            using var client = _factory.CreateClient();

            var response = await client.PostAsJsonAsync("/api/auth/login", new { tckn = TestTckn.Next(), password = "١٢٣٤٥٦" }); // Arabic-Indic 123456

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("ValidationError", await ErrorKeyAsync(response));
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
