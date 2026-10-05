using System.ComponentModel.DataAnnotations;

namespace SmartBank.Core.DTOs
{
    public class RefreshRequestDto
    {
        [Required]
        [StringLength(200, MinimumLength = 20)]
        public string RefreshToken { get; set; } = string.Empty;
    }
}
