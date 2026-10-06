using FluentValidation;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;

namespace SmartBank.Core.Validators
{
    public class TransferRequestDtoValidator : AbstractValidator<TransferRequestDto>
    {
        public TransferRequestDtoValidator()
        {
            RuleFor(x => x.SourceAccountNumber)
                .NotEmpty().WithMessage("Source account number is required.")
                .MaximumLength(30).WithMessage("Account number cannot exceed 30 characters.");

            RuleFor(x => x.DestinationAccountNumber)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Destination account number is required.")
                .MinimumLength(10).WithMessage("Destination account number must be at least 10 characters long.")
                .MaximumLength(30).WithMessage("Account number cannot exceed 30 characters.");

            RuleFor(x => x.Amount)
                .Cascade(CascadeMode.Stop)
                .GreaterThan(0).WithMessage("Transfer amount must be greater than zero.")
                .LessThanOrEqualTo(Money.MaxAmount).WithMessage("Transfer amount cannot exceed 10,000,000.00.")
                .Must(Money.HasValidScale).WithMessage(Money.ScaleMessage);

            RuleFor(x => x.Description)
                .MaximumLength(200).WithMessage("Description cannot exceed 200 characters.");

            RuleFor(x => x.Category)
                .MaximumLength(50).WithMessage("Category cannot exceed 50 characters.");

            RuleFor(x => x.OtpCode)
                .MaximumLength(10).WithMessage("The verification code cannot exceed 10 characters.");
        }
    }
}
