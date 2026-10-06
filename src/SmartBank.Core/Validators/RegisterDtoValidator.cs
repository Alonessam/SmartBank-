using FluentValidation;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using System.Linq;
using System.Text.RegularExpressions;

namespace SmartBank.Core.Validators
{
    public class RegisterDtoValidator : AbstractValidator<RegisterDto>
    {
        // First + " " + last name is stored in a 100-character column, so each part is capped at 50.
        public const int MaxUsernameLength = 50;
        public const int MaxNameLength = 50;
        public const int MaxEmailLength = 100;
        public const int MaxFullNameLength = 100;

        // One rule for both names, shared with the DTO attributes and the web form (see NameRules).
        private static readonly Regex NamePattern = new(NameRules.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public RegisterDtoValidator()
        {
            RuleFor(x => x.Username)
                .NotEmpty().WithMessage("Username is required.")
                .MinimumLength(3).WithMessage("Username must be at least 3 characters long.")
                .MaximumLength(MaxUsernameLength).WithMessage("Username cannot exceed 50 characters.");

            RuleFor(x => x.Tckn)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("T.C. Kimlik Numarası is required.")
                .Length(11).WithMessage("T.C. Kimlik Numarası must be exactly 11 characters long.")
                .Must(x => x.All(char.IsDigit)).WithMessage("T.C. Kimlik Numarası must contain only digits.")
                .Must(TcKimlikNo.IsValid).WithMessage("T.C. Kimlik Numarası is not valid: its check digits do not match. Please re-check the number.");

            RuleFor(x => x.Password)
                .NotEmpty().WithMessage("Password is required.")
                .Length(6).WithMessage("Password must be exactly 6 characters long.")
                .Must(x => x.All(char.IsDigit)).WithMessage("Password must contain only digits.");

            RuleFor(x => x.FirstName)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("First Name is required.")
                .MaximumLength(MaxNameLength).WithMessage("First name cannot exceed 50 characters.")
                .Must(x => NamePattern.IsMatch(x)).WithMessage(NameRules.Message);

            RuleFor(x => x.LastName)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Last Name is required.")
                .MaximumLength(MaxNameLength).WithMessage("Last name cannot exceed 50 characters.")
                .Must(x => NamePattern.IsMatch(x)).WithMessage(NameRules.Message);

            // "First Last" is stored in a 100-character column: 50 + a space + 50 would be one too many.
            RuleFor(x => x)
                .Must(x => (x.FirstName?.Length ?? 0) + 1 + (x.LastName?.Length ?? 0) <= MaxFullNameLength)
                .When(x => x.FirstName?.Length <= MaxNameLength && x.LastName?.Length <= MaxNameLength)
                .WithName("FullName")
                .WithMessage("First and last name together cannot exceed 99 characters.");

            RuleFor(x => x.Email)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Email is required.")
                .MaximumLength(MaxEmailLength).WithMessage("Email cannot exceed 100 characters.")
                .EmailAddress().WithMessage("A valid email address is required.");
        }
    }
}
