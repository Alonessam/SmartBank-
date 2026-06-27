using System;

namespace SmartBank.Core.DTOs
{
    public class TransactionDto
    {
        public Guid Id { get; set; }
        public string? SourceAccountNumber { get; set; }
        public string? DestinationAccountNumber { get; set; }
        public string? SourceAccountOwnerName { get; set; }
        public string? DestinationAccountOwnerName { get; set; }
        public decimal Amount { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Category { get; set; } = "Diğer";
        public DateTime CreatedAt { get; set; }
    }
}
