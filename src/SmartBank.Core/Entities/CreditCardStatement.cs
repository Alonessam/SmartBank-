using System;

namespace SmartBank.Core.Entities
{
    public class CreditCardStatement
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid CreditCardId { get; set; }
        public string PeriodName { get; set; } = string.Empty; // e.g. "Haziran 2026"
        public decimal PeriodDebt { get; set; }
        public decimal MinimumPayment { get; set; }
        public decimal PaidAmount { get; set; } = 0.00m;
        public DateTime CutoffDate { get; set; }
        public DateTime DueDate { get; set; }
        public bool IsPaid { get; set; } = false;

        // Navigation Properties
        public CreditCard? CreditCard { get; set; }
    }
}
