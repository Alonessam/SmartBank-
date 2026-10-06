using System.Text;
using Microsoft.Extensions.Configuration;

namespace SmartBank.Infrastructure.Security
{
    /// <summary>
    /// JWT configuration read from the "JwtSettings" section.
    /// The signing key is a secret: it must come from user-secrets or an environment
    /// variable (JwtSettings__Key), never from a committed file or a code default.
    /// </summary>
    public sealed class JwtSettings
    {
        public const string SectionName = "JwtSettings";

        // HS256 needs at least a 256-bit key.
        public const int MinKeyBytes = 32;

        public string Key { get; }
        public string Issuer { get; }
        public string Audience { get; }

        /// <summary>How long an access token works. Short, because it cannot be revoked; the refresh token renews it.</summary>
        public TimeSpan AccessTokenLifetime { get; }

        /// <summary>How long a refresh token (and so a sign-in that is not used) lasts. Each refresh starts a new window.</summary>
        public TimeSpan RefreshTokenLifetime { get; }

        public JwtSettings(string key, string issuer, string audience, TimeSpan? accessTokenLifetime = null, TimeSpan? refreshTokenLifetime = null)
        {
            Key = key;
            Issuer = issuer;
            Audience = audience;
            AccessTokenLifetime = accessTokenLifetime ?? TimeSpan.FromMinutes(15);
            RefreshTokenLifetime = refreshTokenLifetime ?? TimeSpan.FromDays(7);
        }

        public byte[] KeyBytes => Encoding.UTF8.GetBytes(Key);

        /// <summary>
        /// Reads and validates the settings. Throws at startup if the key is missing or too short,
        /// so the API can never run with a guessable signing key.
        /// </summary>
        private static int ReadInt(IConfiguration configuration, string name, int fallback, int min, int max)
        {
            var raw = configuration[$"{SectionName}:{name}"];
            if (string.IsNullOrWhiteSpace(raw)) return fallback;

            if (!int.TryParse(raw, out var value) || value < min || value > max)
            {
                throw new InvalidOperationException($"{SectionName}:{name} must be a whole number between {min} and {max}.");
            }

            return value;
        }

        public static JwtSettings From(IConfiguration configuration)
        {
            var key = configuration[$"{SectionName}:Key"];

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException(
                    "JwtSettings:Key is not configured. Set it with user-secrets " +
                    "(scripts/dev-secrets.ps1) or the JwtSettings__Key environment variable.");
            }

            if (Encoding.UTF8.GetByteCount(key) < MinKeyBytes)
            {
                throw new InvalidOperationException(
                    $"JwtSettings:Key is too short: HS256 needs at least {MinKeyBytes} bytes.");
            }

            var issuer = configuration[$"{SectionName}:Issuer"] ?? "SmartBankAPI";
            var audience = configuration[$"{SectionName}:Audience"] ?? "SmartBankApp";

            var accessMinutes = ReadInt(configuration, "AccessTokenMinutes", 15, 1, 24 * 60);
            var refreshDays = ReadInt(configuration, "RefreshTokenDays", 7, 1, 90);

            return new JwtSettings(key, issuer, audience, TimeSpan.FromMinutes(accessMinutes), TimeSpan.FromDays(refreshDays));
        }
    }
}
