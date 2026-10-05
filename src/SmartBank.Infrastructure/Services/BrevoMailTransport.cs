using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SmartBank.Infrastructure.Services
{
    /// <summary>
    /// Sends mail through Brevo's HTTPS API. Used instead of SMTP because free hosts such as Render block the outgoing SMTP
    /// ports (25, 465, 587) but allow HTTPS.
    /// Configuration: Brevo:ApiKey (secret), Brevo:SenderEmail (must be a sender verified in Brevo), Brevo:SenderName.
    /// </summary>
    public sealed class BrevoMailTransport : IMailTransport
    {
        public const string Endpoint = "https://api.brevo.com/v3/smtp/email";

        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _senderEmail;
        private readonly string _senderName;

        public BrevoMailTransport(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _apiKey = configuration["Brevo:ApiKey"] ?? string.Empty;
            _senderEmail = configuration["Brevo:SenderEmail"] ?? string.Empty;
            _senderName = configuration["Brevo:SenderName"] is { Length: > 0 } name ? name : "SmartBank Güvenlik";

            if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_senderEmail))
            {
                throw new InvalidOperationException("Brevo:ApiKey and Brevo:SenderEmail must both be set to use the Brevo mail transport.");
            }
        }

        public async Task<bool> SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default)
        {
            var payload = new
            {
                sender = new { name = _senderName, email = _senderEmail },
                to = new[] { new { email = mail.ToAddress, name = mail.ToName } },
                subject = mail.Subject,
                htmlContent = mail.HtmlBody
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(payload) };
            request.Headers.Add("api-key", _apiKey);
            request.Headers.Add("accept", "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode) return true;

            // Report Brevo's error code (e.g. "unauthorized", "invalid_parameter") but not the body: it can echo the address.
            throw new HttpRequestException($"Brevo rejected the mail: HTTP {(int)response.StatusCode} {await ReadErrorCodeAsync(response, cancellationToken)}");
        }

        private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                using var json = JsonDocument.Parse(body);
                return json.RootElement.TryGetProperty("code", out var code) ? code.ToString() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
