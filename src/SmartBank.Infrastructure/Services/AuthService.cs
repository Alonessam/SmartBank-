using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
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
        /// <summary>
        /// The one answer for "no such T.C. number", "wrong PIN" and "account locked". If a locked account answered differently,
        /// five wrong PINs would be a way to find out which T.C. numbers are registered.
        /// </summary>
        public const string InvalidCredentialsMessage = "Invalid T.C. Kimlik Numarası or password, or the account is temporarily locked.";

        // BCrypt hash used when the T.C. number is unknown (or the account is locked), so that case costs as much time as a
        // wrong PIN and response time does not reveal which T.C. numbers are registered.
        private static readonly string DummyPasswordHash = BCrypt.Net.BCrypt.HashPassword("timing-equaliser-not-a-real-pin");

        private static readonly TimeSpan PasswordResetCooldown = TimeSpan.FromSeconds(60);

        // A used refresh token is refused (not treated as theft) for this long, to tolerate two tabs refreshing at once.
        private static readonly TimeSpan RefreshReuseGrace = TimeSpan.FromSeconds(10);

        // Rotated, revoked and expired refresh tokens are kept this long (for the reuse check and the audit) and then removed.
        private static readonly TimeSpan TokenRetention = TimeSpan.FromDays(1);

        private readonly SmartBankDbContext _context;
        private readonly IOtpDelivery _otpDelivery;
        private readonly JwtSettings _jwtSettings;
        private readonly TimeProvider _time;

        public AuthService(SmartBankDbContext context, IConfiguration configuration, IOtpDelivery otpDelivery, TimeProvider? timeProvider = null)
        {
            _context = context;
            _otpDelivery = otpDelivery;
            _jwtSettings = JwtSettings.From(configuration);
            _time = timeProvider ?? TimeProvider.System;
        }

        private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

        private Task<ServiceResult<T>> WithRetryAsync<T>(Func<Task<ServiceResult<T>>> operation) =>
            ConcurrencyRetry.RunAsync(_context, operation, "Your request collided with another one. Please try again.");

        private static ServiceResult<T> InvalidCredentials<T>() => ServiceResult<T>.Failure("InvalidCredentials", InvalidCredentialsMessage);

        // ---- register -----------------------------------------------------------------------------------------

        public Task<ServiceResult<AuthResponseDto>> RegisterAsync(RegisterDto registerDto, string? ipAddress = null) =>
            WithRetryAsync(() => RegisterCoreAsync(registerDto, ipAddress));

        private async Task<ServiceResult<AuthResponseDto>> RegisterCoreAsync(RegisterDto registerDto, string? ipAddress)
        {
            var username = registerDto.Username;

            if (await _context.Users.AnyAsync(u => u.Username == username))
            {
                return ServiceResult<AuthResponseDto>.Failure("UsernameAlreadyExists", "Username is already taken.");
            }

            if (await _context.Users.AnyAsync(u => u.Tckn == registerDto.Tckn))
            {
                return ServiceResult<AuthResponseDto>.Failure("TcknAlreadyExists", "T.C. Kimlik Numarası is already registered.");
            }

            // The e-mail address is unique in the database. It is stored lower-case from now on; the comparison also lowers
            // the stored value, because older rows may have been saved with the case the user typed.
            var email = registerDto.Email.Trim().ToLowerInvariant();
            // ToLower() (not ToLowerInvariant) inside the query: only ToLower is translated to SQL LOWER(). The in-memory provider
            // accepts both, which is why only the real-database tests could catch this.
            if (await _context.Users.AnyAsync(u => u.Email.ToLower() == email))
            {
                return ServiceResult<AuthResponseDto>.Failure("EmailAlreadyExists", "This e-mail address is already registered.");
            }

            var fullName = $"{registerDto.FirstName} {registerDto.LastName}".Trim();
            if (fullName.Length > 100)
            {
                // The controller's validator refuses this earlier; the column holds 100 characters, so never let a 500 get this far.
                return ServiceResult<AuthResponseDto>.Failure("ValidationError", "First and last name together cannot exceed 99 characters.");
            }

            var now = UtcNow;
            var user = new User
            {
                Username = username,
                Tckn = registerDto.Tckn,
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(registerDto.Password),
                FirstName = registerDto.FirstName,
                LastName = registerDto.LastName,
                FullName = fullName,
                CreatedAt = now
            };

            // A default bank account with 1000 TRY for testing purposes.
            var accountCode = "ACC-" + SecureRandom.Next(1000000, 10000000);
            while (await _context.Accounts.AnyAsync(a => a.AccountCode == accountCode))
            {
                accountCode = "ACC-" + SecureRandom.Next(1000000, 10000000);
            }

            user.Accounts.Add(new Account
            {
                User = user,
                AccountNumber = "TR" + SecureRandom.Digits(16),
                AccountCode = accountCode,
                Balance = 1000.00m,
                Currency = Currencies.Try,
                CreatedAt = now,
                EncryptedCardNumber = EncryptionHelper.Encrypt(GenerateCardNumber()),
                ExpiryDate = CardFormat.ExpiryIn(now, CreditCardRules.DefaultCardValidityYears),
                CardTheme = "theme-neon-blue"
            });

            // A default credit card with a 10,000 TRY limit and 1,250 TRY debt, with a few demo transactions and one open
            // statement. No CVV is generated: the register response does not show card details and the CVV is never stored.
            var ccNumber = GenerateCardNumber();
            var card = new CreditCard
            {
                User = user,
                EncryptedCardNumber = EncryptionHelper.Encrypt(ccNumber),
                CardNumberHash = EncryptionHelper.HashCardNumber(ccNumber),
                ExpiryDate = CardFormat.ExpiryIn(now, CreditCardRules.DefaultCardValidityYears),
                CardLimit = CreditCardRules.DefaultLimit,
                CurrentDebt = 1250.00m,
                CardTheme = "theme-neon-blue",
                CreatedAt = now
            };

            card.Transactions.Add(new CreditCardTransaction { Description = "Market Harcaması", Amount = 450.00m, CreatedAt = now.AddDays(-10) });
            card.Transactions.Add(new CreditCardTransaction { Description = "Restoran Yemek Ödemesi", Amount = 300.00m, CreatedAt = now.AddDays(-8) });
            card.Transactions.Add(new CreditCardTransaction { Description = "Akaryakıt Harcaması", Amount = 500.00m, CreatedAt = now.AddDays(-6) });

            var cutoff = now.AddDays(-5);
            card.Statements.Add(new CreditCardStatement
            {
                PeriodName = CreditCardRules.PeriodName(cutoff),
                PeriodDebt = 1250.00m,
                MinimumPayment = CreditCardRules.MinimumPayment(1250.00m),
                PaidAmount = 0.00m,
                CutoffDate = cutoff,
                DueDate = cutoff.AddDays(CreditCardRules.DueDaysAfterCutoff),
                IsPaid = false
            });

            user.CreditCards.Add(card);
            _context.Users.Add(user);

            // The user graph is saved first and the audit row afterwards. An AuditLog has no navigation to its user, so EF is free
            // to insert it BEFORE the user in a shared SaveChanges; a database whose AuditLogs.UserId has a foreign key (the
            // hand-made production tables may) then rejects it, and registration failed for everybody (found on the live site).
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (DatabaseConflict.UniqueConstraintName(ex) is { } index)
            {
                // Two registrations with the same name, number or e-mail passed the checks above together; the unique index let one in.
                _context.ChangeTracker.Clear();
                return index switch
                {
                    var i when i.Contains("Username", StringComparison.OrdinalIgnoreCase) => ServiceResult<AuthResponseDto>.Failure("UsernameAlreadyExists", "Username is already taken."),
                    var i when i.Contains("Tckn", StringComparison.OrdinalIgnoreCase) => ServiceResult<AuthResponseDto>.Failure("TcknAlreadyExists", "T.C. Kimlik Numarası is already registered."),
                    var i when i.Contains("Email", StringComparison.OrdinalIgnoreCase) => ServiceResult<AuthResponseDto>.Failure("EmailAlreadyExists", "This e-mail address is already registered."),
                    _ => ServiceResult<AuthResponseDto>.Failure("AlreadyExists", "An account with these details already exists.")
                };
            }

            // The audit entry names the user but not the full T.C. number: an audit table is read by more people than the user table.
            // The user row exists now, so this row (saved together with the first refresh token) can reference it.
            _context.AuditLogs.Add(NewAuditLog(user.Id, "UserRegistered", $"User registered with Username: {user.Username}, Tckn: {TcKimlikNo.Mask(user.Tckn)}", ipAddress));

            return ServiceResult<AuthResponseDto>.Success(await IssueSessionAsync(user, ipAddress));
        }

        // ---- login --------------------------------------------------------------------------------------------

        public Task<ServiceResult<AuthResponseDto>> LoginAsync(LoginDto loginDto, string? ipAddress = null) =>
            WithRetryAsync(() => LoginCoreAsync(loginDto, ipAddress));

        private async Task<ServiceResult<AuthResponseDto>> LoginCoreAsync(LoginDto loginDto, string? ipAddress)
        {
            var now = UtcNow;
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Tckn == loginDto.Tckn);
            var locked = user != null && LoginLockout.IsLocked(user, now);

            // Always exactly one BCrypt verification: for an unknown T.C. number and for a locked account too, so response
            // time does not tell them apart from a wrong PIN. A locked account is rejected even if the PIN is right, so
            // guessing during a lockout is pointless, and attempts during a lockout do not extend it.
            var pinMatches = BCrypt.Net.BCrypt.Verify(loginDto.Password, user?.PasswordHash ?? DummyPasswordHash);

            if (user == null || locked || !pinMatches)
            {
                if (user != null && !locked)
                {
                    LoginLockout.RegisterFailure(user, now);
                    var lockedNow = LoginLockout.IsLocked(user, now);

                    // Failed logins never sign anyone out: anybody who knows a T.C. number could otherwise end the owner's sessions.
                    _context.AuditLogs.Add(NewAuditLog(user.Id, lockedNow ? "AccountLocked" : "LoginFailed",
                        lockedNow ? $"Account locked for {(int)LoginLockout.Duration.TotalMinutes} minutes after {LoginLockout.MaxFailedAttempts} failed logins." : "Wrong PIN.", ipAddress));
                    await _context.SaveChangesAsync();
                }

                return InvalidCredentials<AuthResponseDto>();
            }

            LoginLockout.Reset(user);

            if (user.TwoFactorEnabled)
            {
                var code = OtpManager.Issue(user, OtpPurpose.Login, now);
                await _context.SaveChangesAsync();

                _otpDelivery.Send(user, code, OtpPurpose.Login);

                var message = "İki aşamalı doğrulama gerekiyor.";
                if (_otpDelivery.ExposeCodeInResponse)
                {
                    message += $"|OTP:{code}"; // demo mode only, see IOtpDelivery.ExposeCodeInResponse
                }

                return ServiceResult<AuthResponseDto>.Failure("Requires2FA", message);
            }

            _context.AuditLogs.Add(NewAuditLog(user.Id, "UserLoggedIn", $"User logged in. Username: {user.Username}", ipAddress));
            await _context.SaveChangesAsync();

            return ServiceResult<AuthResponseDto>.Success(await IssueSessionAsync(user, ipAddress));
        }

        // ---- password reset -----------------------------------------------------------------------------------

        public Task<ServiceResult<bool>> RequestPasswordResetAsync(ForgotPasswordDto forgotPasswordDto, string? ipAddress = null) =>
            WithRetryAsync(() => RequestPasswordResetCoreAsync(forgotPasswordDto, ipAddress));

        private async Task<ServiceResult<bool>> RequestPasswordResetCoreAsync(ForgotPasswordDto forgotPasswordDto, string? ipAddress)
        {
            var now = UtcNow;
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Tckn == forgotPasswordDto.Tckn);

            // Nothing in the answer depends on whether the T.C. number exists. Nothing is sent when
            //  - a reset code was issued less than a minute ago (the cooldown: this public endpoint must not flood a mailbox), or
            //  - the user is in the middle of a login or transfer code: there is one pending slot, and a stranger must not be
            //    able to destroy the code the customer is typing in right now.
            if (user != null &&
                !OtpManager.IsCoolingDown(user, OtpPurpose.PasswordReset, now, PasswordResetCooldown) &&
                !OtpManager.HasLivePendingCodeForOtherPurpose(user, OtpPurpose.PasswordReset, now))
            {
                var code = OtpManager.Issue(user, OtpPurpose.PasswordReset, now);
                _context.AuditLogs.Add(NewAuditLog(user.Id, "PasswordResetRequested", "A password reset code was issued.", ipAddress));
                await _context.SaveChangesAsync();

                _otpDelivery.Send(user, code, OtpPurpose.PasswordReset);
            }

            return ServiceResult<bool>.Success(true);
        }

        public Task<ServiceResult<bool>> ResetPasswordAsync(ResetPasswordDto resetPasswordDto, string? ipAddress = null) =>
            WithRetryAsync(() => ResetPasswordCoreAsync(resetPasswordDto, ipAddress));

        private async Task<ServiceResult<bool>> ResetPasswordCoreAsync(ResetPasswordDto resetPasswordDto, string? ipAddress)
        {
            var now = UtcNow;
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Tckn == resetPasswordDto.Tckn);

            if (user == null)
            {
                BCrypt.Net.BCrypt.Verify(resetPasswordDto.NewPassword, DummyPasswordHash); // keep timing similar
                return ServiceResult<bool>.Failure("InvalidOrExpiredCode", "Geçersiz veya süresi dolmuş doğrulama kodu.");
            }

            var check = OtpManager.Verify(user, OtpPurpose.PasswordReset, resetPasswordDto.Code, now);
            if (check != OtpCheckResult.Valid)
            {
                await _context.SaveChangesAsync(); // persists the failed-attempt counter or the destroyed code
                return OtpFailure<bool>(check);
            }

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(resetPasswordDto.NewPassword);
            LoginLockout.Reset(user); // proving control of the mailbox also lifts a lockout
            await RevokeAllRefreshTokensAsync(user.Id, now); // whoever had a session (including a thief) is signed out

            _context.AuditLogs.Add(NewAuditLog(user.Id, "PasswordReset", $"User password reset. Username: {user.Username}", ipAddress));
            await _context.SaveChangesAsync();

            return ServiceResult<bool>.Success(true);
        }

        // ---- two-factor ---------------------------------------------------------------------------------------

        public Task<ServiceResult<bool>> Toggle2FaAsync(Guid userId, bool enable, string? password, string? ipAddress = null) =>
            WithRetryAsync(() => Toggle2FaCoreAsync(userId, enable, password, ipAddress));

        private async Task<ServiceResult<bool>> Toggle2FaCoreAsync(Guid userId, bool enable, string? password, string? ipAddress)
        {
            if (string.IsNullOrEmpty(password))
            {
                return ServiceResult<bool>.Failure("PinRequired", "Enter your PIN to change this setting.");
            }

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            {
                return ServiceResult<bool>.Failure("UserNotFound", "User details not found.");
            }

            var now = UtcNow;
            var locked = LoginLockout.IsLocked(user, now);
            var pinMatches = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);

            if (locked || !pinMatches)
            {
                // A wrong PIN here is a PIN guess like any other: it counts toward the lockout.
                if (!locked)
                {
                    LoginLockout.RegisterFailure(user, now);
                    _context.AuditLogs.Add(NewAuditLog(user.Id, "TwoFactorChangeRejected", "Wrong PIN while changing the two-factor setting.", ipAddress));
                    await _context.SaveChangesAsync();
                }

                return InvalidCredentials<bool>();
            }

            LoginLockout.Reset(user);
            user.TwoFactorEnabled = enable;
            _context.AuditLogs.Add(NewAuditLog(user.Id, enable ? "TwoFactorEnabled" : "TwoFactorDisabled",
                enable ? "Two-factor sign-in was turned on." : "Two-factor sign-in was turned off.", ipAddress));
            await _context.SaveChangesAsync();

            return ServiceResult<bool>.Success(enable);
        }

        public async Task<ServiceResult<bool>> Get2FaStatusAsync(Guid userId)
        {
            var enabled = await _context.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (bool?)u.TwoFactorEnabled).FirstOrDefaultAsync();
            if (enabled == null)
            {
                return ServiceResult<bool>.Failure("UserNotFound", "User details not found.");
            }

            return ServiceResult<bool>.Success(enabled.Value);
        }

        public Task<ServiceResult<AuthResponseDto>> Verify2FaAsync(Verify2FaDto verify2FaDto, string? ipAddress = null) =>
            WithRetryAsync(() => Verify2FaCoreAsync(verify2FaDto, ipAddress));

        private async Task<ServiceResult<AuthResponseDto>> Verify2FaCoreAsync(Verify2FaDto verify2FaDto, string? ipAddress)
        {
            var now = UtcNow;
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Tckn == verify2FaDto.Tckn);

            // Same answer for an unknown T.C. number, a locked account and a wrong code.
            if (user == null || LoginLockout.IsLocked(user, now))
            {
                return ServiceResult<AuthResponseDto>.Failure("InvalidOrExpiredCode", "Geçersiz veya süresi dolmuş doğrulama kodu.");
            }

            var check = OtpManager.Verify(user, OtpPurpose.Login, verify2FaDto.Code, now);
            await _context.SaveChangesAsync(); // persists the failed-attempt counter, or the cleared code on success

            if (check != OtpCheckResult.Valid)
            {
                return OtpFailure<AuthResponseDto>(check);
            }

            _context.AuditLogs.Add(NewAuditLog(user.Id, "UserLoggedIn", $"User logged in with 2FA. Username: {user.Username}", ipAddress));
            await _context.SaveChangesAsync();

            return ServiceResult<AuthResponseDto>.Success(await IssueSessionAsync(user, ipAddress));
        }

        // ---- sessions -----------------------------------------------------------------------------------------

        public async Task<ServiceResult<AuthResponseDto>> RefreshAsync(string refreshToken, string? ipAddress = null)
        {
            var now = UtcNow;
            var invalid = ServiceResult<AuthResponseDto>.Failure("InvalidRefreshToken", "The session has expired. Please sign in again.");

            var hash = HashRefreshToken(refreshToken);
            var stored = await _context.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (stored == null || stored.RevokedAt != null || stored.ExpiresAt <= now)
            {
                return invalid;
            }

            if (stored.UsedAt != null)
            {
                // A token is single-use. Within a few seconds this is a second tab or a retry that lost the response, so
                // it is simply refused. Later, somebody is replaying a copy: end the whole session family.
                if (now - stored.UsedAt.Value > RefreshReuseGrace)
                {
                    await RevokeFamilyAsync(stored.FamilyId, now);
                    _context.AuditLogs.Add(NewAuditLog(stored.UserId, "RefreshTokenReuse", "A used refresh token was presented again; the session family was revoked.", ipAddress));
                    await _context.SaveChangesAsync();
                }

                return invalid;
            }

            // The account lockout protects the PIN. It does not end sessions that already exist: a refresh token is
            // not a PIN guess, and anyone who knows a T.C. number could otherwise lock the owner out of an open session.
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId);
            if (user == null)
            {
                await RevokeFamilyAsync(stored.FamilyId, now);
                await _context.SaveChangesAsync();
                return invalid;
            }

            stored.UsedAt = now;
            try
            {
                var response = BuildResponse(user, now);
                response.RefreshToken = AddRefreshToken(user.Id, stored.FamilyId, ipAddress, now);
                await _context.SaveChangesAsync();
                return ServiceResult<AuthResponseDto>.Success(response);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request used the same token at the same moment and won.
                return invalid;
            }
        }

        public async Task LogoutAsync(string refreshToken, string? ipAddress = null)
        {
            var hash = HashRefreshToken(refreshToken);
            var stored = await _context.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (stored == null) return;

            await RevokeFamilyAsync(stored.FamilyId, UtcNow);
            _context.AuditLogs.Add(NewAuditLog(stored.UserId, "UserLoggedOut", "The session was ended by the user.", ipAddress));
            await _context.SaveChangesAsync();
        }

        private static ServiceResult<T> OtpFailure<T>(OtpCheckResult check) => check == OtpCheckResult.TooManyAttempts
            ? ServiceResult<T>.Failure("TooManyOtpAttempts", "Too many wrong codes. Request a new code and try again.")
            : ServiceResult<T>.Failure("InvalidOrExpiredCode", "Geçersiz veya süresi dolmuş doğrulama kodu.");

        private AuditLog NewAuditLog(Guid userId, string action, string details, string? ipAddress) => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Action = action,
            Details = details,
            IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? "unknown" : ipAddress,
            CreatedAt = UtcNow
        };

        /// <summary>Signs the user in: a short-lived access token plus the first refresh token of a new session family.</summary>
        private async Task<AuthResponseDto> IssueSessionAsync(User user, string? ipAddress)
        {
            var now = UtcNow;

            // Housekeeping instead of a background job: drop this user's tokens that expired, were rotated or were revoked
            // more than a day ago (each refresh leaves a used token behind, so they would otherwise pile up).
            var cutoff = now - TokenRetention;
            if (_context.SupportsBulkOperations())
            {
                await _context.RefreshTokens
                    .Where(t => t.UserId == user.Id && (t.ExpiresAt < cutoff || (t.UsedAt != null && t.UsedAt < cutoff) || (t.RevokedAt != null && t.RevokedAt < cutoff)))
                    .ExecuteDeleteAsync();
            }
            else
            {
                var stale = await _context.RefreshTokens
                    .Where(t => t.UserId == user.Id && (t.ExpiresAt < cutoff || (t.UsedAt != null && t.UsedAt < cutoff) || (t.RevokedAt != null && t.RevokedAt < cutoff)))
                    .ToListAsync();
                _context.RefreshTokens.RemoveRange(stale);
            }

            var response = BuildResponse(user, now);
            response.RefreshToken = AddRefreshToken(user.Id, Guid.NewGuid(), ipAddress, now);
            await _context.SaveChangesAsync();
            return response;
        }

        private AuthResponseDto BuildResponse(User user, DateTime now)
        {
            var token = GenerateJwtToken(user, now, out var accessTokenExpiresAt);
            return new AuthResponseDto
            {
                Token = token,
                AccessTokenExpiresAt = accessTokenExpiresAt,
                UserId = user.Id,
                Username = user.Username,
                FullName = user.FullName,
                Role = user.Role.ToString()
            };
        }

        /// <summary>Creates a refresh token row and returns the raw value, which only the client will ever see.</summary>
        private string AddRefreshToken(Guid userId, Guid familyId, string? ipAddress, DateTime now)
        {
            var raw = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

            _context.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                FamilyId = familyId,
                TokenHash = HashRefreshToken(raw),
                CreatedAt = now,
                ExpiresAt = now.Add(_jwtSettings.RefreshTokenLifetime),
                CreatedByIp = string.IsNullOrWhiteSpace(ipAddress) ? "unknown" : ipAddress
            });

            return raw;
        }

        // The token is 256 random bits, so a plain SHA-256 is enough (no salt or slow hash needed, unlike a PIN).
        private static string HashRefreshToken(string raw) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

        private async Task RevokeFamilyAsync(Guid familyId, DateTime now)
        {
            if (_context.SupportsBulkOperations())
            {
                // The version is bumped like a normal update, so a refresh that is running at the same moment still notices.
                await _context.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.Version, t => t.Version + 1));
                return;
            }

            var live = await _context.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync();
            foreach (var token in live) token.RevokedAt = now;
        }

        private async Task RevokeAllRefreshTokensAsync(Guid userId, DateTime now)
        {
            if (_context.SupportsBulkOperations())
            {
                await _context.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.Version, t => t.Version + 1));
                return;
            }

            var live = await _context.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync();
            foreach (var token in live) token.RevokedAt = now;
        }

        private string GenerateJwtToken(User user, DateTime now, out DateTime expires)
        {
            var tokenHandler = new JwtSecurityTokenHandler();

            // Only what the API needs to authorise a request. The T.C. number is not in the token (a token is readable by
            // anyone who holds it, and the number is half of the credentials).
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role.ToString())
            };

            expires = now.Add(_jwtSettings.AccessTokenLifetime);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                IssuedAt = now,
                NotBefore = now,
                Expires = expires,
                Issuer = _jwtSettings.Issuer,
                Audience = _jwtSettings.Audience,
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(_jwtSettings.KeyBytes), SecurityAlgorithms.HmacSha256Signature)
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }

        private static string GenerateCardNumber() => "4" + SecureRandom.Digits(15); // Visa
    }
}
