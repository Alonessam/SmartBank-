using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SmartBank.Infrastructure.Data
{
    /// <summary>
    /// Recognises the database failures that mean "someone else got in the way", as opposed to a real error.
    /// Retryable (safe to run again once the transaction has been rolled back):
    ///  - an optimistic-concurrency conflict (the row's version changed since it was read),
    ///  - SQL Server error 1205: this transaction was chosen as the deadlock victim ("Rerun the transaction"),
    ///  - PostgreSQL 40001 (serialization failure) and 40P01 (deadlock detected).
    /// Unique violations are a different thing: running again gives the same answer, so they are reported to the caller
    /// as "already exists" instead (see <see cref="IsUniqueViolation"/>).
    /// </summary>
    public static class DatabaseConflict
    {
        private static readonly Regex SqlServerIndexName = new(@"(?:index|constraint) '([^']+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsRetryable(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                switch (current)
                {
                    case DbUpdateConcurrencyException:
                        return true;
                    case SqlException sql when sql.Number == 1205:
                        return true;
                    // A foreign-key violation while saving a money movement means the other account was closed between the
                    // read and the write (SQL Server 547, PostgreSQL 23503). Repeating the operation re-reads: the account is
                    // gone, so the caller gets a clean "not found" instead of a raw database error.
                    case SqlException sql when sql.Number == 547:
                        return true;
                    case PostgresException pg when pg.SqlState is "40001" or "40P01" or "23503":
                        return true;
                }
            }

            return false;
        }

        /// <summary>A unique index or constraint rejected the write (SQL Server 2601/2627, PostgreSQL 23505).</summary>
        public static bool IsUniqueViolation(Exception exception) => UniqueViolation(exception) != null;

        /// <summary>
        /// The name of the unique index that was violated, or an empty string when the provider did not say.
        /// Null when the exception is not a unique violation at all.
        /// </summary>
        public static string? UniqueConstraintName(Exception exception) => UniqueViolation(exception);

        private static string? UniqueViolation(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                switch (current)
                {
                    case PostgresException pg when pg.SqlState == "23505":
                        return pg.ConstraintName ?? string.Empty;
                    case SqlException sql when sql.Number is 2601 or 2627:
                        var match = SqlServerIndexName.Match(sql.Message);
                        return match.Success ? match.Groups[1].Value : string.Empty;
                }
            }

            return null;
        }
    }
}
