namespace SmartBank.Core.Entities
{
    /// <summary>
    /// An entity whose rows are changed by read-modify-write logic (a balance, a debt) and must therefore be
    /// protected against lost updates. <see cref="Version"/> is an optimistic-concurrency token: every UPDATE and
    /// DELETE says "...WHERE Id = @id AND Version = @versionIRead", so if another request changed the row after it
    /// was read, nothing is written and EF throws DbUpdateConcurrencyException. SmartBankDbContext increments the
    /// version on every update. It is a plain integer so it behaves identically on SQL Server and PostgreSQL.
    /// </summary>
    public interface IConcurrencyVersioned
    {
        int Version { get; set; }
    }
}
