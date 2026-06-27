using System.ComponentModel.DataAnnotations;

namespace SmartBank.Core.DTOs
{
    public class TransferRequestDto
    {
        [Required(ErrorMessage = "Source account number is required.")]
        public string SourceAccountNumber { get; set; } = string.Empty;

        [Required(ErrorMessage = "Destination account number is required.")]
        public string DestinationAccountNumber { get; set; } = string.Empty;

        [Required(ErrorMessage = "Amount is required.")]
        [Range(0.01, 10000000.00, ErrorMessage = "Amount must be between 0.01 and 10,000,000.00.")]
        public decimal Amount { get; set; }

        [MaxLength(200, ErrorMessage = "Description cannot exceed 200 characters.")]
        public string Description { get; set; } = string.Empty;

        [MaxLength(50, ErrorMessage = "Category cannot exceed 50 characters.")]
        public string Category { get; set; } = "Diğer";

        public string? OtpCode { get; set; }
    }
}
