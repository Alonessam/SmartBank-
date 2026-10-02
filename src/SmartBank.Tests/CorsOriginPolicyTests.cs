using SmartBank.API.Security;

namespace SmartBank.Tests
{
    public class CorsOriginPolicyTests
    {
        private static readonly string[] Allowed = { "https://alonessam.github.io" };

        [Fact]
        public void A_listed_origin_is_allowed_in_every_environment()
        {
            Assert.True(CorsOriginPolicy.IsAllowed("https://alonessam.github.io", Allowed, isDevelopment: false));
            Assert.True(CorsOriginPolicy.IsAllowed("https://alonessam.github.io", Allowed, isDevelopment: true));
        }

        [Fact]
        public void The_comparison_ignores_case()
        {
            Assert.True(CorsOriginPolicy.IsAllowed("https://AlonesSam.GitHub.io", Allowed, isDevelopment: false));
        }

        [Theory]
        [InlineData("https://evil.example")]
        [InlineData("https://alonessam.github.io.evil.example")] // looks like the allowed host but is not
        [InlineData("http://alonessam.github.io")]                // same host, wrong scheme
        [InlineData("https://alonessam.github.io:8443")]          // same host, other port
        [InlineData("https://sub.alonessam.github.io")]
        public void Any_other_origin_is_rejected_even_with_credentials_in_play(string origin)
        {
            Assert.False(CorsOriginPolicy.IsAllowed(origin, Allowed, isDevelopment: false));
            Assert.False(CorsOriginPolicy.IsAllowed(origin, Allowed, isDevelopment: true));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_missing_origin_is_rejected(string? origin)
        {
            Assert.False(CorsOriginPolicy.IsAllowed(origin, Allowed, isDevelopment: true));
        }

        [Theory]
        [InlineData("null")]                      // a page opened from disk (file://)
        [InlineData("http://localhost:5500")]
        [InlineData("http://127.0.0.1:5500")]
        [InlineData("https://localhost:7200")]
        public void Local_pages_are_allowed_only_in_development(string origin)
        {
            Assert.True(CorsOriginPolicy.IsAllowed(origin, Allowed, isDevelopment: true));
            Assert.False(CorsOriginPolicy.IsAllowed(origin, Allowed, isDevelopment: false));
        }

        [Theory]
        [InlineData("http://localhost.evil.example")]
        [InlineData("ftp://localhost")]
        [InlineData("not a url")]
        public void Lookalike_or_malformed_origins_are_rejected_even_in_development(string origin)
        {
            Assert.False(CorsOriginPolicy.IsAllowed(origin, Allowed, isDevelopment: true));
        }

        [Fact]
        public void With_nothing_configured_nothing_is_allowed_outside_development()
        {
            Assert.False(CorsOriginPolicy.IsAllowed("https://alonessam.github.io", Array.Empty<string>(), isDevelopment: false));
        }
    }
}
