using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;
using FluentValidation;
using System.Linq;

namespace SmartBank.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [EnableRateLimiting("auth")] // per-IP limit on every auth endpoint, see Program.cs
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly IValidator<RegisterDto> _registerValidator;

        public AuthController(IAuthService authService, IValidator<RegisterDto> registerValidator)
        {
            _authService = authService;
            _registerValidator = registerValidator;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterDto registerDto)
        {
            var validationResult = await _registerValidator.ValidateAsync(registerDto);
            if (!validationResult.IsValid)
            {
                return BadRequest(new { 
                    IsSuccess = false, 
                    ErrorKey = "ValidationError", 
                    Message = string.Join(" ", validationResult.Errors.Select(e => e.ErrorMessage)) 
                });
            }

            var result = await _authService.RegisterAsync(registerDto, ClientIp());

            if (!result.IsSuccess)
            {
                // Returns object containing IsSuccess = false, ErrorKey (for localization), and Message
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }

            return Ok(result.Data);
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto loginDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var result = await _authService.LoginAsync(loginDto, ClientIp());

            if (!result.IsSuccess)
            {
                // Returns object containing IsSuccess = false, ErrorKey (for localization), and Message
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }

            return Ok(result.Data);
        }

        [HttpPost("verify-2fa")]
        public async Task<IActionResult> Verify2Fa([FromBody] Verify2FaDto verify2FaDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var result = await _authService.Verify2FaAsync(verify2FaDto, ClientIp());

            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }

            return Ok(result.Data);
        }

        // Exchanges the single-use refresh token for a new access token and a new refresh token. The token itself is the
        // credential (256 random bits), so this is anonymous; it has its own, more generous limit because every client
        // calls it every few minutes.
        [HttpPost("refresh")]
        [EnableRateLimiting("refresh")]
        public async Task<IActionResult> Refresh([FromBody] RefreshRequestDto request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var result = await _authService.RefreshAsync(request.RefreshToken, ClientIp());

            if (!result.IsSuccess)
            {
                return Unauthorized(new { result.IsSuccess, result.ErrorKey, result.Message });
            }

            return Ok(result.Data);
        }

        // Ends the session. Always 204, so the endpoint does not reveal whether a token was valid.
        [HttpPost("logout")]
        [EnableRateLimiting("refresh")]
        public async Task<IActionResult> Logout([FromBody] RefreshRequestDto request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            await _authService.LogoutAsync(request.RefreshToken, ClientIp());
            return NoContent();
        }

        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto forgotPasswordDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            // Step 1: e-mail a code. The answer is the same whether or not the T.C. number is registered.
            await _authService.RequestPasswordResetAsync(forgotPasswordDto, ClientIp());

            return Ok(new { Message = "If this T.C. Kimlik Numarası is registered, a verification code has been sent to its e-mail address." });
        }

        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto resetPasswordDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            // Step 2: the e-mailed code is required to set a new PIN.
            var result = await _authService.ResetPasswordAsync(resetPasswordDto, ClientIp());

            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }

            return Ok(new { Message = "Password reset successfully." });
        }

        [Authorize]
        [HttpGet("2fa-status")]
        public async Task<IActionResult> Get2FaStatus()
        {
            var userId = GetUserId();
            if (userId == Guid.Empty)
            {
                return Unauthorized();
            }

            var result = await _authService.Get2FaStatusAsync(userId);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }

            return Ok(new { Enabled = result.Data });
        }

        [Authorize]
        [HttpPost("toggle-2fa")]
        public async Task<IActionResult> Toggle2Fa([FromBody] Toggle2FaRequestDto toggle2FaRequest)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var userId = GetUserId();
            if (userId == Guid.Empty)
            {
                return Unauthorized();
            }

            var result = await _authService.Toggle2FaAsync(userId, toggle2FaRequest.Enable);
            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }

            return Ok(new { Enabled = result.Data });
        }

        // Behind a reverse proxy this is the real client address only if forwarded headers are enabled (see Dockerfile).
        private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

        private Guid GetUserId()
        {
            var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(userIdStr, out var userId) ? userId : Guid.Empty;
        }
    }
}
