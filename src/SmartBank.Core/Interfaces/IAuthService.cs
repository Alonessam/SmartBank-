using System.Threading.Tasks;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;

namespace SmartBank.Core.Interfaces
{
    public interface IAuthService
    {
        Task<ServiceResult<AuthResponseDto>> RegisterAsync(RegisterDto registerDto, string? ipAddress = null);
        Task<ServiceResult<AuthResponseDto>> LoginAsync(LoginDto loginDto, string? ipAddress = null);

        /// <summary>Step 1 of a password reset. Always succeeds, so it cannot be used to find out which T.C. numbers are registered.</summary>
        Task<ServiceResult<bool>> RequestPasswordResetAsync(ForgotPasswordDto forgotPasswordDto, string? ipAddress = null);

        /// <summary>Step 2 of a password reset: needs the e-mailed code.</summary>
        Task<ServiceResult<bool>> ResetPasswordAsync(ResetPasswordDto resetPasswordDto, string? ipAddress = null);

        Task<ServiceResult<bool>> Toggle2FaAsync(Guid userId, bool enable);
        Task<ServiceResult<bool>> Get2FaStatusAsync(Guid userId);
        Task<ServiceResult<AuthResponseDto>> Verify2FaAsync(Verify2FaDto verify2FaDto, string? ipAddress = null);
    }
}
