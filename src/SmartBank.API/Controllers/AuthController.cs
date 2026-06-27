using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;
using FluentValidation;
using System.Linq;

namespace SmartBank.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
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

            var result = await _authService.RegisterAsync(registerDto);

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

            var result = await _authService.LoginAsync(loginDto);

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

            var result = await _authService.Verify2FaAsync(verify2FaDto);

            if (!result.IsSuccess)
            {
                return BadRequest(new { result.IsSuccess, result.ErrorKey, result.Message });
            }

            return Ok(result.Data);
        }

        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto forgotPasswordDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var result = await _authService.ForgotPasswordAsync(forgotPasswordDto);

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

        private Guid GetUserId()
        {
            var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(userIdStr, out var userId) ? userId : Guid.Empty;
        }
    }
}
