using System;
using System.Collections.Generic;

namespace SmartBank.Core.Entities
{
    public class User : IConcurrencyVersioned
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        // Optimistic-concurrency token, see IConcurrencyVersioned. Without it two parallel requests could both consume the
        // same one-time code, or each count a wrong guess against the same starting number and lose one of them.
        public int Version { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Tckn { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;

        // Authorization level. Registration always creates a Customer; support agents are promoted in the database.
        public UserRole Role { get; set; } = UserRole.Customer;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Two-Factor Authentication fields.
        // TwoFactorSecret holds the single pending one-time code (login, transfer or password reset),
        // PendingOtpPurpose says which of those it is for, OtpFailedCount counts wrong guesses against it.
        public string? TwoFactorSecret { get; set; }
        public DateTime? TwoFactorExpiry { get; set; }
        public bool TwoFactorEnabled { get; set; } = false;
        public OtpPurpose? PendingOtpPurpose { get; set; }
        public string? PendingOtpBinding { get; set; } // hash of the exact action a code approves (e.g. one transfer)
        public int OtpFailedCount { get; set; }

        // Brute-force protection: consecutive wrong PINs and the end of the current lockout, if any.
        public int FailedLoginCount { get; set; }
        public DateTime? LockoutEnd { get; set; }

        // Navigation Properties
        public ICollection<Account> Accounts { get; set; } = new List<Account>();
        public ICollection<ChatSession> ChatSessions { get; set; } = new List<ChatSession>();
        public ICollection<CreditCard> CreditCards { get; set; } = new List<CreditCard>();
    }
}
