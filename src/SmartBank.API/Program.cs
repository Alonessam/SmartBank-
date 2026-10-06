using Microsoft.AspNetCore.SignalR;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using SmartBank.API.Health;
using SmartBank.API.Hubs;
using SmartBank.API.Middlewares;
using SmartBank.API.Security;
using SmartBank.API.Services;
using SmartBank.Core.Common;
using SmartBank.Core.Interfaces;
using SmartBank.Core.Validators;
using SmartBank.Infrastructure.BackgroundServices;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Security;
using SmartBank.Infrastructure.Services;
using FluentValidation;

var builder = WebApplication.CreateBuilder(args);

// Card-data encryption key (base64, 32 bytes) comes from user-secrets / Encryption__Key, never from the repo.
EncryptionHelper.Configure(builder.Configuration["Encryption:Key"]);

// One clock for every business rule (OTP expiry, statement dates, standing-order schedule, token expiry): tests replace it.
builder.Services.TryAddSingleton(TimeProvider.System);

// Add DbContext (supports local SQL Server and cloud PostgreSQL). Which one is decided from the connection string;
// a missing or unreadable string stops the start with a clear message.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var databaseKind = DatabaseProviderSelector.Detect(connectionString);
builder.Services.AddDbContext<SmartBankDbContext>(options =>
{
    if (databaseKind == DatabaseKind.PostgreSql)
    {
        options.UseNpgsql(DatabaseProviderSelector.ToNpgsqlConnectionString(connectionString!));
    }
    else
    {
        options.UseSqlServer(connectionString);
    }

    // Ignore EF Core 9+ pending model changes warning to allow database migrations to run smoothly on startup
    options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

// Add JWT Authentication
// The signing key has no default on purpose: JwtSettings.From throws at startup if it is missing or weak.
var jwtSettings = JwtSettings.From(builder.Configuration);
var key = jwtSettings.KeyBytes;

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = true,
        ValidIssuer = jwtSettings.Issuer,
        ValidateAudience = true,
        ValidAudience = jwtSettings.Audience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
    };

    // Support JWT token in query string for SignalR WebSockets
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
});

// Register Core & Infrastructure Services
// One-time codes are delivered by e-mail; Demo:ExposeOtp (off by default) additionally exposes them for a mailbox-less demo.
// Transport: the Brevo HTTPS API when Brevo:ApiKey is set (free hosts such as Render block SMTP ports), otherwise plain SMTP.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Brevo:ApiKey"]))
{
    if (string.IsNullOrWhiteSpace(builder.Configuration["Brevo:SenderEmail"]))
    {
        throw new InvalidOperationException("Brevo:ApiKey is set but Brevo:SenderEmail is empty. Set it to the sender address you verified in Brevo.");
    }

    // The transport (and its HttpClient) lives as long as the app, so the handler recycles connections itself to pick up DNS changes.
    builder.Services.AddHttpClient<BrevoMailTransport>(client => client.Timeout = TimeSpan.FromSeconds(10))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
    builder.Services.AddSingleton<IMailTransport>(sp => sp.GetRequiredService<BrevoMailTransport>());
}
else
{
    builder.Services.AddSingleton<IMailTransport, SmtpMailTransport>();
}
builder.Services.AddSingleton<IOtpDelivery, EmailOtpDelivery>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IBankingService, BankingService>();
builder.Services.AddScoped<IChatService, ChatService>();

// Market rates: the HTTP client wrapped in an in-memory cache (decorator).
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<MarketRateService>();
builder.Services.AddScoped<IMarketRateService>(sp =>
    new CachedMarketRateService(sp.GetRequiredService<MarketRateService>(), sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

// AI: the chatbot is "FAQ retrieval, then Ollama, then Gemini, then a canned answer" (FailoverChatbotService). The FAQ search
// is ONE instance for the whole application: it reads its file and embeds its questions once, not for every message.
builder.Services.AddHttpClient<OllamaService>();
builder.Services.AddHttpClient<GeminiService>();
builder.Services.AddHttpClient("faq-embeddings");
builder.Services.AddSingleton<IRAGService>(sp => new RAGService(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("faq-embeddings"),
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILogger<RAGService>>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<IAIChatbotService, FailoverChatbotService>();
builder.Services.AddScoped<SupportAiResponder>();

builder.Services.AddHostedService<StandingOrderExecutionWorker>();
builder.Services.AddValidatorsFromAssemblyContaining<RegisterDtoValidator>();

// Add services to the container.
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        // A request that fails the attribute checks on its DTO (length, range, pattern, decimals) gets the same body shape as
        // every other error: { isSuccess, errorKey, message }, plus the standard "errors" list per field.
        options.InvalidModelStateResponseFactory = context =>
        {
            var errors = context.ModelState
                .Where(e => e.Value is { Errors.Count: > 0 })
                .ToDictionary(e => e.Key, e => e.Value!.Errors.Select(x => string.IsNullOrEmpty(x.ErrorMessage) ? "The value is not valid." : x.ErrorMessage).ToArray());

            return new BadRequestObjectResult(new
            {
                isSuccess = false,
                errorKey = "ValidationError",
                message = string.Join(" ", errors.Values.SelectMany(v => v)),
                errors
            });
        };
    });

// Chat messages are at most ChatLimits.MaxMessageLength characters; 16 KB per hub message is plenty and bounds abuse (default 32 KB).
// The filter refuses hub calls made after the access token has expired.
builder.Services.AddSingleton<HubTokenExpiryFilter>();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 16 * 1024;
    options.AddFilter<HubTokenExpiryFilter>();
});
builder.Services.AddSingleton(ChatSettings.From(builder.Configuration));
builder.Services.AddSingleton<ChatRateLimiter>();

// Rate limits. Every policy is a fixed window per partition; over the limit the answer is 429 with Retry-After.
//  auth     per client address: login, 2FA, password reset, register (the coarse layer; the per-account lockout and the
//           OTP attempt counter in the services are the precise ones)
//  refresh  per client address: silent token refresh and logout
//  banking  per signed-in user (address when anonymous): every banking endpoint
//  transfer per signed-in user: money-moving endpoints, stricter, replaces "banking" on those actions
//  market   per client address: the public rates list
// Behind a reverse proxy the address is only the real client's if forwarded headers are enabled (see Dockerfile).
// Counts live in memory of each instance (not shared between instances).
static string ClientKey(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
static string UserOrClientKey(HttpContext http) =>
    http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value is { Length: > 0 } id ? "user:" + id : "ip:" + ClientKey(http);

FixedWindowRateLimiterOptions Window(string name, int defaultLimit, int defaultSeconds) => new()
{
    PermitLimit = builder.Configuration.GetValue($"RateLimiting:{name}:PermitLimit", defaultLimit),
    Window = TimeSpan.FromSeconds(builder.Configuration.GetValue($"RateLimiting:{name}:WindowSeconds", defaultSeconds)),
    QueueLimit = 0
};

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(RateLimitPolicies.Auth, http => RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => Window("Auth", 10, 60)));
    options.AddPolicy(RateLimitPolicies.Refresh, http => RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => Window("Refresh", 60, 60)));
    options.AddPolicy(RateLimitPolicies.Banking, http => RateLimitPartition.GetFixedWindowLimiter(UserOrClientKey(http), _ => Window("Banking", 60, 60)));
    options.AddPolicy(RateLimitPolicies.Transfer, http => RateLimitPartition.GetFixedWindowLimiter("transfer:" + UserOrClientKey(http), _ => Window("Transfer", 10, 60)));
    options.AddPolicy(RateLimitPolicies.Market, http => RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => Window("Market", 60, 60)));

    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            IsSuccess = false,
            ErrorKey = "TooManyRequests",
            Message = "Too many requests. Please wait a moment and try again."
        }, cancellationToken);
    };
});

// Browsers may only call this API from the origins listed in configuration (Cors:AllowedOrigins, or the environment
// variable Cors__AllowedOrigins__0). The old policy accepted every origin together with credentials, which let any
// website a signed-in customer visited call the API with that customer's token.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
var isDevelopment = builder.Environment.IsDevelopment();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .SetIsOriginAllowed(origin => CorsOriginPolicy.IsAllowed(origin, allowedOrigins, isDevelopment))
              .AllowCredentials();
    });
});

// Liveness (/health) touches nothing; readiness (/health/ready) checks the database. Both answer only Healthy/Unhealthy.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "ready" });

// Lets services write the caller's real IP into the audit trail.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IClientInfo, HttpContextClientInfo>();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// HSTS: once a browser has seen this API over HTTPS it refuses plain HTTP for the next 180 days. (Only sent on HTTPS
// requests and never in Development; behind Render's proxy it needs the forwarded-headers setting from the Dockerfile.)
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(180);
    options.IncludeSubDomains = false; // onrender.com is shared: only this host
});

var app = builder.Build();

// AllowedHosts (the Host headers the app answers to) is read from configuration: set the environment variable
// AllowedHosts to "your-api.example.com" (semicolon-separated for several). "*" is the default so that a fresh deployment
// works, but in Production it is worth narrowing: say so in the log instead of leaving it silent.
var allowedHosts = app.Configuration["AllowedHosts"];
if (app.Environment.IsProduction() && (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts == "*"))
{
    app.Logger.LogWarning("AllowedHosts is \"*\": the API answers to any Host header. Set the AllowedHosts environment variable to this API's host name(s).");
}

app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseCors();

app.UseAuthentication();

// After authentication, so the per-user policies can see who is calling.
app.UseRateLimiter();

app.UseAuthorization();

app.MapControllers();
app.MapHub<SupportHub>("/hubs/support");

// Replaces the old /db-check (which returned the database exception message) and the /weatherforecast template.
// The database tables are created by the EF migrations / the SQL scripts in docs/deploy, not at startup.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();

// Lets the integration tests start the whole application (WebApplicationFactory<Program>).
public partial class Program { }
