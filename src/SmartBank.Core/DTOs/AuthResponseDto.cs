using System;

namespace SmartBank.Core.DTOs
{
    public class AuthResponseDto
    {
        public string Token { get; set; } = string.Empty;
        public Guid UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Tckn { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
    }
}
