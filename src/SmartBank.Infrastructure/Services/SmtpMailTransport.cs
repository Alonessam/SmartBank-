using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;

namespace SmartBank.Infrastructure.Services
{
    /// <summary>Plain SMTP, for local development or a host that allows outgoing SMTP. Does nothing when SmtpSettings:Host is empty.</summary>
    public sealed class SmtpMailTransport : IMailTransport
    {
        private readonly IConfiguration _configuration;

        public SmtpMailTransport(IConfiguration configuration) => _configuration = configuration;

        public async Task<bool> SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default)
        {
            var host = _configuration["SmtpSettings:Host"];
            if (string.IsNullOrWhiteSpace(host)) return false;

            int.TryParse(_configuration["SmtpSettings:Port"], out var port);
            var username = _configuration["SmtpSettings:Username"] ?? string.Empty;
            var password = _configuration["SmtpSettings:Password"] ?? string.Empty;
            var enableSsl = bool.TryParse(_configuration["SmtpSettings:EnableSsl"], out var ssl) && ssl;
            var from = _configuration["SmtpSettings:FromAddress"] ?? "no-reply@smartbank.com";

            using var message = new MailMessage
            {
                From = new MailAddress(from, "SmartBank Güvenlik"),
                Subject = mail.Subject,
                Body = mail.HtmlBody,
                IsBodyHtml = true
            };
            message.To.Add(new MailAddress(mail.ToAddress, mail.ToName));

            using var smtp = new SmtpClient(host, port == 0 ? 25 : port) { EnableSsl = enableSsl };
            if (!string.IsNullOrEmpty(username))
            {
                smtp.Credentials = new NetworkCredential(username, password);
            }

            await smtp.SendMailAsync(message, cancellationToken);
            return true;
        }
    }
}
