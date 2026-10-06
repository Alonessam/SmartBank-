using System;

namespace SmartBank.Core.Entities
{
    /// <summary>
    /// A long-lived, single-use credential that is exchanged for a new short-lived access token. Only a hash is stored,
    /// so a database leak does not hand out working tokens. Every rotation stays in the same <see cref="FamilyId"/>:
    /// if an already-used token shows up again, somebody holds a copy and the whole family is revoked.
    /// </summary>
    public class RefreshToken : IConcurrencyVersioned
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }

        /// <summary>SHA-256 (hex) of the random token the client holds.</summary>
        public string TokenHash { get; set; } = string.Empty;

        public Guid FamilyId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresAt { get; set; }

        /// <summary>Set when this token was exchanged for the next one.</summary>
        public DateTime? UsedAt { get; set; }

        /// <summary>Set on logout, password reset, lockout, or when reuse of the family was detected.</summary>
        public DateTime? RevokedAt { get; set; }

        public string CreatedByIp { get; set; } = "unknown";

        /// <summary>Concurrency token: two simultaneous refreshes with the same token cannot both win.</summary>
        public int Version { get; set; }
    }
}
