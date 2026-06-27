using System;
using System.Collections.Generic;

namespace SmartBank.Core.DTOs
{
    public class CreditCardStatementDto
    {
        public Guid Id { get; set; }
        public string PeriodName { get; set; } = string.Empty;
        public decimal PeriodDebt { get; set; }
        public decimal MinimumPayment { get; set; }
        public decimal PaidAmount { get; set; }
        public DateTime CutoffDate { get; set; }
        public DateTime DueDate { get; set; }
        public bool IsPaid { get; set; }
        public List<CreditCardTransactionDto> Transactions { get; set; } = new List<CreditCardTransactionDto>();
    }
}
