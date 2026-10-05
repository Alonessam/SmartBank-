using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Services;

namespace SmartBank.Tests.Api
{
    /// <summary>Checks the real dependency-injection wiring in Program.cs picks the right mail transport from configuration.</summary>
    public class EmailWiringTests
    {
        [Fact]
        public void Without_a_brevo_key_the_smtp_transport_is_used()
        {
            using var factory = new ApiFactory();

            using var scope = factory.Services.CreateScope();
            Assert.IsType<SmtpMailTransport>(scope.ServiceProvider.GetRequiredService<IMailTransport>());
            Assert.IsType<EmailOtpDelivery>(scope.ServiceProvider.GetRequiredService<IOtpDelivery>());
        }

        [Fact]
        public void With_a_brevo_key_and_sender_the_brevo_transport_is_used()
        {
            using var factory = new ApiFactory().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Brevo:ApiKey", "test-key");
                builder.UseSetting("Brevo:SenderEmail", "sender@example.com");
            });

            using var scope = factory.Services.CreateScope();
            Assert.IsType<BrevoMailTransport>(scope.ServiceProvider.GetRequiredService<IMailTransport>());
        }

        [Fact]
        public void A_brevo_key_without_a_sender_address_stops_the_app_from_starting()
        {
            using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseSetting("Brevo:ApiKey", "test-key"));

            var ex = Assert.ThrowsAny<Exception>(() => factory.Services);

            Assert.Contains("Brevo:SenderEmail", ex.ToString());
        }
    }
}
