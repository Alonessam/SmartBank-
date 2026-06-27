using FluentValidation;
using SmartBank.Core.DTOs;
using System.Linq;

namespace SmartBank.Core.Validators
{
    public class RegisterDtoValidator : AbstractValidator<RegisterDto>
    {
        public RegisterDtoValidator()
        {
            RuleFor(x => x.Username)
                .NotEmpty().WithMessage("Username is required.")
                .MinimumLength(3).WithMessage("Username must be at least 3 characters long.");

            RuleFor(x => x.Tckn)
                .NotEmpty().WithMessage("T.C. Kimlik Numarası is required.")
                .Length(11).WithMessage("T.C. Kimlik Numarası must be exactly 11 characters long.")
                .Must(x => x.All(char.IsDigit)).WithMessage("T.C. Kimlik Numarası must contain only digits.");

            RuleFor(x => x.Password)
                .NotEmpty().WithMessage("Password is required.")
                .Length(6).WithMessage("Password must be exactly 6 characters long.")
                .Must(x => x.All(char.IsDigit)).WithMessage("Password must contain only digits.");

            RuleFor(x => x.FirstName)
                .NotEmpty().WithMessage("First Name is required.");

            RuleFor(x => x.LastName)
                .NotEmpty().WithMessage("Last Name is required.");

            RuleFor(x => x.Email)
                .NotEmpty().WithMessage("Email is required.")
                .EmailAddress().WithMessage("A valid email address is required.");
        }
    }
}
