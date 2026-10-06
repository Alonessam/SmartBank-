using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBank.Infrastructure.Data;

namespace SmartBank.Tests
{
    /// <summary>Which database errors mean "run the operation again from fresh reads" and which are real failures.</summary>
    public class DatabaseConflictTests
    {
        private static PostgresException Pg(string sqlState) => new("test error", "ERROR", "ERROR", sqlState);

        [Theory]
        [InlineData("40001")] // serialization failure
        [InlineData("40P01")] // deadlock
        [InlineData("23503")] // foreign key: the other account was closed between read and write
        public void These_postgres_errors_are_worth_repeating(string sqlState)
        {
            Assert.True(DatabaseConflict.IsRetryable(Pg(sqlState)));
            Assert.True(DatabaseConflict.IsRetryable(new DbUpdateException("save failed", Pg(sqlState))));
        }

        [Theory]
        [InlineData("23505")] // unique violation: a retry would fail the same way
        [InlineData("22001")] // value too long
        [InlineData("42P01")] // missing table
        public void Other_postgres_errors_are_not_retried(string sqlState)
        {
            Assert.False(DatabaseConflict.IsRetryable(Pg(sqlState)));
        }

        [Fact]
        public void A_concurrency_exception_is_retried_and_an_ordinary_exception_is_not()
        {
            Assert.True(DatabaseConflict.IsRetryable(new DbUpdateConcurrencyException("someone else changed the row")));
            Assert.False(DatabaseConflict.IsRetryable(new InvalidOperationException("bug")));
        }

        [Fact]
        public void A_unique_violation_is_recognised_and_is_not_a_retryable_conflict()
        {
            var unique = new DbUpdateException("save failed", Pg("23505"));

            Assert.True(DatabaseConflict.IsUniqueViolation(unique));
            Assert.False(DatabaseConflict.IsRetryable(unique));
        }
    }
}
