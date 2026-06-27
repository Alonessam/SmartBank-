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
using SmartBank.Infrastructure.Data;

namespace SmartBank.Infrastructure.Services
{
    public class AuthService : IAuthService
    {
        private readonly SmartBankDbContext _context;
        private readonly IConfiguration _configuration;

        public AuthService(SmartBankDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
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
            var cvv = GenerateCvv();

            var accountCode = "ACC-" + new Random().Next(1000000, 9999999).ToString();
            while (await _context.Accounts.AnyAsync(a => a.AccountCode == accountCode))
            {
                accountCode = "ACC-" + new Random().Next(1000000, 9999999).ToString();
            }

            var defaultAccount = new Account
            {
                User = user,
                AccountNumber = GenerateAccountNumber(),
                AccountCode = accountCode,
                Balance = 1000.00m,
                Currency = "TRY",
                EncryptedCardNumber = SmartBank.Core.Common.EncryptionHelper.Encrypt(cardNum),
                EncryptedCardCvv = SmartBank.Core.Common.EncryptionHelper.Encrypt(cvv),
                CardTheme = "theme-neon-blue"
            };

            user.Accounts.Add(defaultAccount);

            // Automatically create a default Credit Card with 10,000 TRY limit and 1,250 TRY debt
            var ccNumber = GenerateCardNumber();
            var ccCvv = GenerateCvv();
            var defaultCreditCard = new CreditCard
            {
                User = user,
                EncryptedCardNumber = SmartBank.Core.Common.EncryptionHelper.Encrypt(ccNumber),
                EncryptedCardCvv = SmartBank.Core.Common.EncryptionHelper.Encrypt(ccCvv),
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

        public async Task<ServiceResult<AuthResponseDto>> LoginAsync(LoginDto loginDto)
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Tckn == loginDto.Tckn);

            if (user == null || !BCrypt.Net.BCrypt.Verify(loginDto.Password, user.PasswordHash))
            {
                return ServiceResult<AuthResponseDto>.Failure("InvalidCredentials", "Invalid T.C. Kimlik Numarası or password.");
            }

            // Check if 2FA is enabled for this user
            if (user.TwoFactorEnabled)
            {
                var random = new Random();
                var otp = random.Next(100000, 1000000).ToString();
                
                user.TwoFactorSecret = otp;
                user.TwoFactorExpiry = DateTime.UtcNow.AddMinutes(5);

                _context.Users.Update(user);
                await _context.SaveChangesAsync();

                // Send 2FA email in background
                _ = Task.Run(async () => {
                    await SendAuth2FaEmailAsync(user.Email, user.FullName, otp);
                });

                Console.WriteLine($"[SmartBank Login 2FA] Generated OTP: {otp} for user {user.Username}");

                // Return failure with Requires2FA key and simulated OTP inside message
                return ServiceResult<AuthResponseDto>.Failure("Requires2FA", $"İki aşamalı doğrulama gerekiyor.|OTP:{otp}");
            }

            var token = GenerateJwtToken(user);

            // Write Audit Log
            var audit = new AuditLog
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Action = "UserLoggedIn",
                Details = $"User logged in. Username: {user.Username}",
                IpAddress = "127.0.0.1",
                CreatedAt = DateTime.UtcNow
            };
            _context.AuditLogs.Add(audit);
            await _context.SaveChangesAsync();

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

        public async Task<ServiceResult<bool>> ForgotPasswordAsync(ForgotPasswordDto forgotPasswordDto)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Tckn == forgotPasswordDto.Tckn);
            if (user == null)
            {
                return ServiceResult<bool>.Failure("TcknNotFound", "T.C. Kimlik Numarası is not registered.");
            }

            var passwordHash = BCrypt.Net.BCrypt.HashPassword(forgotPasswordDto.NewPassword);
            user.PasswordHash = passwordHash;

            _context.Users.Update(user);
            await _context.SaveChangesAsync();

            // Write Audit Log
            var audit = new AuditLog
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Action = "PasswordReset",
                Details = $"User password reset. Username: {user.Username}",
                IpAddress = "127.0.0.1",
                CreatedAt = DateTime.UtcNow
            };
            _context.AuditLogs.Add(audit);
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

        public async Task<ServiceResult<AuthResponseDto>> Verify2FaAsync(Verify2FaDto verify2FaDto)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Tckn == verify2FaDto.Tckn);
            if (user == null)
            {
                return ServiceResult<AuthResponseDto>.Failure("UserNotFound", "Kullanıcı bulunamadı.");
            }

            if (string.IsNullOrEmpty(user.TwoFactorSecret) || 
                user.TwoFactorSecret != verify2FaDto.Code || 
                !user.TwoFactorExpiry.HasValue || 
                user.TwoFactorExpiry.Value < DateTime.UtcNow)
            {
                return ServiceResult<AuthResponseDto>.Failure("InvalidOrExpiredCode", "Geçersiz veya süresi dolmuş doğrulama kodu.");
            }

            // Clear OTP after success
            user.TwoFactorSecret = null;
            user.TwoFactorExpiry = null;
            _context.Users.Update(user);
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

        private async Task SendAuth2FaEmailAsync(string emailAddress, string username, string otpCode)
        {
            try
            {
                var smtpHost = _configuration["SmtpSettings:Host"] ?? "localhost";
                var smtpPortStr = _configuration["SmtpSettings:Port"] ?? "25";
                int.TryParse(smtpPortStr, out var smtpPort);
                var smtpUsername = _configuration["SmtpSettings:Username"] ?? "";
                var smtpPassword = _configuration["SmtpSettings:Password"] ?? "";
                var enableSsl = bool.Parse(_configuration["SmtpSettings:EnableSsl"] ?? "false");
                var fromAddress = _configuration["SmtpSettings:FromAddress"] ?? "no-reply@smartbank.com";

                using (var mail = new System.Net.Mail.MailMessage())
                {
                    mail.From = new System.Net.Mail.MailAddress(fromAddress, "SmartBank Güvenlik");
                    mail.To.Add(emailAddress);
                    mail.Subject = "SmartBank Giriş Doğrulama Kodu";
                    
                    mail.Body = $@"
                    <html>
                    <body style='font-family: Arial, sans-serif; background-color: #0d1b2a; color: #e0e1dd; padding: 2rem;'>
                        <div style='max-width: 600px; margin: 0 auto; background-color: #1b263b; border-radius: 12px; border: 1px solid #415a77; padding: 2rem;'>
                            <h2 style='color: #00f260; text-align: center; font-size: 1.8rem; margin-top: 0;'>❖ SmartBank Giriş Doğrulaması</h2>
                            <p style='font-size: 1.1rem;'>Merhaba <strong>{username}</strong>,</p>
                            <p style='font-size: 1.1rem; line-height: 1.6;'>SmartBank hesabınıza güvenli giriş yapmak için aşağıdaki 6 haneli doğrulama kodunu kullanın:</p>
                            <div style='text-align: center; margin: 2rem 0;'>
                                <span style='font-size: 2.2rem; font-weight: bold; background-color: #0d1b2a; color: #00f260; padding: 0.75rem 2rem; border-radius: 8px; letter-spacing: 5px; border: 1px solid #415a77;'>{otpCode}</span>
                            </div>
                            <p style='color: #a3b18a; font-size: 0.9rem; line-height: 1.6;'>Bu kod 5 dakika boyunca geçerlidir. Giriş talebi size ait değilse lütfen hemen müşteri hizmetlerimizle iletişime geçiniz.</p>
                            <hr style='border: 0; border-top: 1px solid #415a77; margin: 2rem 0;' />
                            <p style='font-size: 0.8rem; text-align: center; color: #a3b18a;'>SmartBank A.Ş. &copy; {DateTime.UtcNow.Year}</p>
                        </div>
                    </body>
                    </html>";
                    mail.IsBodyHtml = true;

                    using (var smtp = new System.Net.Mail.SmtpClient(smtpHost, smtpPort))
                    {
                        if (!string.IsNullOrEmpty(smtpUsername))
                        {
                            smtp.Credentials = new System.Net.NetworkCredential(smtpUsername, smtpPassword);
                        }
                        smtp.EnableSsl = enableSsl;
                        
                        await smtp.SendMailAsync(mail);
                    }
                }
                Console.WriteLine($"[SmartBank Login 2FA Email] Real verification email successfully sent to {emailAddress}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SmartBank Login 2FA Email Error] Failed to send email to {emailAddress}: {ex.Message}");
            }
        }

        private string GenerateJwtToken(User user)
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            
            var keyString = _configuration["JwtSettings:Key"];
            if (string.IsNullOrEmpty(keyString))
            {
                // Fallback key for development if not configured
                keyString = "SuperSecretKeyForDevelopmentSmartBankSupportMesh2026";
            }
            var key = Encoding.ASCII.GetBytes(keyString);

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
                Issuer = _configuration["JwtSettings:Issuer"] ?? "SmartBankAPI",
                Audience = _configuration["JwtSettings:Audience"] ?? "SmartBankApp",
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }

        private string GenerateAccountNumber()
        {
            var random = new Random();
            var sb = new StringBuilder("TR");
            for (int i = 0; i < 16; i++)
            {
                sb.Append(random.Next(0, 10));
            }
            return sb.ToString();
        }
        private string GenerateCardNumber()
        {
            var random = new Random();
            var sb = new StringBuilder("4"); // Visa
            for (int i = 0; i < 15; i++)
            {
                sb.Append(random.Next(0, 10));
            }
            return sb.ToString();
        }

        private string GenerateCvv()
        {
            var random = new Random();
            return random.Next(100, 1000).ToString();
        }
    }
}
