using System;

namespace SmartBank.Core.DTOs
{
    public class ChatSessionDto
    {
        public Guid Id { get; set; }
        public Guid? UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
