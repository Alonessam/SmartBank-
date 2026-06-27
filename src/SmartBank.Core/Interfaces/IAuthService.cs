using System.Threading.Tasks;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;

namespace SmartBank.Core.Interfaces
{
    public interface IAuthService
    {
        Task<ServiceResult<AuthResponseDto>> RegisterAsync(RegisterDto registerDto);
        Task<ServiceResult<AuthResponseDto>> LoginAsync(LoginDto loginDto);
        Task<ServiceResult<bool>> ForgotPasswordAsync(ForgotPasswordDto forgotPasswordDto);
        Task<ServiceResult<bool>> Toggle2FaAsync(Guid userId, bool enable);
        Task<ServiceResult<bool>> Get2FaStatusAsync(Guid userId);
        Task<ServiceResult<AuthResponseDto>> Verify2FaAsync(Verify2FaDto verify2FaDto);
    }
}
