using System.Data.Common;
using Npgsql;

namespace SmartBank.Infrastructure.Data
{
    public enum DatabaseKind
    {
        SqlServer,
        PostgreSql
    }

    /// <summary>
    /// Decides from the connection string whether to talk to SQL Server (local development) or PostgreSQL (production,
    /// Supabase). The old check was a handful of case-sensitive substring tests and passed a "postgresql://" URI straight to
    /// Npgsql, which does not read URIs: the app then failed with a message about a bad connection string.
    /// </summary>
    public static class DatabaseProviderSelector
    {
        // Keys that only a PostgreSQL (Npgsql) connection string uses. "Server", "User ID" and "Database" exist in both.
        private static readonly string[] PostgresOnlyKeys = { "host", "port", "username", "sslmode", "ssl mode", "search path", "searchpath" };

        public static bool IsPostgresUri(string connectionString) =>
            connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

        /// <exception cref="InvalidOperationException">The connection string is missing or cannot be read.</exception>
        public static DatabaseKind Detect(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not set. Use a SQL Server connection string for local development or a PostgreSQL one (key=value form or a postgresql:// URI) in production.");
            }

            if (IsPostgresUri(connectionString)) return DatabaseKind.PostgreSql;

            DbConnectionStringBuilder builder;
            try
            {
                builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not a valid connection string: " + ex.Message, ex);
            }

            return PostgresOnlyKeys.Any(key => builder.ContainsKey(key)) ? DatabaseKind.PostgreSql : DatabaseKind.SqlServer;
        }

        /// <summary>
        /// The Npgsql (key=value) form of the connection string. A postgresql://user:password@host:port/database?sslmode=require
        /// URI is converted; anything else is returned as it is.
        /// </summary>
        /// <exception cref="InvalidOperationException">A URI that has no host or database.</exception>
        public static string ToNpgsqlConnectionString(string connectionString)
        {
            if (!IsPostgresUri(connectionString)) return connectionString;

            if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            {
                throw new InvalidOperationException("ConnectionStrings:DefaultConnection looks like a PostgreSQL URI but has no host. Expected postgresql://user:password@host:5432/database.");
            }

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.IsDefaultPort || uri.Port < 0 ? 5432 : uri.Port,
                Database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'))
            };

            if (string.IsNullOrEmpty(builder.Database))
            {
                throw new InvalidOperationException("ConnectionStrings:DefaultConnection looks like a PostgreSQL URI but names no database. Expected postgresql://user:password@host:5432/database.");
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var separator = uri.UserInfo.IndexOf(':');
                builder.Username = Uri.UnescapeDataString(separator < 0 ? uri.UserInfo : uri.UserInfo[..separator]);
                if (separator >= 0) builder.Password = Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]);
            }

            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(parts[0]);
                var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;

                if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<SslMode>(value.Replace("-", string.Empty), ignoreCase: true, out var sslMode))
                {
                    builder.SslMode = sslMode;
                }
            }

            return builder.ConnectionString;
        }
    }
}
