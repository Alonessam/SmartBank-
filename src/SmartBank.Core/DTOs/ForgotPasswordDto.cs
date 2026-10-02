using System.ComponentModel.DataAnnotations;

namespace SmartBank.Core.DTOs
{
    /// <summary>Step 1 of a password reset: ask for a verification code to be e-mailed.</summary>
    public class ForgotPasswordDto
    {
        [Required(ErrorMessage = "T.C. Kimlik Numarası is required.")]
        [RegularExpression(@"^\d{11}$", ErrorMessage = "T.C. Kimlik Numarası must be exactly 11 digits.")]
        public string Tckn { get; set; } = string.Empty;
    }

    /// <summary>Step 2 of a password reset: the e-mailed code plus the new PIN.</summary>
    public class ResetPasswordDto
    {
        [Required(ErrorMessage = "T.C. Kimlik Numarası is required.")]
        [RegularExpression(@"^\d{11}$", ErrorMessage = "T.C. Kimlik Numarası must be exactly 11 digits.")]
        public string Tckn { get; set; } = string.Empty;

        [Required(ErrorMessage = "Verification code is required.")]
        [RegularExpression(@"^\d{6}$", ErrorMessage = "Verification code must be exactly 6 digits.")]
        public string Code { get; set; } = string.Empty;

        [Required(ErrorMessage = "New Password is required.")]
        [RegularExpression(@"^\d{6}$", ErrorMessage = "Password must be exactly 6 digits.")]
        public string NewPassword { get; set; } = string.Empty;
    }
}
