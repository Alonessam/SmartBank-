using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SmartBank.API.Hubs;
using SmartBank.Core.Interfaces;
using SmartBank.Infrastructure.Data;
using SmartBank.Infrastructure.Services;
using SmartBank.Infrastructure.BackgroundServices;
using SmartBank.API.Middlewares;
using FluentValidation;
using SmartBank.Core.Validators;
using SmartBank.Core.Entities;

var builder = WebApplication.CreateBuilder(args);

// Add DbContext (Supports local SQL Server and Cloud PostgreSQL)
builder.Services.AddDbContext<SmartBankDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    if (connectionString != null && (connectionString.Contains("Host=") || connectionString.Contains("port=") || connectionString.Contains("sslmode=")))
    {
        options.UseNpgsql(connectionString);
    }
    else
    {
        options.UseSqlServer(connectionString);
    }
});

// Add JWT Authentication
var jwtKey = builder.Configuration["JwtSettings:Key"] ?? "SuperSecretKeyForDevelopmentSmartBankSupportMesh2026";
var key = Encoding.ASCII.GetBytes(jwtKey);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false;
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = true,
        ValidIssuer = builder.Configuration["JwtSettings:Issuer"] ?? "SmartBankAPI",
        ValidateAudience = true,
        ValidAudience = builder.Configuration["JwtSettings:Audience"] ?? "SmartBankApp",
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

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .SetIsOriginAllowed(origin => true)
              .AllowCredentials();
    });
});

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

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<SupportHub>("/hubs/support");

app.MapGet("/db-check", async (SmartBankDbContext db) =>
{
    try
    {
        var canConnect = await db.Database.CanConnectAsync();
        return Results.Ok(new { DatabaseConnection = canConnect, Message = "Successfully connected to SmartBankDb on LocalDB!" });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
})
.WithName("DbCheck");

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

// Automatic Database Setup & Seeding on Startup
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<SmartBankDbContext>();
        // Ensure Database is Created
        await context.Database.EnsureCreatedAsync();
        
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

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}


