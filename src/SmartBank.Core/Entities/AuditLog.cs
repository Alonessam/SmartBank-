using System;

namespace SmartBank.Core.Entities
{
    public class AuditLog
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string Action { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
        public string IpAddress { get; set; } = "127.0.0.1";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
