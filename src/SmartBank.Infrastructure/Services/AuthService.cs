using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using SmartBank.Core.Common;
using SmartBank.Core.DTOs;
using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;
using SmartBank.Core.Security;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Security;

namespace SmartBank.Infrastructure.Services
{
    public class AuthService : IAuthService
    {
        // BCrypt hash used when the T.C. number is unknown, so that case costs as much time as a wrong PIN
        // and response time does not reveal which T.C. numbers are registered.
        private static readonly string DummyPasswordHash = BCrypt.Net.BCrypt.HashPassword("timing-equaliser-not-a-real-pin");

        private static readonly TimeSpan PasswordResetCooldown = TimeSpan.FromSeconds(60);

        private readonly SmartBankDbContext _context;
        private readonly IOtpDelivery _otpDelivery;
        private readonly JwtSettings _jwtSettings;

        public AuthService(SmartBankDbContext context, IConfiguration configuration, IOtpDelivery otpDelivery)
        {
            _context = context;
            _otpDelivery = otpDelivery;
            _jwtSettings = JwtSettings.From(configuration);
        }

        public async Task<ServiceResult<AuthResponseDto>> RegisterAsync(RegisterDto registerDto)
        {
            // Check if username already exists
            if (await _context.Users.AnyAsync(u => u.Username == registerDto.Username))
            {
                return ServiceResult<AuthResponseDto>.Failure("UsernameAlreadyExists", "Username is already taken.");
            }

            // Check if TCKN already exists
            if (await _context.Users.AnyAsync(u => u.Tckn == registerDto.Tckn))
            {
                return ServiceResult<AuthResponseDto>.Failure("TcknAlreadyExists", "T.C. Kimlik Numarası is already registered.");
            }

            // Hash password
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(registerDto.Password);

            var user = new User
            {
                Username = registerDto.Username,
                Tckn = registerDto.Tckn,
                Email = registerDto.Email,
                PasswordHash = passwordHash,
                FirstName = registerDto.FirstName,
                LastName = registerDto.LastName,
                FullName = $"{registerDto.FirstName} {registerDto.LastName}".Trim()
            };

            // Automatically create a default bank account with 1000 TRY for testing purposes
            var cardNum = GenerateCardNumber();

            var accountCode = "ACC-" + SecureRandom.Next(1000000, 10000000);
            while (await _context.Accounts.AnyAsync(a => a.AccountCode == accountCode))
            {
                accountCode = "ACC-" + SecureRandom.Next(1000000, 10000000);
            }

            var defaultAccount = new Account
            {
                User = user,
                AccountNumber = GenerateAccountNumber(),
                AccountCode = accountCode,
                Balance = 1000.00m,
                Currency = "TRY",
                EncryptedCardNumber = SmartBank.Core.Common.EncryptionHelper.Encrypt(cardNum),
                CardTheme = "theme-neon-blue"
            };

            user.Accounts.Add(defaultAccount);

            // Automatically create a default Credit Card with 10,000 TRY limit and 1,250 TRY debt.
            // No CVV is generated: the register response does not show card details and the CVV is never stored.
            var ccNumber = GenerateCardNumber();
            var defaultCreditCard = new CreditCard
            {
                User = user,
                EncryptedCardNumber = SmartBank.Core.Common.EncryptionHelper.Encrypt(ccNumber),
                CardNumberHash = SmartBank.Core.Common.EncryptionHelper.HashCardNumber(ccNumber),
                ExpiryDate = DateTime.UtcNow.AddYears(5).ToString("MM/yy"),
                CardLimit = 10000.00m,
                CurrentDebt = 1250.00m,
                CardTheme = "theme-neon-blue"
            };

            // Add mock transactions
            defaultCreditCard.Transactions.Add(new CreditCardTransaction
            {
                Description = "Market Harcaması",
                Amount = 450.00m,
                CreatedAt = DateTime.UtcNow.AddDays(-10)
            });
            defaultCreditCard.Transactions.Add(new CreditCardTransaction
            {
                Description = "Restoran Yemek Ödemesi",
                Amount = 300.00m,
                CreatedAt = DateTime.UtcNow.AddDays(-8)
            });
            defaultCreditCard.Transactions.Add(new CreditCardTransaction
            {
                Description = "Akaryakıt Harcaması",
                Amount = 500.00m,
                CreatedAt = DateTime.UtcNow.AddDays(-6)
            });

            // Add mock statement (Haziran 2026)
            defaultCreditCard.Statements.Add(new CreditCardStatement
            {
                PeriodName = "Haziran 2026",
                PeriodDebt = 1250.00m,
                MinimumPayment = 375.00m,
                PaidAmount = 0.00m,
                CutoffDate = DateTime.UtcNow.AddDays(-5),
                DueDate = DateTime.UtcNow.AddDays(5),
                IsPaid = false
            });

            user.CreditCards.Add(defaultCreditCard);

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            // Write Audit Log
            var audit = new AuditLog
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Action = "UserRegistered",
                Details = $"User registered with Username: {user.Username}, Tckn: {user.Tckn}",
                IpAddress = "127.0.0.1",
                CreatedAt = DateTime.UtcNow
            };
            _context.AuditLogs.Add(audit);
            await _context.SaveChangesAsync();

            var token = GenerateJwtToken(user);

            var response = new AuthResponseDto
            {
                Token = token,
                UserId = user.Id,
                Username = user.Username,
                Tckn = user.Tckn,
                FullName = user.FullName
            };

            return ServiceResult<AuthResponseDto>.Success(response);
        }

        public async Task<ServiceResult<AuthResponseDto>> LoginAsync(LoginDto loginDto, string? ipAddress = null)
        {
            var now = DateTime.UtcNow;
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Tckn == loginDto.Tckn);

            // A locked account is rejected before the PIN is even checked, so guessing during a lockout is pointless
            // and a lockout is not extended by further attempts.
            if (user != null && LoginLockout.IsLocked(user, now))
            {
                var minutes = (int)Math.Ceiling(LoginLockout.Remaining(user, now).TotalMinutes);
                return ServiceResult<AuthResponseDto>.Failure("AccountLocked",
                    $"Too many failed attempts. The account is locked for about {minutes} more minute(s).");
            }

            // Always run exactly one BCrypt verification, even for an unknown T.C. number, so response time
            // does not reveal which numbers are registered.
            var pinMatches = BCrypt.Net.BCrypt.Verify(loginDto.Password, user?.PasswordHash ?? DummyPasswordHash);

            if (user == null || !pinMatches)
            {
                if (user != null)
                {
                    LoginLockout.RegisterFailure(user, now);
                    var lockedNow = LoginLockout.IsLocked(user, now);
                    _context.AuditLogs.Add(NewAuditLog(user.Id, lockedNow ? "AccountLocked" : "LoginFailed",
                        lockedNow ? $"Account locked after {LoginLockout.MaxFailedAttempts} failed logins." : "Wrong PIN.", ipAddress));
                    await _context.SaveChangesAsync();
                }

                // Identical answer for "no such T.C. number" and "wrong PIN".
                return ServiceResult<AuthResponseDto>.Failure("InvalidCredentials", "Invalid T.C. Kimlik NumarasÄ± or password.");
            }

            LoginLockout.Reset(user);

            if (user.TwoFactorEnabled)
            {
                var code = OtpManager.Issue(user, OtpPurpose.Login, now);
                await _context.SaveChangesAsync();

                _otpDelivery.Send(user, code, OtpPurpose.Login);

                var message = "Ä°ki aÅŸamalÄ± doÄŸrulama gerekiyor.";
                if (_otpDelivery.ExposeCodeInResponse)
                {
                    message += $"|OTP:{code}"; // demo mode only, see IOtpDelivery.ExposeCodeInResponse
                }

                return ServiceResult<AuthResponseDto>.Failure("Requires2FA", message);
            }

            _context.AuditLogs.Add(NewAuditLog(user.Id, "UserLoggedIn", $"User logged in. Username: {user.Username}", ipAddress));
            await _context.SaveChangesAsync();

            return ServiceResult<AuthResponseDto>.Success(ToAuthResponse(user));
        }

        public async Task<ServiceResult<bool>> RequestPasswordResetAsync(ForgotPasswordDto forgotPasswordDto, string? ipAddress = null)
        {
            var now = DateTime.UtcNow;
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Tckn == forgotPasswordDto.Tckn);

            // Nothing in the answer depends on whether the T.C. number exists. The cooldown stops this public
            // endpoint from being used to flood somebody's mailbox with codes.
            if (user != null && !OtpManager.IsCoolingDown(user, OtpPurpose.PasswordReset, now, PasswordResetCooldown))
            {
                var code = OtpManager.Issue(user, OtpPurpose.PasswordReset, now);
                _context.AuditLogs.Add(NewAuditLog(user.Id, "PasswordResetRequested", "A password reset code was issued.", ipAddress));
                await _context.SaveChangesAsync();

                _otpDelivery.Send(user, code, OtpPurpose.PasswordReset);
            }

            return ServiceResult<bool>.Success(true);
        }

        public async Task<ServiceResult<bool>> ResetPasswordAsync(ResetPasswordDto resetPasswordDto, string? ipAddress = null)
        {
            var now = DateTime.UtcNow;
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Tckn == resetPasswordDto.Tckn);

            if (user == null)
            {
                BCrypt.Net.BCrypt.Verify(resetPasswordDto.NewPassword, DummyPasswordHash); // keep timing similar
                return ServiceResult<bool>.Failure("InvalidOrExpiredCode", "GeÃ§ersiz veya sÃ¼resi dolmuÅŸ doÄŸrulama kodu.");
            }

            var check = OtpManager.Verify(user, OtpPurpose.PasswordReset, resetPasswordDto.Code, now);
            if (check != OtpCheckResult.Valid)
            {
                await _context.SaveChangesAsync(); // persists the failed-attempt counter or the destroyed code
                return OtpFailure<bool>(check);
            }

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(resetPasswordDto.NewPassword);
            LoginLockout.Reset(user); // proving control of the mailbox also lifts a lockout

            _context.AuditLogs.Add(NewAuditLog(user.Id, "PasswordReset", $"User password reset. Username: {user.Username}", ipAddress));
            await _context.SaveChangesAsync();

            return ServiceResult<bool>.Success(true);
        }
public async Task<ServiceResult<bool>> Toggle2FaAsync(Guid userId, bool enable)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return ServiceResult<bool>.Failure("UserNotFound", "User details not found.");
            }

            user.TwoFactorEnabled = enable;
            _context.Users.Update(user);
            await _context.SaveChangesAsync();

            return ServiceResult<bool>.Success(enable);
        }

        public async Task<ServiceResult<bool>> Get2FaStatusAsync(Guid userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return ServiceResult<bool>.Failure("UserNotFound", "User details not found.");
            }

            return ServiceResult<bool>.Success(user.TwoFactorEnabled);
        }

        public async Task<ServiceResult<AuthResponseDto>> Verify2FaAsync(Verify2FaDto verify2FaDto, string? ipAddress = null)
        {
            var now = DateTime.UtcNow;
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Tckn == verify2FaDto.Tckn);

            // Same answer for an unknown T.C. number and a wrong code.
            if (user == null)
            {
                return ServiceResult<AuthResponseDto>.Failure("InvalidOrExpiredCode", "GeÃ§ersiz veya sÃ¼resi dolmuÅŸ doÄŸrulama kodu.");
            }

            if (LoginLockout.IsLocked(user, now))
            {
                return ServiceResult<AuthResponseDto>.Failure("AccountLocked", "The account is temporarily locked.");
            }

            var check = OtpManager.Verify(user, OtpPurpose.Login, verify2FaDto.Code, now);
            await _context.SaveChangesAsync(); // persists the failed-attempt counter, or the cleared code on success

            if (check != OtpCheckResult.Valid)
            {
                return OtpFailure<AuthResponseDto>(check);
            }

            _context.AuditLogs.Add(NewAuditLog(user.Id, "UserLoggedIn", $"User logged in with 2FA. Username: {user.Username}", ipAddress));
            await _context.SaveChangesAsync();

            return ServiceResult<AuthResponseDto>.Success(ToAuthResponse(user));
        }

        private static ServiceResult<T> OtpFailure<T>(OtpCheckResult check) => check == OtpCheckResult.TooManyAttempts
            ? ServiceResult<T>.Failure("TooManyOtpAttempts", "Too many wrong codes. Request a new code and try again.")
            : ServiceResult<T>.Failure("InvalidOrExpiredCode", "GeÃ§ersiz veya sÃ¼resi dolmuÅŸ doÄŸrulama kodu.");

        private static AuditLog NewAuditLog(Guid userId, string action, string details, string? ipAddress) => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Action = action,
            Details = details,
            IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? "unknown" : ipAddress,
            CreatedAt = DateTime.UtcNow
        };

        private AuthResponseDto ToAuthResponse(User user) => new()
        {
            Token = GenerateJwtToken(user),
            UserId = user.Id,
            Username = user.Username,
            Tckn = user.Tckn,
            FullName = user.FullName
        };
private string GenerateJwtToken(User user)
        {
            var tokenHandler = new JwtSecurityTokenHandler();

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim("tckn", user.Tckn)
            };

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddDays(7),
                Issuer = _jwtSettings.Issuer,
                Audience = _jwtSettings.Audience,
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(_jwtSettings.KeyBytes), SecurityAlgorithms.HmacSha256Signature)
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }

        private string GenerateAccountNumber() => "TR" + SecureRandom.Digits(16);

        private string GenerateCardNumber() => "4" + SecureRandom.Digits(15); // Visa
    }
}
