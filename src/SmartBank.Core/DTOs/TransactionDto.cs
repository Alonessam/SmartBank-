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

        // The two sides of the movement, each in its own currency. A side the row does not have (no account, or not part of
        // this kind of movement) is null. Plain transfer: both sides carry Amount. Deposit: only the destination side.
        // Card payment: only the source side. Exchange: the source side is what was debited (TRY cost of a purchase, the
        // asset quantity of a sale), the destination side what was credited. Amount and Type are unchanged.
        public string? SourceCurrency { get; set; }
        public string? DestinationCurrency { get; set; }
        public decimal? SourceAmount { get; set; }
        public decimal? DestinationAmount { get; set; }

        public string Description { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Category { get; set; } = "Diğer";
        public DateTime CreatedAt { get; set; }
    }
}
