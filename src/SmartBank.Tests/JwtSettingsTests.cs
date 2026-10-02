using Microsoft.Extensions.Configuration;
using SmartBank.Infrastructure.Security;

namespace SmartBank.Tests
{
    public class JwtSettingsTests
    {
        private static IConfiguration Config(params (string Key, string? Value)[] values) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
                .Build();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void From_Throws_When_Key_Is_Missing(string? key)
        {
            var config = Config(("JwtSettings:Key", key));

            var ex = Assert.Throws<InvalidOperationException>(() => JwtSettings.From(config));
            Assert.Contains("JwtSettings:Key", ex.Message);
        }

        [Fact]
        public void From_Throws_When_Key_Is_Not_Configured_At_All()
        {
            Assert.Throws<InvalidOperationException>(() => JwtSettings.From(Config()));
        }

        [Fact]
        public void From_Throws_When_Key_Is_Shorter_Than_256_Bits()
        {
            var config = Config(("JwtSettings:Key", new string('a', JwtSettings.MinKeyBytes - 1)));

            var ex = Assert.Throws<InvalidOperationException>(() => JwtSettings.From(config));
            Assert.Contains("too short", ex.Message);
        }

        [Fact]
        public void From_Uses_Defaults_For_Issuer_And_Audience()
        {
            var config = Config(("JwtSettings:Key", new string('a', JwtSettings.MinKeyBytes)));

            var settings = JwtSettings.From(config);

            Assert.Equal("SmartBankAPI", settings.Issuer);
            Assert.Equal("SmartBankApp", settings.Audience);
            Assert.Equal(JwtSettings.MinKeyBytes, settings.KeyBytes.Length);
        }

        [Fact]
        public void From_Reads_Custom_Issuer_And_Audience()
        {
            var config = Config(
                ("JwtSettings:Key", new string('a', JwtSettings.MinKeyBytes)),
                ("JwtSettings:Issuer", "my-issuer"),
                ("JwtSettings:Audience", "my-audience"));

            var settings = JwtSettings.From(config);

            Assert.Equal("my-issuer", settings.Issuer);
            Assert.Equal("my-audience", settings.Audience);
        }
    }
}
