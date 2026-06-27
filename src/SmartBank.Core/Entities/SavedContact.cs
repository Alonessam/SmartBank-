using System;

namespace SmartBank.Core.Entities
{
    public class SavedContact
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public string AccountNumber { get; set; } = string.Empty;
        public string Alias { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        public User? User { get; set; }
    }
}
