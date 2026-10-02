using System.Security.Cryptography;
using SmartBank.Core.Common;

namespace SmartBank.Tests
{
    // EncryptionHelper holds static state, so every test that uses it shares this collection and runs serially.
    [Collection("EncryptionHelper")]
    public class EncryptionHelperTests
    {
        private const string Pan = "4111111111111111";

        private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        // ---- Configure ---------------------------------------------------------------------------------------

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

        // ---- Encrypt / Decrypt -------------------------------------------------------------------------------

        [Fact]
        public void Encrypt_Then_Decrypt_Returns_Original_Text()
        {
            EncryptionHelper.Configure(NewKey());

            var cipher = EncryptionHelper.Encrypt(Pan);

            Assert.DoesNotContain(Pan, cipher);
            Assert.Equal(Pan, EncryptionHelper.Decrypt(cipher));
        }

        [Fact]
        public void Encrypt_Round_Trips_Non_Ascii_Text()
        {
            EncryptionHelper.Configure(NewKey());

            const string text = "Şükrü Çağlayan — 😀";

            Assert.Equal(text, EncryptionHelper.Decrypt(EncryptionHelper.Encrypt(text)));
        }

        [Fact]
        public void Encrypting_The_Same_Text_Twice_Gives_Different_Ciphertext()
        {
            EncryptionHelper.Configure(NewKey());

            var first = EncryptionHelper.Encrypt(Pan);
            var second = EncryptionHelper.Encrypt(Pan);

            Assert.NotEqual(first, second);
            Assert.Equal(Pan, EncryptionHelper.Decrypt(first));
            Assert.Equal(Pan, EncryptionHelper.Decrypt(second));
        }

        [Fact]
        public void Ciphertext_Carries_A_Version_Prefix()
        {
            EncryptionHelper.Configure(NewKey());

            Assert.StartsWith("v1:", EncryptionHelper.Encrypt(Pan));
        }

        [Fact]
        public void Empty_Input_Returns_Empty_Output()
        {
            EncryptionHelper.Configure(NewKey());

            Assert.Equal(string.Empty, EncryptionHelper.Encrypt(string.Empty));
            Assert.Equal(string.Empty, EncryptionHelper.Decrypt(string.Empty));
        }

        [Fact]
        public void Decrypt_Throws_When_The_Ciphertext_Was_Tampered_With()
        {
            EncryptionHelper.Configure(NewKey());
            var cipher = EncryptionHelper.Encrypt(Pan);

            // Flip one bit inside the encrypted payload.
            var payload = Convert.FromBase64String(cipher["v1:".Length..]);
            payload[^1] ^= 0x01;
            var tampered = "v1:" + Convert.ToBase64String(payload);

            Assert.ThrowsAny<CryptographicException>(() => EncryptionHelper.Decrypt(tampered));
        }

        [Fact]
        public void Decrypt_Throws_When_The_Key_Is_Different()
        {
            EncryptionHelper.Configure(NewKey());
            var cipher = EncryptionHelper.Encrypt(Pan);

            EncryptionHelper.Configure(NewKey());

            Assert.ThrowsAny<CryptographicException>(() => EncryptionHelper.Decrypt(cipher));
        }

        [Theory]
        [InlineData("not-versioned-at-all")]
        [InlineData("v1:%%%not base64%%%")]
        [InlineData("v1:AAAA")] // valid base64, shorter than nonce + tag
        public void Decrypt_Throws_For_Malformed_Input(string garbage)
        {
            EncryptionHelper.Configure(NewKey());

            Assert.ThrowsAny<CryptographicException>(() => EncryptionHelper.Decrypt(garbage));
        }

        [Fact]
        public void TryDecrypt_Returns_False_Instead_Of_Throwing()
        {
            EncryptionHelper.Configure(NewKey());

            Assert.False(EncryptionHelper.TryDecrypt("legacy-ciphertext", out var plain));
            Assert.Equal(string.Empty, plain);
        }

        [Fact]
        public void TryDecrypt_Succeeds_For_Valid_Input_And_For_Null()
        {
            EncryptionHelper.Configure(NewKey());

            Assert.True(EncryptionHelper.TryDecrypt(EncryptionHelper.Encrypt(Pan), out var plain));
            Assert.Equal(Pan, plain);

            Assert.True(EncryptionHelper.TryDecrypt(null, out var empty));
            Assert.Equal(string.Empty, empty);
        }

        [Fact]
        public void Ciphertext_Fits_The_Database_Columns()
        {
            EncryptionHelper.Configure(NewKey());

            // SmartBankDbContext: EncryptedCardNumber is HasMaxLength(100). Guards against a format change
            // that silently overflows the column.
            Assert.True(EncryptionHelper.Encrypt(Pan).Length <= 100);
        }

        // ---- HashCardNumber ----------------------------------------------------------------------------------

        [Fact]
        public void Hash_Is_Deterministic_And_64_Hex_Characters()
        {
            EncryptionHelper.Configure(NewKey());

            var first = EncryptionHelper.HashCardNumber(Pan);
            var second = EncryptionHelper.HashCardNumber(Pan);

            Assert.Equal(first, second);
            Assert.Equal(64, first.Length);
            Assert.Matches("^[0-9a-f]{64}$", first);
        }

        [Fact]
        public void Hash_Ignores_Spaces_And_Dashes()
        {
            EncryptionHelper.Configure(NewKey());

            Assert.Equal(
                EncryptionHelper.HashCardNumber(Pan),
                EncryptionHelper.HashCardNumber("4111 1111 1111 1111"));
            Assert.Equal(
                EncryptionHelper.HashCardNumber(Pan),
                EncryptionHelper.HashCardNumber("4111-1111-1111-1111"));
        }

        [Fact]
        public void Hash_Differs_For_Different_Card_Numbers()
        {
            EncryptionHelper.Configure(NewKey());

            Assert.NotEqual(
                EncryptionHelper.HashCardNumber("4111111111111111"),
                EncryptionHelper.HashCardNumber("4111111111111112"));
        }

        [Fact]
        public void Hash_Depends_On_The_Secret_Key()
        {
            EncryptionHelper.Configure(NewKey());
            var hashWithFirstKey = EncryptionHelper.HashCardNumber(Pan);

            EncryptionHelper.Configure(NewKey());

            Assert.NotEqual(hashWithFirstKey, EncryptionHelper.HashCardNumber(Pan));
        }
    }
}
