using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SmartBank.Tests.Api;

namespace SmartBank.Tests.Support
{
    /// <summary>Banking and market limits low enough to hit in a test; authentication is not limited (users are registered through it).</summary>
    public sealed class BankingLimitedApiFactory : ApiFactory
    {
        protected override IReadOnlyDictionary<string, string> ExtraSettings { get; } = new Dictionary<string, string>
        {
            ["RateLimiting:Banking:PermitLimit"] = "5",
            ["RateLimiting:Transfer:PermitLimit"] = "2",
            ["RateLimiting:Market:PermitLimit"] = "2"
        };
    }

    /// <summary>The auth and refresh limits low enough to hit in a test.</summary>
    public sealed class AuthLimitedApiFactory : ApiFactory
    {
        protected override IReadOnlyDictionary<string, string> ExtraSettings { get; } = new Dictionary<string, string>
        {
            ["RateLimiting:Auth:PermitLimit"] = "3",
            ["RateLimiting:Refresh:PermitLimit"] = "2"
        };
    }

    /// <summary>The credit-card charge and advance-period simulation switched off.</summary>
    public sealed class SimulationOffApiFactory : ApiFactory
    {
        protected override IReadOnlyDictionary<string, string> ExtraSettings { get; } = new Dictionary<string, string>
        {
            ["Demo:EnableSimulationEndpoints"] = "false"
        };
    }

    /// <summary>Demo mode: one-time codes also travel in the answer (never in anything that is stored).</summary>
    public sealed class DemoOtpApiFactory : ApiFactory
    {
        protected override IReadOnlyDictionary<string, string> ExtraSettings { get; } = new Dictionary<string, string>
        {
            ["Demo:ExposeOtp"] = "true"
        };
    }

    /// <summary>The application's clock is one the test moves by hand (starting at the real time, because the JWT middleware uses the real clock).</summary>
    public sealed class ClockedApiFactory : ApiFactory
    {
        public TestClock Clock { get; } = new(DateTimeOffset.UtcNow);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(Clock));
        }
    }
}
