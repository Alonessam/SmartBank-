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

        private static Task<List<string>> ColumnsAsync(NpgsqlConnection connection) =>
            ColumnsAsync(connection, "'Accounts', 'CreditCards', 'Users'");

        private static async Task<List<string>> ColumnsAsync(NpgsqlConnection connection, string tableList)
        {
            // Compare name, type, nullability and length. Defaults are deliberately ignored: the model has none,
            // the script adds "DEFAULT 0" so existing rows can be filled in.
            var sql = @"
                SELECT table_name || '.' || column_name || ' ' || data_type || ' null=' || is_nullable ||
                       ' len=' || COALESCE(character_maximum_length::text, '-') ||
                       ' num=' || COALESCE(numeric_precision::text, '-') || ',' || COALESCE(numeric_scale::text, '-')
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name IN (" + tableList + @")
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
                                      DROP COLUMN ""PendingOtpPurpose"", DROP COLUMN ""PendingOtpBinding"", DROP COLUMN ""Role"";
                ALTER TABLE ""Accounts"" ALTER COLUMN ""InterestRate"" TYPE numeric;
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

        [PostgresFact]
        public async Task The_v1_2_script_creates_the_refresh_token_table_the_model_expects_and_can_be_run_twice()
        {
            await using var modelDb = await TestDatabase.CreateAsync(TestProvider.PostgreSql);
            await using var legacyDb = await TestDatabase.CreateAsync(TestProvider.PostgreSql);

            await using var modelConnection = new NpgsqlConnection(modelDb.NewContext().Database.GetConnectionString());
            await using var legacyConnection = new NpgsqlConnection(legacyDb.NewContext().Database.GetConnectionString());
            await modelConnection.OpenAsync();
            await legacyConnection.OpenAsync();

            // Pre-v1.2 shape of the hand-made production tables: no refresh-token table, ChatSessions without "IsActive",
            // a NOT NULL auto-pay amount, and every time column "without time zone" (one of them with a default).
            await ExecuteAsync(legacyConnection, @"
                DROP TABLE ""RefreshTokens"";
                ALTER TABLE ""ChatSessions"" DROP COLUMN ""IsActive"";
                UPDATE ""StandingOrders"" SET ""Amount"" = 0 WHERE ""Amount"" IS NULL;
                ALTER TABLE ""StandingOrders"" ALTER COLUMN ""Amount"" SET NOT NULL;
                DO $$
                DECLARE col record;
                BEGIN
                    FOR col IN SELECT table_name, column_name FROM information_schema.columns
                               WHERE table_schema = 'public' AND data_type = 'timestamp with time zone'
                                 AND NOT (table_name = 'Users' AND column_name = 'LockoutEnd')
                    LOOP
                        EXECUTE format('ALTER TABLE %I ALTER COLUMN %I TYPE timestamp without time zone', col.table_name, col.column_name);
                    END LOOP;
                END $$;
                ALTER TABLE ""Users"" ALTER COLUMN ""CreatedAt"" SET DEFAULT timezone('utc', now());");
            Assert.Empty(await ColumnsAsync(legacyConnection, "'RefreshTokens'"));
            Assert.NotEqual(await ColumnsAsync(modelConnection, AllTables), await ColumnsAsync(legacyConnection, AllTables));

            var script = await File.ReadAllTextAsync(RepositoryFile("docs/deploy/v1.2-postgres-upgrade.sql"));

            // A row written before the upgrade keeps its meaning: the stored UTC clock time is now a UTC instant.
            await ExecuteAsync(legacyConnection, @"
                INSERT INTO ""Users"" (""Id"", ""Username"", ""Tckn"", ""PasswordHash"", ""FirstName"", ""LastName"", ""FullName"", ""Email"",
                                     ""TwoFactorEnabled"", ""FailedLoginCount"", ""OtpFailedCount"", ""Role"", ""CreatedAt"")
                VALUES ('00000000-0000-0000-0000-000000000001', 'legacy', '11111111111', 'x', 'L', 'U', 'L U', 'l@u.test',
                        false, 0, 0, 0, '2026-10-06 10:27:18');");

            await ExecuteAsync(legacyConnection, script);
            var expected = await ColumnsAsync(modelConnection, AllTables);
            Assert.NotEmpty(expected);
            Assert.Contains(expected, c => c.StartsWith("ChatSessions.IsActive "));
            Assert.Contains(expected, c => c.StartsWith("StandingOrders.Amount ") && c.Contains("null=YES"));
            Assert.Equal(expected, await ColumnsAsync(legacyConnection, AllTables));

            await using (var instant = new NpgsqlCommand(
                @"SELECT to_char(""CreatedAt"" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS') FROM ""Users"" WHERE ""Username"" = 'legacy'", legacyConnection))
            {
                Assert.Equal("2026-10-06 10:27:18", await instant.ExecuteScalarAsync());
            }

            await ExecuteAsync(legacyConnection, script); // idempotent: a second run must not shift the times again
            Assert.Equal(expected, await ColumnsAsync(legacyConnection, AllTables));
            await using (var again = new NpgsqlCommand(
                @"SELECT to_char(""CreatedAt"" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS') FROM ""Users"" WHERE ""Username"" = 'legacy'", legacyConnection))
            {
                Assert.Equal("2026-10-06 10:27:18", await again.ExecuteScalarAsync());
            }

            // Same indexes as the model (the unique one on the hash is what makes the lookup safe), and the cascade works.
            const string indexSql = "SELECT indexname || ' ' || indexdef FROM pg_indexes WHERE tablename = 'RefreshTokens' ORDER BY indexname";
            Assert.Equal(await ListAsync(modelConnection, indexSql), await ListAsync(legacyConnection, indexSql));
        }

        // Every table the upgrade script touches. MarketRates is left out on purpose: the old hand-made table differs from the
        // model in other ways (see docs/DEFENSE.md, T14) and nothing reads it except a "is it empty" check.
        private const string AllTables =
            "'Accounts', 'AuditLogs', 'ChatMessages', 'ChatSessions', 'CreditCards', 'CreditCardStatements', 'CreditCardTransactions', " +
            "'RefreshTokens', 'SavedContacts', 'StandingOrders', 'Transactions', 'Users'";

        private static async Task<List<string>> ListAsync(NpgsqlConnection connection, string sql)
        {
            var result = new List<string>();
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(reader.GetString(0));
            return result;
        }
    }
}
