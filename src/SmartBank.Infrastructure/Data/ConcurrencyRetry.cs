using Microsoft.EntityFrameworkCore;
using SmartBank.Core.Common;

namespace SmartBank.Infrastructure.Data
{
    /// <summary>
    /// Optimistic concurrency. Accounts, credit cards, users and refresh tokens carry a version number; an UPDATE or DELETE
    /// only succeeds if the row is still at the version that was read. When two requests collide, one of them gets a
    /// conflict error instead of silently overwriting the other's change (a "lost update"), and the operation is repeated
    /// from fresh reads. Only an operation that is safe to run again may go through here: nothing before the step that
    /// is saved may have side effects (e-mails are sent after the save, not inside the operation).
    /// </summary>
    public static class ConcurrencyRetry
    {
        public const int MaxAttempts = 10;

        public static async Task<ServiceResult<T>> RunAsync<T>(DbContext context, Func<Task<ServiceResult<T>>> operation, string conflictMessage = "The data was changed by another operation at the same time. Please try again.")
        {
            for (var attempt = 1; ; attempt++)
            {
                // Start every attempt from fresh reads: anything the context still tracks may be stale.
                context.ChangeTracker.Clear();

                try
                {
                    return await operation();
                }
                catch (Exception ex) when (DatabaseConflict.IsRetryable(ex))
                {
                    context.ChangeTracker.Clear();

                    if (attempt >= MaxAttempts)
                    {
                        return ServiceResult<T>.Failure("ConcurrentModification", conflictMessage);
                    }

                    // A short, growing, random pause spreads out requests that keep colliding.
                    await Task.Delay(SecureRandom.Next(2, 20 * attempt));
                }
            }
        }
    }

    public static class DbContextExtensions
    {
        /// <summary>
        /// True for a real relational database. ExecuteUpdate/ExecuteDelete only exist there; the in-memory provider the
        /// unit tests use needs the tracked "load, change, save" path instead.
        /// </summary>
        public static bool SupportsBulkOperations(this DbContext context) => context.Database.IsRelational();
    }
}
