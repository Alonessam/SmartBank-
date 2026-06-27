using System;
using System.Collections.Generic;

namespace SmartBank.Core.Entities
{
    public class User
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Username { get; set; } = string.Empty;
        public string Tckn { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Two-Factor Authentication fields
        public string? TwoFactorSecret { get; set; }
        public DateTime? TwoFactorExpiry { get; set; }
        public bool TwoFactorEnabled { get; set; } = false;

        // Navigation Properties
        public ICollection<Account> Accounts { get; set; } = new List<Account>();
        public ICollection<ChatSession> ChatSessions { get; set; } = new List<ChatSession>();
        public ICollection<CreditCard> CreditCards { get; set; } = new List<CreditCard>();
    }
}
