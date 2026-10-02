using System.Security.Cryptography;
using SmartBank.Core.Common;

namespace SmartBank.Tests
{
    // EncryptionHelper holds static state, so these tests must not run in parallel with each other.
    [Collection("EncryptionHelper")]
    public class EncryptionHelperTests
    {
        private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Configure_Throws_When_Key_Is_Missing(string? key)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => EncryptionHelper.Configure(key));
            Assert.Contains("Encryption:Key", ex.Message);
        }

        [Fact]
        public void Configure_Throws_When_Key_Is_Not_Base64()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => EncryptionHelper.Configure("this is not base64!!"));
            Assert.Contains("base64", ex.Message);
        }

        [Theory]
        [InlineData(16)]
        [InlineData(24)]
        [InlineData(33)]
        public void Configure_Throws_When_Key_Is_Not_32_Bytes(int byteCount)
        {
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount));

            var ex = Assert.Throws<InvalidOperationException>(() => EncryptionHelper.Configure(key));
            Assert.Contains("32 bytes", ex.Message);
        }

        [Fact]
        public void Encrypt_Then_Decrypt_Returns_Original_Text()
        {
            EncryptionHelper.Configure(NewKey());

            var cipher = EncryptionHelper.Encrypt("4111111111111111");

            Assert.NotEqual("4111111111111111", cipher);
            Assert.Equal("4111111111111111", EncryptionHelper.Decrypt(cipher));
        }

        [Fact]
        public void Decrypt_With_A_Different_Key_Does_Not_Return_The_Plaintext()
        {
            EncryptionHelper.Configure(NewKey());
            var cipher = EncryptionHelper.Encrypt("4111111111111111");

            EncryptionHelper.Configure(NewKey());

            Assert.NotEqual("4111111111111111", EncryptionHelper.Decrypt(cipher));
        }
    }
}
