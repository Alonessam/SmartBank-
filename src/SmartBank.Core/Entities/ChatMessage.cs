using System;

namespace SmartBank.Core.Entities
{
    public class ChatMessage
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid SessionId { get; set; }
        public string Sender { get; set; } = string.Empty; // "User", "AI", "Agent"
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation Properties
        public ChatSession? Session { get; set; }
    }
}
