using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartBank.Core.Entities;
using SmartBank.Infrastructure.Services;

namespace SmartBank.Tests
{
    public class EmailDeliveryTests
    {
        private sealed class CapturingHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _body;

            public CapturingHandler(HttpStatusCode status, string body = "{}")
            {
                _status = status;
                _body = body;
            }

            public HttpRequestMessage? Request { get; private set; }
            public string? RequestBody { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Request = request;
                RequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
            }
        }

        private sealed class ScriptedTransport : IMailTransport
        {
            private readonly Func<OutgoingMail, Task<bool>> _send;
            public ScriptedTransport(Func<OutgoingMail, Task<bool>> send) => _send = send;
            public List<OutgoingMail> Mails { get; } = new();

            public Task<bool> SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default)
            {
                Mails.Add(mail);
                return _send(mail);
            }
        }

        private static IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

        private static BrevoMailTransport Brevo(CapturingHandler handler) =>
            new(new HttpClient(handler), Config(("Brevo:ApiKey", "test-key"), ("Brevo:SenderEmail", "sender@example.com")));

        private static readonly OutgoingMail SampleMail = new("customer@example.com", "Ayşe Yılmaz", "Konu", "<p>123456</p>");

        [Fact]
        public async Task Brevo_posts_the_mail_to_the_transactional_endpoint_with_the_api_key_header()
        {
            var handler = new CapturingHandler(HttpStatusCode.Created, "{\"messageId\":\"<x@y>\"}");

            var sent = await Brevo(handler).SendAsync(SampleMail);

            Assert.True(sent);
            Assert.Equal(HttpMethod.Post, handler.Request!.Method);
            Assert.Equal(BrevoMailTransport.Endpoint, handler.Request.RequestUri!.ToString());
            Assert.Equal("test-key", handler.Request.Headers.GetValues("api-key").Single());

            using var json = JsonDocument.Parse(handler.RequestBody!);
            var root = json.RootElement;
            Assert.Equal("sender@example.com", root.GetProperty("sender").GetProperty("email").GetString());
            Assert.Equal("customer@example.com", root.GetProperty("to")[0].GetProperty("email").GetString());
            Assert.Equal("Konu", root.GetProperty("subject").GetString());
            Assert.Equal("<p>123456</p>", root.GetProperty("htmlContent").GetString());
        }

        [Fact]
        public async Task Brevo_rejection_throws_with_the_status_and_error_code_but_without_the_body()
        {
            var handler = new CapturingHandler(HttpStatusCode.Unauthorized, "{\"code\":\"unauthorized\",\"message\":\"Key not found for customer@example.com\"}");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Brevo(handler).SendAsync(SampleMail));

            Assert.Contains("401", ex.Message);
            Assert.Contains("unauthorized", ex.Message);
            Assert.DoesNotContain("customer@example.com", ex.Message);
        }

        [Theory]
        [InlineData("", "sender@example.com")]
        [InlineData("key", "")]
        [InlineData("", "")]
        public void Brevo_needs_both_an_api_key_and_a_sender_address(string apiKey, string sender)
        {
            Assert.Throws<InvalidOperationException>(() =>
                new BrevoMailTransport(new HttpClient(new CapturingHandler(HttpStatusCode.Created)),
                    Config(("Brevo:ApiKey", apiKey), ("Brevo:SenderEmail", sender))));
        }

        [Fact]
        public async Task Smtp_transport_does_nothing_when_no_host_is_configured()
        {
            var sent = await new SmtpMailTransport(Config()).SendAsync(SampleMail);

            Assert.False(sent);
        }

        [Fact]
        public void The_mail_names_the_purpose_contains_the_code_and_encodes_the_users_name()
        {
            var mail = EmailOtpDelivery.BuildMail("a@example.com", "<script>alert(1)</script>", "654321", OtpPurpose.PasswordReset);

            Assert.Equal("a@example.com", mail.ToAddress);
            Assert.Contains("654321", mail.HtmlBody);
            Assert.Contains("Şifre", mail.Subject);
            Assert.DoesNotContain("<script>", mail.HtmlBody);
            Assert.Contains("&lt;script&gt;", mail.HtmlBody);
        }

        [Theory]
        [InlineData(OtpPurpose.Login)]
        [InlineData(OtpPurpose.Transfer)]
        [InlineData(OtpPurpose.PasswordReset)]
        public void Every_purpose_has_its_own_subject(OtpPurpose purpose)
        {
            var mail = EmailOtpDelivery.BuildMail("a@example.com", "Ali", "111111", purpose);

            Assert.False(string.IsNullOrWhiteSpace(mail.Subject));
        }

        [Fact]
        public async Task A_failing_transport_never_throws_out_of_delivery()
        {
            var transport = new ScriptedTransport(_ => throw new HttpRequestException("provider down"));
            var delivery = new EmailOtpDelivery(Config(), transport, NullLogger<EmailOtpDelivery>.Instance);

            await delivery.DeliverAsync(SampleMail, OtpPurpose.Login, Guid.NewGuid());

            Assert.Single(transport.Mails);
        }

        [Fact]
        public async Task A_missing_transport_configuration_is_not_an_error()
        {
            var transport = new ScriptedTransport(_ => Task.FromResult(false));
            var delivery = new EmailOtpDelivery(Config(), transport, NullLogger<EmailOtpDelivery>.Instance);

            await delivery.DeliverAsync(SampleMail, OtpPurpose.Login, Guid.NewGuid());

            Assert.Single(transport.Mails);
        }

        [Fact]
        public async Task Send_hands_the_mail_to_the_transport_in_the_background()
        {
            var delivered = new TaskCompletionSource<OutgoingMail>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new ScriptedTransport(mail => { delivered.TrySetResult(mail); return Task.FromResult(true); });
            var delivery = new EmailOtpDelivery(Config(), transport, NullLogger<EmailOtpDelivery>.Instance);
            var user = new User { Id = Guid.NewGuid(), Email = "ali@example.com", FirstName = "Ali", LastName = "Veli" };

            delivery.Send(user, "246810", OtpPurpose.Login);

            var mail = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("ali@example.com", mail.ToAddress);
            Assert.Contains("246810", mail.HtmlBody);
        }

        [Fact]
        public void Demo_mode_is_off_unless_explicitly_enabled()
        {
            var transport = new ScriptedTransport(_ => Task.FromResult(true));

            Assert.False(new EmailOtpDelivery(Config(), transport, NullLogger<EmailOtpDelivery>.Instance).ExposeCodeInResponse);
            Assert.True(new EmailOtpDelivery(Config(("Demo:ExposeOtp", "true")), transport, NullLogger<EmailOtpDelivery>.Instance).ExposeCodeInResponse);
        }
    }
}
