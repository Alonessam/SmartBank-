using Npgsql;
using SmartBank.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace SmartBank.Tests.Database
{
    /// <summary>
    /// docs/deploy/v1.1-postgres-upgrade.sql is run by hand against the production database, so a typo in it would
    /// be discovered in production. This test turns a database built from the current model back into a "legacy"
    /// one, runs the script on it, and checks that it ends up with the same columns as the model, and that running
    /// the script a second time changes nothing.
    /// </summary>
    // One collection: these classes each open dozens of connections, so they run one after another, not in parallel.
    [Collection("Database")]
    public class PostgresUpgradeScriptTests
    {
        private static string RepositoryFile(string relativePath)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "SmartBank.slnx")))
                {
                    return Path.Combine(dir.FullName, relativePath);
                }
            }

            throw new FileNotFoundException("Could not find the repository root (SmartBank.slnx).");
        }

        private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task<List<string>> ColumnsAsync(NpgsqlConnection connection)
        {
            // Compare name, type, nullability and length. Defaults are deliberately ignored: the model has none,
            // the script adds "DEFAULT 0" so existing rows can be filled in.
            const string sql = @"
                SELECT table_name || '.' || column_name || ' ' || data_type || ' null=' || is_nullable ||
                       ' len=' || COALESCE(character_maximum_length::text, '-')
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name IN ('Accounts', 'CreditCards', 'Users')
                ORDER BY table_name, column_name";

            var result = new List<string>();
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(reader.GetString(0));
            return result;
        }

        [PostgresFact]
        public async Task The_upgrade_script_brings_a_legacy_schema_to_the_current_model_and_can_be_run_twice()
        {
            await using var modelDb = await TestDatabase.CreateAsync(TestProvider.PostgreSql);
            await using var legacyDb = await TestDatabase.CreateAsync(TestProvider.PostgreSql);

            await using var modelConnection = new NpgsqlConnection(modelDb.NewContext().Database.GetConnectionString());
            await using var legacyConnection = new NpgsqlConnection(legacyDb.NewContext().Database.GetConnectionString());
            await modelConnection.OpenAsync();
            await legacyConnection.OpenAsync();

            // Turn the legacy copy back into the pre-v1.1 shape.
            await ExecuteAsync(legacyConnection, @"
                DROP INDEX IF EXISTS ""IX_CreditCards_CardNumberHash"";
                ALTER TABLE ""CreditCards"" DROP COLUMN ""CardNumberHash"", DROP COLUMN ""Version"";
                ALTER TABLE ""Accounts"" DROP COLUMN ""Version"";
                ALTER TABLE ""Users"" DROP COLUMN ""FailedLoginCount"", DROP COLUMN ""LockoutEnd"", DROP COLUMN ""OtpFailedCount"",
                                      DROP COLUMN ""PendingOtpPurpose"", DROP COLUMN ""PendingOtpBinding"";
                ALTER TABLE ""Accounts"" ADD COLUMN ""EncryptedCardCvv"" varchar(50) NOT NULL DEFAULT '';
                ALTER TABLE ""CreditCards"" ADD COLUMN ""EncryptedCardCvv"" varchar(50) NOT NULL DEFAULT '';");

            Assert.NotEqual(await ColumnsAsync(modelConnection), await ColumnsAsync(legacyConnection));

            var script = await File.ReadAllTextAsync(RepositoryFile("docs/deploy/v1.1-postgres-upgrade.sql"));

            await ExecuteAsync(legacyConnection, script);
            Assert.Equal(await ColumnsAsync(modelConnection), await ColumnsAsync(legacyConnection));

            await ExecuteAsync(legacyConnection, script); // idempotent: a second run must not fail or change anything
            Assert.Equal(await ColumnsAsync(modelConnection), await ColumnsAsync(legacyConnection));

            // The unique index on the card hash exists and rejects duplicates.
            await using var indexCheck = new NpgsqlCommand(
                "SELECT count(*) FROM pg_indexes WHERE indexname = 'IX_CreditCards_CardNumberHash' AND indexdef LIKE 'CREATE UNIQUE%'",
                legacyConnection);
            Assert.Equal(1L, await indexCheck.ExecuteScalarAsync());
        }
    }
}
