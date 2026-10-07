using System;

namespace SmartBank.Core.DTOs
{
    public class ChatMessageDto
    {
        public Guid Id { get; set; }

        /// <summary>The conversation the message belongs to. Set on every message that reaches a client (history, live pushes).</summary>
        public Guid SessionId { get; set; }
        public string Sender { get; set; } = string.Empty; // "User", "AI", "Agent"
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}
