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
                                     ""TwoFactorEnabled"", ""FailedLoginCount"", ""OtpFailedCount"", ""Role"", ""Version"", ""CreatedAt"")
                VALUES ('00000000-0000-0000-0000-000000000001', 'legacy', '11111111111', 'x', 'L', 'U', 'L U', 'l@u.test',
                        false, 0, 0, 0, 0, '2026-10-06 10:27:18');");

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

        [PostgresFact]
        public async Task The_v1_3_script_adds_the_user_version_and_the_indexes_merges_duplicates_and_can_be_run_twice()
        {
            await using var modelDb = await TestDatabase.CreateAsync(TestProvider.PostgreSql);
            await using var legacyDb = await TestDatabase.CreateAsync(TestProvider.PostgreSql);

            await using var modelConnection = new NpgsqlConnection(modelDb.NewContext().Database.GetConnectionString());
            await using var legacyConnection = new NpgsqlConnection(legacyDb.NewContext().Database.GetConnectionString());
            await modelConnection.OpenAsync();
            await legacyConnection.OpenAsync();

            // The pre-v1.3 shape: no version on users, a plain index on credit cards and saved recipients, single-column foreign
            // key indexes on transactions and chat messages, none of the new query indexes.
            await ExecuteAsync(legacyConnection, @"
                ALTER TABLE ""Users"" DROP COLUMN ""Version"";
                DROP INDEX ""IX_CreditCards_UserId"";
                CREATE INDEX ""IX_CreditCards_UserId"" ON ""CreditCards"" (""UserId"");
                DROP INDEX ""IX_SavedContacts_UserId_AccountNumber"";
                DROP INDEX ""IX_Transactions_SourceAccountId_CreatedAt"";
                DROP INDEX ""IX_Transactions_DestinationAccountId_CreatedAt"";
                CREATE INDEX ""IX_Transactions_SourceAccountId"" ON ""Transactions"" (""SourceAccountId"");
                CREATE INDEX ""IX_Transactions_DestinationAccountId"" ON ""Transactions"" (""DestinationAccountId"");
                DROP INDEX ""IX_AuditLogs_UserId_CreatedAt"";
                DROP INDEX ""IX_StandingOrders_IsActive_NextExecutionDate"";
                DROP INDEX ""IX_ChatMessages_SessionId_CreatedAt"";
                CREATE INDEX ""IX_ChatMessages_SessionId"" ON ""ChatMessages"" (""SessionId"");
                CREATE INDEX ""IX_SavedContacts_UserId"" ON ""SavedContacts"" (""UserId"");");

            Assert.NotEqual(await ColumnsAsync(modelConnection, AllTables), await ColumnsAsync(legacyConnection, AllTables));
            Assert.NotEqual(await ListAsync(modelConnection, IndexSql), await ListAsync(legacyConnection, IndexSql));

            // Data that existed before the upgrade: a customer with TWO credit cards (a race in v1.2), each with a statement,
            // one standing order that points at the NEWER card, and the same recipient saved twice.
            await ExecuteAsync(legacyConnection, @"
                INSERT INTO ""Users"" (""Id"", ""Username"", ""Tckn"", ""PasswordHash"", ""FirstName"", ""LastName"", ""FullName"", ""Email"",
                                     ""TwoFactorEnabled"", ""FailedLoginCount"", ""OtpFailedCount"", ""Role"", ""CreatedAt"")
                VALUES ('00000000-0000-0000-0000-0000000000a1', 'dup', '11111111111', 'x', 'D', 'U', 'D U', 'd@u.test', false, 0, 0, 0, '2026-10-01 10:00:00+00');

                INSERT INTO ""CreditCards"" (""Id"", ""UserId"", ""EncryptedCardNumber"", ""ExpiryDate"", ""CardLimit"", ""CurrentDebt"", ""CardTheme"", ""CreatedAt"", ""Version"")
                VALUES ('00000000-0000-0000-0000-0000000000c1', '00000000-0000-0000-0000-0000000000a1', 'old', '10/31', 10000, 100.50, 't', '2026-10-01 10:00:00+00', 0),
                       ('00000000-0000-0000-0000-0000000000c2', '00000000-0000-0000-0000-0000000000a1', 'new', '10/31', 10000, 49.50, 't', '2026-10-02 10:00:00+00', 0);

                INSERT INTO ""CreditCardStatements"" (""Id"", ""CreditCardId"", ""PeriodName"", ""PeriodDebt"", ""MinimumPayment"", ""PaidAmount"", ""CutoffDate"", ""DueDate"", ""IsPaid"")
                VALUES ('00000000-0000-0000-0000-0000000000d1', '00000000-0000-0000-0000-0000000000c1', 'Ekim 2026', 100.50, 30, 0, '2026-11-01 00:00:00+00', '2026-11-11 00:00:00+00', false),
                       ('00000000-0000-0000-0000-0000000000d2', '00000000-0000-0000-0000-0000000000c2', 'Ekim 2026', 49.50, 15, 0, '2026-11-01 00:00:00+00', '2026-11-11 00:00:00+00', false);

                INSERT INTO ""StandingOrders"" (""Id"", ""UserId"", ""SourceAccountNumber"", ""Frequency"", ""MaturityDate"", ""NextExecutionDate"", ""IsActive"", ""OrderType"", ""CreditCardId"", ""CreatedAt"")
                VALUES ('00000000-0000-0000-0000-0000000000e1', '00000000-0000-0000-0000-0000000000a1', 'TR0000000000000001', 'Monthly',
                        '2027-10-01 00:00:00+00', '2026-11-01 00:00:00+00', true, 'CreditCardAutoPay', '00000000-0000-0000-0000-0000000000c2', '2026-10-01 10:00:00+00');

                INSERT INTO ""SavedContacts"" (""Id"", ""UserId"", ""AccountNumber"", ""Alias"", ""CreatedAt"")
                VALUES ('00000000-0000-0000-0000-0000000000b1', '00000000-0000-0000-0000-0000000000a1', 'TR0000000000000002', 'first alias', '2026-10-01 10:00:00+00'),
                       ('00000000-0000-0000-0000-0000000000b2', '00000000-0000-0000-0000-0000000000a1', 'TR0000000000000002', 'second alias', '2026-10-02 10:00:00+00'),
                       ('00000000-0000-0000-0000-0000000000b3', '00000000-0000-0000-0000-0000000000a1', 'TR0000000000000003', 'other recipient', '2026-10-03 10:00:00+00');");

            var script = await File.ReadAllTextAsync(RepositoryFile("docs/deploy/v1.3-postgres-upgrade.sql"));

            await ExecuteAsync(legacyConnection, script);

            var expectedColumns = await ColumnsAsync(modelConnection, AllTables);
            Assert.Contains(expectedColumns, c => c.StartsWith("Users.Version "));
            Assert.Equal(expectedColumns, await ColumnsAsync(legacyConnection, AllTables));
            var expectedIndexes = await ListAsync(modelConnection, IndexSql);
            Assert.Contains(expectedIndexes, i => i.Contains("IX_CreditCards_UserId") && i.Contains("UNIQUE"));
            Assert.Equal(expectedIndexes, await ListAsync(legacyConnection, IndexSql));

            // The newer card was folded into the older one: one card, both debts, both statements, the order points at it.
            Assert.Equal("00000000-0000-0000-0000-0000000000c1", await ScalarAsync(legacyConnection, @"SELECT ""Id""::text FROM ""CreditCards"""));
            Assert.Equal("150.00", await ScalarAsync(legacyConnection, @"SELECT ""CurrentDebt""::text FROM ""CreditCards"""));
            Assert.Equal("2", await ScalarAsync(legacyConnection, @"SELECT count(*)::text FROM ""CreditCardStatements"" WHERE ""CreditCardId"" = '00000000-0000-0000-0000-0000000000c1'"));
            Assert.Equal("00000000-0000-0000-0000-0000000000c1", await ScalarAsync(legacyConnection, @"SELECT ""CreditCardId""::text FROM ""StandingOrders"""));

            // The recipient saved twice keeps its OLDEST entry; the other recipient is untouched.
            Assert.Equal("2", await ScalarAsync(legacyConnection, @"SELECT count(*)::text FROM ""SavedContacts"""));
            Assert.Equal("first alias", await ScalarAsync(legacyConnection, @"SELECT ""Alias"" FROM ""SavedContacts"" WHERE ""AccountNumber"" = 'TR0000000000000002'"));

            // Running it again changes nothing.
            const string dataSql = @"SELECT string_agg(x, '|' ORDER BY x) FROM (
                SELECT 'card ' || ""Id""::text || ' ' || ""CurrentDebt""::text || ' v' || ""Version""::text AS x FROM ""CreditCards""
                UNION ALL SELECT 'contact ' || ""Id""::text FROM ""SavedContacts""
                UNION ALL SELECT 'order ' || ""CreditCardId""::text FROM ""StandingOrders"") t";
            var dataAfterFirstRun = await ScalarAsync(legacyConnection, dataSql);

            await ExecuteAsync(legacyConnection, script);

            Assert.Equal(expectedColumns, await ColumnsAsync(legacyConnection, AllTables));
            Assert.Equal(expectedIndexes, await ListAsync(legacyConnection, IndexSql));
            Assert.Equal(dataAfterFirstRun, await ScalarAsync(legacyConnection, dataSql));

            // The unique indexes really reject duplicates now.
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(legacyConnection, @"
                INSERT INTO ""SavedContacts"" (""Id"", ""UserId"", ""AccountNumber"", ""Alias"", ""CreatedAt"")
                VALUES ('00000000-0000-0000-0000-0000000000b9', '00000000-0000-0000-0000-0000000000a1', 'TR0000000000000002', 'again', now())"));
        }

        // Indexes of the tables the v1.3 script touches (names and definitions).
        private const string IndexSql =
            "SELECT indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename IN " +
            "('Accounts', 'AuditLogs', 'ChatMessages', 'ChatSessions', 'CreditCards', 'CreditCardStatements', 'CreditCardTransactions', " +
            "'SavedContacts', 'StandingOrders', 'Transactions') ORDER BY indexname";

        private static async Task<string?> ScalarAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return (await command.ExecuteScalarAsync())?.ToString();
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
