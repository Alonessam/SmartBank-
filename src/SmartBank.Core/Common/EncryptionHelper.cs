using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SmartBank.Core.Common
{
    public static class EncryptionHelper
    {
        private static readonly byte[] Key;
        private static readonly byte[] Iv;

        static EncryptionHelper()
        {
            // Ensure Key is exactly 32 bytes (256 bits)
            var keyBytes = new byte[32];
            var tempKey = Encoding.UTF8.GetBytes("SmartBankEncryptionKeySecret2026!");
            Array.Copy(tempKey, keyBytes, Math.Min(tempKey.Length, 32));
            Key = keyBytes;

            // Ensure IV is exactly 16 bytes (128 bits)
            var ivBytes = new byte[16];
            var tempIv = Encoding.UTF8.GetBytes("SmartBankIvVectorVector2026!");
            Array.Copy(tempIv, ivBytes, Math.Min(tempIv.Length, 16));
            Iv = ivBytes;
        }

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
            catch
            {
                return "[Decryption Error]";
            }
        }
    }
}
