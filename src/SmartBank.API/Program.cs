using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SmartBank.API.Health;
using SmartBank.API.Hubs;
using SmartBank.API.Security;
using SmartBank.API.Services;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Services;
using SmartBank.Infrastructure.BackgroundServices;
using SmartBank.API.Middlewares;
using FluentValidation;
using SmartBank.Core.Validators;
using SmartBank.Core.Common;
using SmartBank.Core.Entities;
using SmartBank.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

// Card-data encryption key (base64, 32 bytes) comes from user-secrets / Encryption__Key, never from the repo.
EncryptionHelper.Configure(builder.Configuration["Encryption:Key"]);

// Add DbContext (Supports local SQL Server and Cloud PostgreSQL)
builder.Services.AddDbContext<SmartBankDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    if (connectionString != null && (connectionString.StartsWith("postgresql://") || connectionString.StartsWith("postgres://") || connectionString.Contains("Host=") || connectionString.Contains("port=") || connectionString.Contains("sslmode=")))
    {
        options.UseNpgsql(connectionString);
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
    options.SaveToken = true;
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
// Register Caching & Market Rates Decorator
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<MarketRateService>();
builder.Services.AddScoped<IMarketRateService>(sp => 
    new CachedMarketRateService(sp.GetRequiredService<MarketRateService>(), sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));
builder.Services.AddHttpClient<OllamaService>();
builder.Services.AddScoped<IAIChatbotService>(sp => sp.GetRequiredService<OllamaService>());
builder.Services.AddHttpClient<GeminiService>();
builder.Services.AddHttpClient<IRAGService, RAGService>();
builder.Services.AddHostedService<StandingOrderExecutionWorker>();
builder.Services.AddValidatorsFromAssemblyContaining<RegisterDtoValidator>();

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddSignalR();

// Per-IP rate limit on the auth endpoints (login, 2FA, password reset, register). This is the coarse layer;
// the per-account lockout and the OTP attempt counter in the services are the precise ones.
// Behind a reverse proxy the IP is only the real client address if forwarded headers are enabled (see Dockerfile).
var authPermitLimit = builder.Configuration.GetValue("RateLimiting:Auth:PermitLimit", 10);
var authWindowSeconds = builder.Configuration.GetValue("RateLimiting:Auth:WindowSeconds", 60);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authPermitLimit,
                Window = TimeSpan.FromSeconds(authWindowSeconds),
                QueueLimit = 0
            }));

    // Silent token refresh and logout: every signed-in client calls this every few minutes, so it gets a wider window.
    options.AddPolicy("refresh", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimiting:Refresh:PermitLimit", 60),
                Window = TimeSpan.FromSeconds(60),
                QueueLimit = 0
            }));

    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
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

var app = builder.Build();

app.UseMiddleware<GlobalExceptionMiddleware>();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseCors();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<SupportHub>("/hubs/support");

// Replaces the old /db-check (which returned the database exception message) and the /weatherforecast template.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
// Automatic Database Setup & Seeding on Startup
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<SmartBankDbContext>();
        // Apply migrations on startup (Disabled for Cloud pgBouncer. Table creation handled via SQL Editor)
        // await context.Database.MigrateAsync();
        
        // Seed default rates if empty
        if (!await context.MarketRates.AnyAsync())
        {
            context.MarketRates.AddRange(
                new MarketRate { Code = "USD", Buy = 32.50m, Sell = 32.80m, UpdatedAt = DateTime.UtcNow },
                new MarketRate { Code = "EUR", Buy = 35.10m, Sell = 35.45m, UpdatedAt = DateTime.UtcNow },
                new MarketRate { Code = "XAU", Buy = 2450.00m, Sell = 2480.00m, UpdatedAt = DateTime.UtcNow },
                new MarketRate { Code = "XAG", Buy = 30.50m, Sell = 31.20m, UpdatedAt = DateTime.UtcNow }
            );
            await context.SaveChangesAsync();
        }
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred creating or seeding the database.");
    }
}

app.Run();

// Lets the integration tests start the whole application (WebApplicationFactory<Program>).
public partial class Program { }
