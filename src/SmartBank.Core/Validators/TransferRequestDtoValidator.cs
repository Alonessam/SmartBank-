using FluentValidation;
using SmartBank.Core.DTOs;

namespace SmartBank.Core.Validators
{
    public class TransferRequestDtoValidator : AbstractValidator<TransferRequestDto>
    {
        public TransferRequestDtoValidator()
        {
            RuleFor(x => x.SourceAccountNumber)
                .NotEmpty().WithMessage("Source account number is required.");

            RuleFor(x => x.DestinationAccountNumber)
                .NotEmpty().WithMessage("Destination account number is required.")
                .MinimumLength(10).WithMessage("Destination account number must be at least 10 characters long.");

            RuleFor(x => x.Amount)
                .GreaterThan(0).WithMessage("Transfer amount must be greater than zero.");
        }
    }
}
