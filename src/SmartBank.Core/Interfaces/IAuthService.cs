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

        /// <summary>
        /// Turns two-factor sign-in on or off. Needs the user's current PIN (error key PinRequired when it is missing,
        /// InvalidCredentials when it is wrong - a wrong PIN counts toward the lockout like a failed login).
        /// </summary>
        Task<ServiceResult<bool>> Toggle2FaAsync(Guid userId, bool enable, string? password, string? ipAddress = null);
        Task<ServiceResult<bool>> Get2FaStatusAsync(Guid userId);
        Task<ServiceResult<AuthResponseDto>> Verify2FaAsync(Verify2FaDto verify2FaDto, string? ipAddress = null);

        /// <summary>Exchanges a refresh token for a new access token and a new refresh token (rotation). The old one stops working.</summary>
        Task<ServiceResult<AuthResponseDto>> RefreshAsync(string refreshToken, string? ipAddress = null);

        /// <summary>Ends the session the refresh token belongs to. Never reveals whether the token was valid.</summary>
        Task LogoutAsync(string refreshToken, string? ipAddress = null);
    }
}
