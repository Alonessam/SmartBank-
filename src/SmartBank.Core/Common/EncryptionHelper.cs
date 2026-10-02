using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SmartBank.Core.Common
{
    public static class EncryptionHelper
    {
        private const int KeyBytes = 32;

        private static byte[]? _key;
        private static byte[]? _iv;

        private static byte[] Key => _key ?? throw NotConfigured();
        private static byte[] Iv => _iv ?? throw NotConfigured();

        /// <summary>
        /// Sets the AES-256 key. Must be called once at startup with a base64 string of exactly 32 bytes
        /// coming from user-secrets or the Encryption__Key environment variable.
        /// </summary>
        public static void Configure(string? base64Key)
        {
            if (string.IsNullOrWhiteSpace(base64Key))
            {
                throw new InvalidOperationException(
                    "Encryption:Key is not configured. Set it with user-secrets " +
                    "(scripts/dev-secrets.ps1) or the Encryption__Key environment variable.");
            }

            byte[] key;
            try
            {
                key = Convert.FromBase64String(base64Key);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException("Encryption:Key must be a base64 string.");
            }

            if (key.Length != KeyBytes)
            {
                throw new InvalidOperationException($"Encryption:Key must decode to exactly {KeyBytes} bytes.");
            }

            _key = key;

            // Transitional: the IV is derived from the key so no constant lives in the source.
            // It is still deterministic (same plaintext -> same ciphertext); the card-data rework replaces
            // this whole construct with AES-GCM and a random nonce per message.
            _iv = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("smartbank:aes-iv:v1"))[..16];
        }

        private static InvalidOperationException NotConfigured() =>
            new("EncryptionHelper is not configured. Call EncryptionHelper.Configure(...) at startup.");

        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;

            using (var aes = Aes.Create())
            {
                aes.Key = Key;
                aes.IV = Iv;

                using (var encryptor = aes.CreateEncryptor(aes.Key, aes.IV))
                using (var ms = new MemoryStream())
                {
                    using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                    using (var sw = new StreamWriter(cs))
                    {
                        sw.Write(plainText);
                    }
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;

            try
            {
                using (var aes = Aes.Create())
                {
                    aes.Key = Key;
                    aes.IV = Iv;

                    using (var decryptor = aes.CreateDecryptor(aes.Key, aes.IV))
                    using (var ms = new MemoryStream(Convert.FromBase64String(cipherText)))
                    using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                    using (var sr = new StreamReader(cs))
                    {
                        return sr.ReadToEnd();
                    }
                }
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return "[Decryption Error]";
            }
        }
    }
}
