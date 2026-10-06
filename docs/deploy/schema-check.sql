-- SmartBank: list every column of the production database, to compare with what the code expects.
--
-- Run in the Supabase SQL editor and copy the single result cell. The production tables were created by hand, so a
-- column the code needs can be missing (that is how "Start Session" once did nothing: "ChatSessions"."IsActive"
-- was absent). The expected schema is what EF Core generates for PostgreSQL:
--
--     dotnet ef dbcontext script --project src/SmartBank.Infrastructure --startup-project src/SmartBank.API ^
--         --output expected.sql        (with ConnectionStrings__DefaultConnection set to a "Host=..." string)
--
-- Differences that are harmless: varchar instead of text, a column that is nullable in the database but required in the
-- model, extra columns. What matters: a column the model has that the database lacks, a NOT NULL column the code
-- leaves empty, and "timestamp without time zone" (the API then returns times without a "Z").

select string_agg(table_name || '.' || column_name || ' ' || data_type || ' ' || is_nullable, E'\n'
                  order by table_name, column_name)
from information_schema.columns
where table_schema = 'public';
