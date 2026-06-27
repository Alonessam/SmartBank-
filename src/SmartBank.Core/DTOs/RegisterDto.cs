using System.ComponentModel.DataAnnotations;

namespace SmartBank.Core.DTOs
{
    public class RegisterDto
    {
        [Required(ErrorMessage = "Username is required.")]
        [StringLength(50, MinimumLength = 3, ErrorMessage = "Username must be between 3 and 50 characters.")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "T.C. Kimlik Numarası is required.")]
        [RegularExpression(@"^\d{11}$", ErrorMessage = "T.C. Kimlik Numarası must be exactly 11 digits.")]
        public string Tckn { get; set; } = string.Empty;

        [Required(ErrorMessage = "Password is required.")]
        [RegularExpression(@"^\d{6}$", ErrorMessage = "Password must be exactly 6 digits.")]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "First name is required.")]
        [RegularExpression(@"^[a-zA-ZçğıöşüÇĞİÖŞÜ\s]+$", ErrorMessage = "First name can only contain letters.")]
        [StringLength(50, ErrorMessage = "First name cannot exceed 50 characters.")]
        public string FirstName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Last name is required.")]
        [RegularExpression(@"^[a-zA-ZçğıöşüÇĞİÖŞÜ]+$", ErrorMessage = "Last name can only contain letters (no spaces).")]
        [StringLength(50, ErrorMessage = "Last name cannot exceed 50 characters.")]
        public string LastName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email is required.")]
        [EmailAddress(ErrorMessage = "Invalid email address format.")]
        [StringLength(100, ErrorMessage = "Email cannot exceed 100 characters.")]
        public string Email { get; set; } = string.Empty;
    }
}
