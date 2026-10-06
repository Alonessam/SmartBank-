using System;

namespace SmartBank.Core.DTOs
{
    public class AuthResponseDto
    {
        public string Token { get; set; } = string.Empty;

        /// <summary>UTC time the access token stops working; the client refreshes shortly before it.</summary>
        public DateTime AccessTokenExpiresAt { get; set; }

        /// <summary>Single-use token for POST /api/auth/refresh. Replaced by a new one on every refresh.</summary>
        public string RefreshToken { get; set; } = string.Empty;
        public Guid UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Tckn { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty; // "Customer" or "Agent", for the UI; the server trusts only the token
    }
}
