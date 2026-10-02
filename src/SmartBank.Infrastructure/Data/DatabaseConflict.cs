using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SmartBank.Infrastructure.Data
{
    /// <summary>
    /// Recognises the database failures that mean "someone else got in the way, run it again", as opposed to a real
    /// error. All of them are safe to retry once the transaction has been rolled back:
    ///  - an optimistic-concurrency conflict (the row's version changed since it was read),
    ///  - SQL Server error 1205: this transaction was chosen as the deadlock victim ("Rerun the transaction"),
    ///  - PostgreSQL 40001 (serialization failure) and 40P01 (deadlock detected).
    /// </summary>
    public static class DatabaseConflict
    {
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
                    case PostgresException pg when pg.SqlState is "40001" or "40P01":
                        return true;
                }
            }

            return false;
        }
    }
}
