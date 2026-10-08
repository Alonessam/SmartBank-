using System.ComponentModel.DataAnnotations;

namespace SmartBank.Core.DTOs
{
    /// <summary>
    /// What a person's first or last name may look like. Real names contain spaces ("Zeynep Nur"), hyphens ("Çelik-Yılmaz"),
    /// apostrophes ("O'Neil") and dots ("Ş. Ç."); the old letters-only rule rejected them. The web form uses the same rule
    /// (NAME_PATTERN in app.js). Letters and combining marks of any script are accepted, digits and symbols are not.
    /// </summary>
    public static class NameRules
    {
        public const string Pattern = @"^[\p{L}\p{M}]+(?:[ '’.\-]+[\p{L}\p{M}]+)*\.?\z";
        public const string Message = "Names can contain letters, spaces, hyphens, apostrophes and dots only.";
    }

    public class RegisterDto
    {
        [Required(ErrorMessage = "Username is required.")]
        [StringLength(50, MinimumLength = 3, ErrorMessage = "Username must be between 3 and 50 characters.")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "T.C. Kimlik Numarası is required.")]
        [RegularExpression(@"^[0-9]{11}$", ErrorMessage = "T.C. Kimlik Numarası must be exactly 11 digits.")]
        public string Tckn { get; set; } = string.Empty;

        [Required(ErrorMessage = "Password is required.")]
        [RegularExpression(@"^[0-9]{6}$", ErrorMessage = "Password must be exactly 6 digits.")]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "First name is required.")]
        [RegularExpression(NameRules.Pattern, ErrorMessage = NameRules.Message)]
        [StringLength(50, ErrorMessage = "First name cannot exceed 50 characters.")]
        public string FirstName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Last name is required.")]
        [RegularExpression(NameRules.Pattern, ErrorMessage = NameRules.Message)]
        [StringLength(50, ErrorMessage = "Last name cannot exceed 50 characters.")]
        public string LastName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email is required.")]
        [EmailAddress(ErrorMessage = "Invalid email address format.")]
        [StringLength(100, ErrorMessage = "Email cannot exceed 100 characters.")]
        public string Email { get; set; } = string.Empty;
    }
}
