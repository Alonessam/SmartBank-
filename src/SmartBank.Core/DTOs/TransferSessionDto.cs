using System.ComponentModel.DataAnnotations;

namespace SmartBank.Core.DTOs
{
    public class TransferSessionDto
    {
        [Required]
        [StringLength(100, MinimumLength = 1)]
        public string Department { get; set; } = string.Empty;
    }
}
