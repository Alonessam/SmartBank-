using System;

namespace SmartBank.Core.DTOs
{
    public class ChatMessageDto
    {
        public Guid Id { get; set; }
        public string Sender { get; set; } = string.Empty; // "User", "AI", "Agent"
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}
