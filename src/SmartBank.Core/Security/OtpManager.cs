using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SmartBank.Core.Common;
using SmartBank.Core.Entities;

namespace SmartBank.Core.Security
{
    public enum OtpCheckResult
    {
        Valid,
        Invalid,
        Expired,
        TooManyAttempts,
        NoPendingCode
    }

    /// <summary>
    /// One-time-code rules in one place, as pure functions over the <see cref="User"/> entity (the caller saves).
    ///
    /// - The code is generated with a cryptographic RNG and is valid for <see cref="Lifetime"/>.
    /// - A code is bound to a purpose (login, transfer, password reset) and is useless for any other.
    /// - A code can also be bound to the exact action it approves (e.g. one specific transfer): changing the
    ///   amount or the recipient after the code was issued makes it invalid ("dynamic linking").
    /// - Wrong guesses are counted; after <see cref="MaxFailedAttempts"/> the code is destroyed, so a 6-digit
    ///   code cannot be brute-forced inside its lifetime (5 guesses out of 900,000 possible codes).
    /// - A correct code is single-use and the comparison is constant-time.
    /// </summary>
    public static class OtpManager
    {
        public const int MaxFailedAttempts = 5;
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

        public static string Issue(User user, OtpPurpose purpose, DateTime utcNow, string? binding = null)
        {
            var code = SecureRandom.Next(100000, 1000000).ToString();

            user.TwoFactorSecret = code;
            user.TwoFactorExpiry = utcNow + Lifetime;
            user.PendingOtpPurpose = purpose;
            user.PendingOtpBinding = binding;
            user.OtpFailedCount = 0;

            return code;
        }

        /// <summary>
        /// True when a code for this purpose was issued less than <paramref name="cooldown"/> ago.
        /// Used to stop someone from using a public endpoint to flood a victim's mailbox with codes.
        /// </summary>
        public static bool IsCoolingDown(User user, OtpPurpose purpose, DateTime utcNow, TimeSpan cooldown)
        {
            // Also while the code is gone after too many wrong guesses (see Verify): asking for a new one right away would
            // hand out a fresh budget of guesses with every request.
            if (user.PendingOtpPurpose != purpose || !user.TwoFactorExpiry.HasValue)
            {
                return false;
            }

            var issuedAt = user.TwoFactorExpiry.Value - Lifetime;
            return utcNow - issuedAt < cooldown;
        }

        /// <summary>
        /// True when the user holds an unexpired code that was issued for a *different* purpose. There is only one pending
        /// slot, so issuing a new code would silently destroy it: a public "forgot password" request must not be able to
        /// invalidate the login or transfer code a customer is typing in right now.
        /// </summary>
        public static bool HasLivePendingCodeForOtherPurpose(User user, OtpPurpose purpose, DateTime utcNow) =>
            !string.IsNullOrEmpty(user.TwoFactorSecret) &&
            user.TwoFactorExpiry.HasValue &&
            user.TwoFactorExpiry.Value >= utcNow &&
            user.PendingOtpPurpose.HasValue &&
            user.PendingOtpPurpose.Value != purpose;

        public static OtpCheckResult Verify(User user, OtpPurpose purpose, string? submittedCode, DateTime utcNow, string? binding = null)
        {
            // Nothing pending for this purpose: there is nothing to guess, so this does not count as a failed attempt.
            if (string.IsNullOrEmpty(user.TwoFactorSecret) ||
                !user.TwoFactorExpiry.HasValue ||
                user.PendingOtpPurpose != purpose)
            {
                return OtpCheckResult.NoPendingCode;
            }

            if (user.TwoFactorExpiry.Value < utcNow)
            {
                Clear(user);
                return OtpCheckResult.Expired;
            }

            var codeMatches = submittedCode is not null && ConstantTimeEquals(user.TwoFactorSecret, submittedCode);
            var bindingMatches = ConstantTimeEquals(user.PendingOtpBinding ?? string.Empty, binding ?? string.Empty);

            if (codeMatches && bindingMatches)
            {
                Clear(user);
                return OtpCheckResult.Valid;
            }

            user.OtpFailedCount++;
            if (user.OtpFailedCount >= MaxFailedAttempts)
            {
                Clear(user);

                // The code is destroyed, but "when was a code issued" is kept (as of now): the cooldown for the next one starts here.
                user.PendingOtpPurpose = purpose;
                user.TwoFactorExpiry = utcNow + Lifetime;
                return OtpCheckResult.TooManyAttempts;
            }

            return OtpCheckResult.Invalid;
        }

        public static void Clear(User user)
        {
            user.TwoFactorSecret = null;
            user.TwoFactorExpiry = null;
            user.PendingOtpPurpose = null;
            user.PendingOtpBinding = null;
            user.OtpFailedCount = 0;
        }

        /// <summary>
        /// Binding value for a money transfer: SHA-256 (hex) over source, destination and amount, so a code issued
        /// for one transfer cannot approve a different one.
        /// </summary>
        public static string TransferBinding(string sourceAccountNumber, string destinationAccountNumber, decimal amount)
        {
            var text = string.Create(CultureInfo.InvariantCulture, $"{sourceAccountNumber}|{destinationAccountNumber}|{amount:0.00}");
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }

        private static bool ConstantTimeEquals(string a, string b) =>
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }
}
