using System;
using System.Security.Cryptography;
using System.Text;

namespace SmartBank.Core.Common
{
    /// <summary>
    /// Authenticated encryption for card data (AES-256-GCM) plus a keyed hash for duplicate detection.
    ///
    /// Stored format: "v1:" + base64(nonce[12] | tag[16] | ciphertext).
    /// - A fresh random nonce per message, so encrypting the same text twice gives different output.
    /// - GCM authenticates the data: a tampered value or a wrong key fails to decrypt instead of returning garbage.
    /// - The version prefix is also bound as associated data, so the format can change without ambiguity.
    ///
    /// One master key (Encryption:Key, base64, 32 bytes) is split with HKDF into independent keys for
    /// encryption and hashing, so a weakness in one use cannot leak the other.
    /// </summary>
    public static class EncryptionHelper
    {
        private const int MasterKeyBytes = 32;
        private const int NonceBytes = 12;
        private const int TagBytes = 16;
        private const string VersionPrefix = "v1:";

        private static byte[]? _encryptionKey;
        private static byte[]? _hashKey;

        private static byte[] EncryptionKey => _encryptionKey ?? throw NotConfigured();
        private static byte[] HashKey => _hashKey ?? throw NotConfigured();

        private static byte[] AssociatedData => Encoding.UTF8.GetBytes(VersionPrefix);

        /// <summary>
        /// Must be called once at startup with a base64 string of exactly 32 bytes coming from
        /// user-secrets or the Encryption__Key environment variable.
        /// </summary>
        public static void Configure(string? base64Key)
        {
            if (string.IsNullOrWhiteSpace(base64Key))
            {
                throw new InvalidOperationException(
                    "Encryption:Key is not configured. Set it with user-secrets " +
                    "(scripts/dev-secrets.ps1) or the Encryption__Key environment variable.");
            }

            byte[] master;
            try
            {
                master = Convert.FromBase64String(base64Key);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException("Encryption:Key must be a base64 string.");
            }

            if (master.Length != MasterKeyBytes)
            {
                throw new InvalidOperationException($"Encryption:Key must decode to exactly {MasterKeyBytes} bytes.");
            }

            _encryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.UTF8.GetBytes("smartbank:card-encryption:v1"));
            _hashKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.UTF8.GetBytes("smartbank:card-hash:v1"));
        }

        private static InvalidOperationException NotConfigured() =>
            new("EncryptionHelper is not configured. Call EncryptionHelper.Configure(...) at startup.");

        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;

            var plain = Encoding.UTF8.GetBytes(plainText);
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagBytes];

            using (var aes = new AesGcm(EncryptionKey, TagBytes))
            {
                aes.Encrypt(nonce, plain, cipher, tag, AssociatedData);
            }

            var payload = new byte[NonceBytes + TagBytes + cipher.Length];
            nonce.CopyTo(payload, 0);
            tag.CopyTo(payload, NonceBytes);
            cipher.CopyTo(payload, NonceBytes + TagBytes);

            return VersionPrefix + Convert.ToBase64String(payload);
        }

        /// <summary>
        /// Decrypts a value produced by <see cref="Encrypt"/>.
        /// Throws <see cref="CryptographicException"/> if the value is malformed, was tampered with,
        /// or was encrypted with a different key. Empty input returns an empty string.
        /// </summary>
        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;

            if (!cipherText.StartsWith(VersionPrefix, StringComparison.Ordinal))
            {
                throw new CryptographicException("Unrecognised ciphertext format.");
            }

            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(cipherText[VersionPrefix.Length..]);
            }
            catch (FormatException)
            {
                throw new CryptographicException("Ciphertext is not valid base64.");
            }

            if (payload.Length < NonceBytes + TagBytes)
            {
                throw new CryptographicException("Ciphertext is too short.");
            }

            var nonce = payload.AsSpan(0, NonceBytes);
            var tag = payload.AsSpan(NonceBytes, TagBytes);
            var cipher = payload.AsSpan(NonceBytes + TagBytes);
            var plain = new byte[cipher.Length];

            using (var aes = new AesGcm(EncryptionKey, TagBytes))
            {
                aes.Decrypt(nonce, cipher, tag, plain, AssociatedData);
            }

            return Encoding.UTF8.GetString(plain);
        }

        /// <summary>Non-throwing variant for display paths: on failure the result is an empty string.</summary>
        public static bool TryDecrypt(string? cipherText, out string plainText)
        {
            try
            {
                plainText = Decrypt(cipherText ?? string.Empty);
                return true;
            }
            catch (CryptographicException)
            {
                plainText = string.Empty;
                return false;
            }
        }

        /// <summary>
        /// Keyed hash (HMAC-SHA256, lowercase hex, 64 chars) of a card number, used to detect duplicates without
        /// decrypting anything. Spaces and dashes are ignored. Without the key the hash cannot be brute-forced
        /// back to a card number, which plain SHA-256 of a 16-digit number could be.
        /// </summary>
        public static string HashCardNumber(string cardNumber)
        {
            var digits = new StringBuilder(cardNumber.Length);
            foreach (var c in cardNumber)
            {
                if (c is not (' ' or '-')) digits.Append(c);
            }

            var hash = HMACSHA256.HashData(HashKey, Encoding.UTF8.GetBytes(digits.ToString()));
            return Convert.ToHexStringLower(hash);
        }
    }
}
