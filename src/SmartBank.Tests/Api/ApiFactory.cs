using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SmartBank.Core.Entities;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Services;
using SmartBank.Tests.Support;

namespace SmartBank.Tests.Api
{
    /// <summary>
    /// Starts the real application (all middleware, authentication, authorization, controllers and the SignalR hub) in
    /// memory against an in-memory database. Everything is exercised over HTTP/SignalR the way a client would,
    /// including registering users and logging in, so the tests check the pipeline and not just single classes.
    /// </summary>
    public class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString();
        private int _counter;

        /// <summary>Settings a test class can add or override (for example a very low rate limit). Applied last.</summary>
        protected virtual IReadOnlyDictionary<string, string> ExtraSettings { get; } = new Dictionary<string, string>();

        /// <summary>False only for the test that checks the real AI wiring of Program.cs.</summary>
        protected virtual bool UseFakeAi => true;

        /// <summary>The AI "model" of this application instance: the test decides what it says and sees what it was asked.</summary>
        public FakeAiChatbot Ai => Services.GetRequiredService<FakeAiChatbot>();

        /// <summary>The market prices of this application instance.</summary>
        public FakeMarketRates Rates => Services.GetRequiredService<FakeMarketRates>();

        public sealed record TestUser(Guid Id, string Username, string Tckn, string Token, string Role);

        private sealed record AuthBody(string Token, Guid UserId, string Username, string Role);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Neither "Development" nor "Production": no developer-only shortcuts, no user-secrets.
            builder.UseEnvironment("Testing");

            // Program.cs reads these while it is still building the host (before ConfigureAppConfiguration would be
            // applied), so they are supplied as host settings.
            builder.UseSetting("JwtSettings:Key", "integration-test-signing-key-0123456789-abcdef");
            builder.UseSetting("Encryption:Key", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("RateLimiting:Auth:PermitLimit", "100000");
            builder.UseSetting("RateLimiting:Refresh:PermitLimit", "100000");
            builder.UseSetting("RateLimiting:Banking:PermitLimit", "100000");
            builder.UseSetting("RateLimiting:Transfer:PermitLimit", "100000");
            builder.UseSetting("RateLimiting:Market:PermitLimit", "100000");
            builder.UseSetting("ConnectionStrings:DefaultConnection", "Server=unused;Database=unused");
            foreach (var (key, value) in ExtraSettings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                // Swap the real database for an in-memory one. EF Core keeps the provider configuration in two places.
                services.RemoveAll<DbContextOptions<SmartBankDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<SmartBankDbContext>>();
                services.AddDbContext<SmartBankDbContext>(options => options
                    .UseInMemoryDatabase(_databaseName)
                    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

                // The standing-order worker has its own tests and would only add noise here.
                services.RemoveAll<IHostedService>();

                // Nothing in these tests may reach the network or a local Ollama: the AI, the FAQ search and the market prices are fakes.
                if (UseFakeAi)
                {
                    services.RemoveAll<IAIChatbotService>();
                    services.RemoveAll<OllamaService>();
                    services.RemoveAll<GeminiService>();
                    services.RemoveAll<IRAGService>();
                    services.RemoveAll<IMarketRateService>();
                    services.AddSingleton<FakeAiChatbot>();
                    services.AddSingleton<IAIChatbotService>(sp => sp.GetRequiredService<FakeAiChatbot>());
                    services.AddSingleton<IRAGService, FakeRagService>();
                    services.AddSingleton<FakeMarketRates>();
                    services.AddSingleton<IMarketRateService>(sp => sp.GetRequiredService<FakeMarketRates>());
                }
            });
        }

        public HttpClient ClientFor(TestUser? user)
        {
            var client = CreateClient();
            if (user != null)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
            }

            return client;
        }

        /// <summary>Registers a customer through the real endpoint.</summary>
        public async Task<TestUser> RegisterCustomerAsync(string? username = null)
        {
            var n = Interlocked.Increment(ref _counter);
            var tckn = TestTckn.Next(); // registration checks the check digits
            username ??= $"customer{n}";

            using var client = CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/register", new
            {
                username,
                tckn,
                password = "123456",
                firstName = "Test",
                lastName = "User",
                email = $"user{n}@example.com"
            });
            response.EnsureSuccessStatusCode();

            var body = (await response.Content.ReadFromJsonAsync<AuthBody>())!;
            return new TestUser(body.UserId, body.Username, tckn, body.Token, body.Role);
        }

        /// <summary>
        /// A support agent. No endpoint can create one, so this registers a customer, promotes it directly in the
        /// database (what an administrator does with SQL) and signs in again to get a token that carries the role.
        /// </summary>
        public async Task<TestUser> RegisterAgentAsync(string? username = null)
        {
            var customer = await RegisterCustomerAsync(username ?? "support_staff_" + Guid.NewGuid().ToString("N")[..8]);

            using (var scope = Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SmartBankDbContext>();
                var user = await db.Users.SingleAsync(u => u.Id == customer.Id);
                user.Role = UserRole.Agent;
                await db.SaveChangesAsync();
            }

            using var client = CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/login", new { tckn = customer.Tckn, password = "123456" });
            response.EnsureSuccessStatusCode();
            var body = (await response.Content.ReadFromJsonAsync<AuthBody>())!;
            return customer with { Token = body.Token, Role = body.Role };
        }

        /// <summary>A chat session owned by <paramref name="owner"/> with one message in it.</summary>
        public async Task<Guid> CreateChatSessionAsync(TestUser owner, string firstMessage)
        {
            using var scope = Services.CreateScope();
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            var session = (await chat.CreateSessionAsync(owner.Id, "Question about my card")).Data!;
            await chat.AddMessageAsync(session.Id, "User", firstMessage);
            return session.Id;
        }

        public async Task<Guid> GetCreditCardIdAsync(TestUser owner)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartBankDbContext>();
            return await db.CreditCards.AsNoTracking().Where(c => c.UserId == owner.Id).Select(c => c.Id).FirstAsync();
        }

        public async Task<Account> GetFirstAccountAsync(TestUser owner)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartBankDbContext>();
            return await db.Accounts.AsNoTracking().FirstAsync(a => a.UserId == owner.Id);
        }
    }
}
