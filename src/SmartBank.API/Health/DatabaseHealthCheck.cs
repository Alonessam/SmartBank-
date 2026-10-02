using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartBank.Infrastructure.Data;

namespace SmartBank.API.Health
{
    /// <summary>
    /// Readiness: can the API reach its database? The result is only Healthy or Unhealthy. Connection details and error
    /// messages go to the log, never into the response (the old /db-check endpoint returned the exception message).
    /// </summary>
    public sealed class DatabaseHealthCheck : IHealthCheck
    {
        private readonly SmartBankDbContext _db;
        private readonly ILogger<DatabaseHealthCheck> _logger;

        public DatabaseHealthCheck(SmartBankDbContext db, ILogger<DatabaseHealthCheck> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                return await _db.Database.CanConnectAsync(cancellationToken)
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database health check failed.");
                return HealthCheckResult.Unhealthy();
            }
        }
    }
}
