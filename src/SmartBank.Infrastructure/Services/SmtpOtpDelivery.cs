using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;

namespace SmartBank.Infrastructure.Services
{
    public sealed class SmtpOtpDelivery : IOtpDelivery
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SmtpOtpDelivery> _logger;

        public bool ExposeCodeInResponse { get; }

        public SmtpOtpDelivery(IConfiguration configuration, ILogger<SmtpOtpDelivery> logger)
        {
            _configuration = configuration;
            _logger = logger;
            ExposeCodeInResponse = bool.TryParse(configuration["Demo:ExposeOtp"], out var expose) && expose;
        }

        public void Send(User user, string code, OtpPurpose purpose)
        {
            if (ExposeCodeInResponse)
            {
                _logger.LogWarning("DEMO MODE (Demo:ExposeOtp): {Purpose} code for user {UserId} is {Code}", purpose, user.Id, code);
            }

            // Capture plain values: the request (and its DbContext) may be gone by the time the mail is sent.
            var email = user.Email;
            var name = user.FullName;
            var userId = user.Id;

            _ = Task.Run(() => SendEmailAsync(email, name, code, purpose, userId));
        }

        private async Task SendEmailAsync(string emailAddress, string fullName, string code, OtpPurpose purpose, Guid userId)
        {
            try
            {
                var host = _configuration["SmtpSettings:Host"];
                if (string.IsNullOrWhiteSpace(host))
                {
                    _logger.LogInformation("SMTP is not configured; the {Purpose} code for user {UserId} was not e-mailed.", purpose, userId);
                    return;
                }

                int.TryParse(_configuration["SmtpSettings:Port"], out var port);
                var username = _configuration["SmtpSettings:Username"] ?? string.Empty;
                var password = _configuration["SmtpSettings:Password"] ?? string.Empty;
                var enableSsl = bool.TryParse(_configuration["SmtpSettings:EnableSsl"], out var ssl) && ssl;
                var from = _configuration["SmtpSettings:FromAddress"] ?? "no-reply@smartbank.com";

                var (subject, heading, intro) = Describe(purpose);

                using var mail = new MailMessage
                {
                    From = new MailAddress(from, "SmartBank Güvenlik"),
                    Subject = subject,
                    Body = BuildBody(WebUtility.HtmlEncode(fullName), heading, intro, code),
                    IsBodyHtml = true
                };
                mail.To.Add(emailAddress);

                using var smtp = new SmtpClient(host, port == 0 ? 25 : port) { EnableSsl = enableSsl };
                if (!string.IsNullOrEmpty(username))
                {
                    smtp.Credentials = new NetworkCredential(username, password);
                }

                await smtp.SendMailAsync(mail);
                _logger.LogInformation("{Purpose} code e-mailed to user {UserId}.", purpose, userId);
            }
            catch (Exception ex)
            {
                // Never log the code or the address, only that delivery failed.
                _logger.LogError(ex, "Could not e-mail the {Purpose} code to user {UserId}.", purpose, userId);
            }
        }

        private static (string Subject, string Heading, string Intro) Describe(OtpPurpose purpose) => purpose switch
        {
            OtpPurpose.Login => (
                "SmartBank Giriş Doğrulama Kodu",
                "SmartBank Giriş Doğrulaması",
                "SmartBank hesabınıza güvenli giriş yapmak için aşağıdaki 6 haneli doğrulama kodunu kullanın:"),
            OtpPurpose.Transfer => (
                "SmartBank Güvenlik Doğrulama Kodu",
                "SmartBank Güvenlik",
                "Hesabınızdan başlatılan para transferi işlemini onaylamak için aşağıdaki 6 haneli doğrulama kodunu kullanın:"),
            OtpPurpose.PasswordReset => (
                "SmartBank Şifre Sıfırlama Kodu",
                "SmartBank Şifre Sıfırlama",
                "Şifrenizi sıfırlamak için aşağıdaki 6 haneli doğrulama kodunu kullanın:"),
            _ => ("SmartBank Doğrulama Kodu", "SmartBank Güvenlik", "Doğrulama kodunuz:")
        };

        private static string BuildBody(string encodedName, string heading, string intro, string code) => $@"
<html>
<body style='font-family: Arial, sans-serif; background-color: #0d1b2a; color: #e0e1dd; padding: 2rem;'>
    <div style='max-width: 600px; margin: 0 auto; background-color: #1b263b; border-radius: 12px; border: 1px solid #415a77; padding: 2rem;'>
        <h2 style='color: #00f260; text-align: center; font-size: 1.8rem; margin-top: 0;'>❖ {heading}</h2>
        <p style='font-size: 1.1rem;'>Merhaba <strong>{encodedName}</strong>,</p>
        <p style='font-size: 1.1rem; line-height: 1.6;'>{intro}</p>
        <div style='text-align: center; margin: 2rem 0;'>
            <span style='font-size: 2.2rem; font-weight: bold; background-color: #0d1b2a; color: #00f260; padding: 0.75rem 2rem; border-radius: 8px; letter-spacing: 5px; border: 1px solid #415a77;'>{code}</span>
        </div>
        <p style='color: #a3b18a; font-size: 0.9rem; line-height: 1.6;'>Bu kod 5 dakika boyunca geçerlidir ve tek kullanımlıktır. İşlemi siz başlatmadıysanız bu e-postayı yok sayın ve kodu kimseyle paylaşmayın.</p>
        <hr style='border: 0; border-top: 1px solid #415a77; margin: 2rem 0;' />
        <p style='font-size: 0.8rem; text-align: center; color: #a3b18a;'>SmartBank A.Ş. &copy; {DateTime.UtcNow.Year}</p>
    </div>
</body>
</html>";
    }
}
