using System.ComponentModel.DataAnnotations;

namespace SmartBank.Core.DTOs
{
    public class ForgotPasswordDto
    {
        [Required(ErrorMessage = "T.C. Kimlik Numarası is required.")]
        [RegularExpression(@"^\d{11}$", ErrorMessage = "T.C. Kimlik Numarası must be exactly 11 digits.")]
        public string Tckn { get; set; } = string.Empty;

        [Required(ErrorMessage = "New Password is required.")]
        [RegularExpression(@"^\d{6}$", ErrorMessage = "Password must be exactly 6 digits.")]
        public string NewPassword { get; set; } = string.Empty;
    }
}
