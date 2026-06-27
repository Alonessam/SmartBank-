using System.ComponentModel.DataAnnotations;

namespace SmartBank.Core.DTOs
{
    public class Verify2FaDto
    {
        [Required]
        [RegularExpression(@"^\d{11}$")]
        public string Tckn { get; set; } = string.Empty;

        [Required]
        [RegularExpression(@"^\d{6}$")]
        public string Code { get; set; } = string.Empty;
    }
}
