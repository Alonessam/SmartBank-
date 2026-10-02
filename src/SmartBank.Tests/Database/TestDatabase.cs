using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBank.Infrastructure.Data;

namespace SmartBank.Tests.Database
{
    public enum TestProvider
    {
        SqlServer,
        PostgreSql
    }

    /// <summary>
    /// Real-database tests. Concurrency bugs cannot be shown with the in-memory provider (no real parallel
    /// transactions), so these tests talk to a real server. They only run when a connection string is supplied:
    ///
    ///   SMARTBANK_TEST_SQLSERVER  e.g. Server=(localdb)\mssqllocaldb;Trusted_Connection=True;TrustServerCertificate=True
    ///   SMARTBANK_TEST_POSTGRES   e.g. Host=localhost;Username=postgres;Password=...
    ///
    /// Each test creates its own throw-away database from the EF model and drops it afterwards.
    /// CI runs the PostgreSQL variant (the production provider).
    /// </summary>
    public static class TestDatabase
    {
        public const string SqlServerVariable = "SMARTBANK_TEST_SQLSERVER";
        public const string PostgresVariable = "SMARTBANK_TEST_POSTGRES";

        private static string? Setting(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        public static bool AnyConfigured => Setting(SqlServerVariable) != null || Setting(PostgresVariable) != null;

        /// <summary>Providers that have a connection string configured; feeds [MemberData].</summary>
        public static IEnumerable<object[]> Providers()
        {
            if (Setting(SqlServerVariable) != null) yield return new object[] { TestProvider.SqlServer };
            if (Setting(PostgresVariable) != null) yield return new object[] { TestProvider.PostgreSql };
        }

        public static async Task<TestDb> CreateAsync(TestProvider provider)
        {
            var name = "smartbanktest_" + Guid.NewGuid().ToString("N");

            DbContextOptions<SmartBankDbContext> options;
            switch (provider)
            {
                case TestProvider.SqlServer:
                    var sql = new SqlConnectionStringBuilder(Setting(SqlServerVariable)!) { InitialCatalog = name };
                    options = new DbContextOptionsBuilder<SmartBankDbContext>().UseSqlServer(sql.ConnectionString).Options;
                    break;
                case TestProvider.PostgreSql:
                    var pg = new NpgsqlConnectionStringBuilder(Setting(PostgresVariable)!) { Database = name };
                    options = new DbContextOptionsBuilder<SmartBankDbContext>().UseNpgsql(pg.ConnectionString).Options;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(provider));
            }

            var db = new TestDb(options);
            await using var context = db.NewContext();
            await context.Database.EnsureCreatedAsync();
            return db;
        }
    }

    public sealed class TestDb : IAsyncDisposable
    {
        private readonly DbContextOptions<SmartBankDbContext> _options;

        public TestDb(DbContextOptions<SmartBankDbContext> options) => _options = options;

        /// <summary>A new context, like the one a single web request would get.</summary>
        public SmartBankDbContext NewContext() => new(_options);

        public async ValueTask DisposeAsync()
        {
            await using (var context = NewContext())
            {
                SqlConnection.ClearAllPools();
                NpgsqlConnection.ClearAllPools();
                await context.Database.EnsureDeletedAsync();
            }
        }
    }

    /// <summary>A [Theory] that is skipped (and says why) when no test database is configured.</summary>
    public sealed class DatabaseTheoryAttribute : TheoryAttribute
    {
        public DatabaseTheoryAttribute()
        {
            if (!TestDatabase.AnyConfigured)
            {
                Skip = $"Set {TestDatabase.SqlServerVariable} and/or {TestDatabase.PostgresVariable} to run the real-database tests.";
            }
        }
    }
}

namespace SmartBank.Tests.Database
{
    /// <summary>A [Fact] that is skipped (and says why) when no PostgreSQL test database is configured.</summary>
    public sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (!TestDatabase.Providers().Any(p => (TestProvider)p[0] == TestProvider.PostgreSql))
            {
                Skip = $"Set {TestDatabase.PostgresVariable} to run the PostgreSQL tests.";
            }
        }
    }
}
