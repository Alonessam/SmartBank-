using System.ComponentModel.DataAnnotations;

namespace SmartBank.Core.DTOs
{
    public class Toggle2FaRequestDto
    {
        public bool Enable { get; set; }

        /// <summary>The user's current PIN. Changing a security setting needs it, so a stolen access token is not enough.</summary>
        [RegularExpression(@"^\d{6}$", ErrorMessage = "Password must be exactly 6 digits.")]
        public string? Password { get; set; }
    }
}
