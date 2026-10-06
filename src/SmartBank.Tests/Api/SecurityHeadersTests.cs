using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SmartBank.Tests.Api
{
    [Collection("EncryptionHelper")]
    public class SecurityHeadersTests : IClassFixture<ApiFactory>
    {
        private readonly ApiFactory _factory;

        public SecurityHeadersTests(ApiFactory factory) => _factory = factory;

        private static string Header(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out var values) ? string.Join(",", values)
            : response.Content.Headers.TryGetValues(name, out var contentValues) ? string.Join(",", contentValues)
            : string.Empty;

        [Theory]
        [InlineData("/health", HttpStatusCode.OK)]
        [InlineData("/api/banking/accounts", HttpStatusCode.Unauthorized)] // an error response gets them too
        [InlineData("/api/does-not-exist", HttpStatusCode.NotFound)]
        public async Task Every_response_carries_the_browser_hardening_headers(string path, HttpStatusCode expected)
        {
            using var client = _factory.CreateClient();

            var response = await client.GetAsync(path);

            Assert.Equal(expected, response.StatusCode);
            Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
            Assert.Equal("DENY", Header(response, "X-Frame-Options"));
            Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
            Assert.Contains("frame-ancestors 'none'", Header(response, "Content-Security-Policy"));
            Assert.Contains("default-src 'none'", Header(response, "Content-Security-Policy"));
        }

        [Fact]
        public async Task Api_responses_are_not_cacheable_so_balances_stay_out_of_browser_and_proxy_caches()
        {
            var customer = await _factory.RegisterCustomerAsync();
            using var client = _factory.ClientFor(customer);

            var response = await client.GetAsync("/api/banking/accounts");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", Header(response, "Cache-Control"));
        }

        [Fact]
        public async Task Hsts_is_sent_over_https_but_not_over_plain_http()
        {
            using var plain = _factory.CreateClient();
            using var secure = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://api.example.test") });

            var overHttp = await plain.GetAsync("/health");
            var overHttps = await secure.GetAsync("/health");

            // (ASP.NET never sends HSTS for localhost, so the HTTPS client uses another host name.)
            Assert.Equal(string.Empty, Header(overHttp, "Strict-Transport-Security"));
            var hsts = Header(overHttps, "Strict-Transport-Security");
            Assert.Contains("max-age=15552000", hsts); // 180 days
            Assert.DoesNotContain("includeSubDomains", hsts);
        }
    }
}
