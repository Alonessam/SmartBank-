using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using FluentValidation;
using SmartBank.API.Security;
using SmartBank.Core.DTOs;
using SmartBank.Core.Interfaces;

namespace SmartBank.API.Controllers
{
    /// <summary>
    /// Sign-up, sign-in, one-time codes, password reset and the two-factor setting. Everything here is limited per client
    /// address (see Program.cs). Wrong credentials, a locked account and an unknown T.C. number all give the same answer.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public class AuthController : SmartBankControllerBase
    {
        private readonly IAuthService _authService;
        private readonly IValidator<RegisterDto> _registerValidator;

        public AuthController(IAuthService authService, IValidator<RegisterDto> registerValidator)
        {
            _authService = authService;
            _registerValidator = registerValidator;
        }

        /// <summary>Creates a customer with a default account and card and signs them in.</summary>
        [HttpPost("register")]
        [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Register([FromBody] RegisterDto registerDto)
        {
            var validation = await _registerValidator.ValidateAsync(registerDto);
            if (!validation.IsValid)
            {
                return ErrorResult("ValidationError", string.Join(" ", validation.Errors.Select(e => e.ErrorMessage)));
            }

            return ToActionResult(await _authService.RegisterAsync(registerDto, ClientIp()));
        }

        /// <summary>Signs in with T.C. number and PIN. With two-factor on, answers 400 Requires2FA and e-mails a code (then call verify-2fa).</summary>
        [HttpPost("login")]
        [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Login([FromBody] LoginDto loginDto)
            => ToActionResult(await _authService.LoginAsync(loginDto, ClientIp()));

        [HttpPost("verify-2fa")]
        [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Verify2Fa([FromBody] Verify2FaDto verify2FaDto)
            => ToActionResult(await _authService.Verify2FaAsync(verify2FaDto, ClientIp()));

        /// <summary>
        /// Exchanges the single-use refresh token for a new access token and a new refresh token. The token itself is the
        /// credential (256 random bits), so this is anonymous; it has its own, more generous limit because every client
        /// calls it every few minutes.
        /// </summary>
        [HttpPost("refresh")]
        [EnableRateLimiting(RateLimitPolicies.Refresh)]
        [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> Refresh([FromBody] RefreshRequestDto request)
        {
            var result = await _authService.RefreshAsync(request.RefreshToken, ClientIp());

            if (!result.IsSuccess)
            {
                return Unauthorized(new { isSuccess = false, errorKey = result.ErrorKey, message = result.Message });
            }

            return Ok(result.Data);
        }

        /// <summary>Ends the session. Always 204, so the endpoint does not reveal whether a token was valid.</summary>
        [HttpPost("logout")]
        [EnableRateLimiting(RateLimitPolicies.Refresh)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<IActionResult> Logout([FromBody] RefreshRequestDto request)
        {
            await _authService.LogoutAsync(request.RefreshToken, ClientIp());
            return NoContent();
        }

        /// <summary>Step 1 of a password reset: e-mails a code. The answer is the same whether or not the T.C. number is registered.</summary>
        [HttpPost("forgot-password")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto forgotPasswordDto)
        {
            await _authService.RequestPasswordResetAsync(forgotPasswordDto, ClientIp());

            return Ok(new { Message = "If this T.C. Kimlik Numarası is registered, a verification code has been sent to its e-mail address." });
        }

        /// <summary>Step 2 of a password reset: the e-mailed code is required to set a new PIN.</summary>
        [HttpPost("reset-password")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto resetPasswordDto)
            => ToActionResult(await _authService.ResetPasswordAsync(resetPasswordDto, ClientIp()), _ => new { Message = "Password reset successfully." });

        [Authorize]
        [HttpGet("2fa-status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> Get2FaStatus()
            => ToActionResult(await _authService.Get2FaStatusAsync(GetUserId()), data => new { Enabled = data });

        /// <summary>
        /// Turns two-factor sign-in on or off. The body needs the current PIN in <c>password</c> (400 PinRequired when it is
        /// missing, 400 InvalidCredentials when it is wrong; a wrong PIN counts toward the account lockout).
        /// </summary>
        [Authorize]
        [HttpPost("toggle-2fa")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> Toggle2Fa([FromBody] Toggle2FaRequestDto toggle2FaRequest)
            => ToActionResult(await _authService.Toggle2FaAsync(GetUserId(), toggle2FaRequest.Enable, toggle2FaRequest.Password, ClientIp()),
                data => new { Enabled = data });
    }
}
