using System;
using System.Security.Cryptography;

namespace SmartBank.Core.Common
{
    /// <summary>
    /// Cryptographically secure random values for anything an attacker must not be able to predict:
    /// one-time passwords, card numbers, CVVs, account numbers and codes.
    /// System.Random is a statistical generator, not a cryptographic one: its internal state can be reconstructed
    /// from observed outputs, so later values become predictable. It must not be used for these.
    /// (Simulation noise, such as demo market-rate jitter, may keep using System.Random.)
    /// </summary>
    public static class SecureRandom
    {
        /// <summary>Uniform random integer in [minInclusive, maxExclusive).</summary>
        public static int Next(int minInclusive, int maxExclusive) =>
            RandomNumberGenerator.GetInt32(minInclusive, maxExclusive);

        /// <summary>A string of <paramref name="length"/> uniformly random decimal digits (leading zeros allowed).</summary>
        public static string Digits(int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);

            var chars = new char[length];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
            }

            return new string(chars);
        }
    }
}
