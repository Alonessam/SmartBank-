using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SmartBank.Tests.Database
{
    /// <summary>
    /// docs/deploy/00-baseline-postgres.sql is how a fresh PostgreSQL database (a new Supabase project, the docker-compose
    /// container) gets its tables. It is generated from the EF model by hand (see its header), so it can silently fall behind
    /// when the model changes. This test applies it to an empty throw-away database and compares columns, indexes and
    /// constraints with a database that EF Core builds from the current model (EnsureCreated).
    /// If it fails, regenerate the baseline: the command is in the header of the script.
    /// </summary>
    // One collection: these classes each open dozens of connections, so they run one after another, not in parallel.
    [Collection("Database")]
    public class BaselineSchemaTests
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

        private const string ColumnSql = @"
            SELECT table_name || '.' || column_name || ' ' || data_type || ' null=' || is_nullable ||
                   ' identity=' || is_identity ||
                   ' len=' || COALESCE(character_maximum_length::text, '-') ||
                   ' num=' || COALESCE(numeric_precision::text, '-') || ',' || COALESCE(numeric_scale::text, '-')
            FROM information_schema.columns
            WHERE table_schema = 'public'
            ORDER BY table_name, column_name";

        private const string IndexSql = @"
            SELECT tablename || ' ' || indexname || ' ' || indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
            ORDER BY tablename, indexname";

        private const string ConstraintSql = @"
            SELECT conrelid::regclass::text || ' ' || conname || ' ' || contype::text || ' ' || pg_get_constraintdef(oid)
            FROM pg_constraint
            WHERE connamespace = 'public'::regnamespace
            ORDER BY 1";

        private static async Task<List<string>> ListAsync(NpgsqlConnection connection, string sql)
        {
            var result = new List<string>();
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(reader.GetString(0));
            return result;
        }

        private static string Describe(string what, List<string> expected, List<string> actual)
        {
            var missing = expected.Except(actual).ToList();
            var extra = actual.Except(expected).ToList();
            return $"{what}: the baseline script and the EF model differ. Regenerate docs/deploy/00-baseline-postgres.sql.\n" +
                   "Only in the model (missing from the baseline):\n  " + string.Join("\n  ", missing) +
                   "\nOnly in the baseline (not in the model):\n  " + string.Join("\n  ", extra);
        }

        [PostgresFact]
        public async Task The_baseline_script_creates_the_same_schema_as_the_current_EF_model()
        {
            var adminString = Environment.GetEnvironmentVariable(TestDatabase.PostgresVariable)!;
            var admin = new NpgsqlConnectionStringBuilder(adminString) { Database = "postgres", Pooling = false };
            var emptyName = "smartbanktest_baseline_" + Guid.NewGuid().ToString("N");

            await using var modelDb = await TestDatabase.CreateAsync(TestProvider.PostgreSql);
            await using var modelConnection = new NpgsqlConnection(modelDb.NewContext().Database.GetConnectionString());
            await modelConnection.OpenAsync();

            await using (var adminConnection = new NpgsqlConnection(admin.ConnectionString))
            {
                await adminConnection.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{emptyName}\"", adminConnection);
                await create.ExecuteNonQueryAsync();
            }

            try
            {
                var baseline = new NpgsqlConnectionStringBuilder(adminString) { Database = emptyName, Pooling = false };
                await using var baselineConnection = new NpgsqlConnection(baseline.ConnectionString);
                await baselineConnection.OpenAsync();

                var script = await File.ReadAllTextAsync(RepositoryFile("docs/deploy/00-baseline-postgres.sql"));
                await using (var run = new NpgsqlCommand(script, baselineConnection))
                {
                    await run.ExecuteNonQueryAsync();
                }

                var modelColumns = await ListAsync(modelConnection, ColumnSql);
                var baselineColumns = await ListAsync(baselineConnection, ColumnSql);
                Assert.True(modelColumns.SequenceEqual(baselineColumns), Describe("Columns", modelColumns, baselineColumns));

                var modelIndexes = await ListAsync(modelConnection, IndexSql);
                var baselineIndexes = await ListAsync(baselineConnection, IndexSql);
                Assert.True(modelIndexes.SequenceEqual(baselineIndexes), Describe("Indexes", modelIndexes, baselineIndexes));

                var modelConstraints = await ListAsync(modelConnection, ConstraintSql);
                var baselineConstraints = await ListAsync(baselineConnection, ConstraintSql);
                Assert.True(modelConstraints.SequenceEqual(baselineConstraints), Describe("Constraints", modelConstraints, baselineConstraints));

                // Not an empty comparison by accident: the tables really are there.
                Assert.Contains(baselineColumns, c => c.StartsWith("Users.Version "));
                Assert.Contains(baselineColumns, c => c.StartsWith("RefreshTokens.TokenHash "));
            }
            finally
            {
                NpgsqlConnection.ClearAllPools();
                await using var adminConnection = new NpgsqlConnection(admin.ConnectionString);
                await adminConnection.OpenAsync();
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{emptyName}\" WITH (FORCE)", adminConnection);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }
}
