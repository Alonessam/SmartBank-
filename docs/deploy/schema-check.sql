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

-- Second query: the foreign keys of the production database. The EF model defines some; a hand-written schema may have more
-- (for example from AuditLogs.UserId to Users), and a constraint the code does not expect can make a write fail in production
-- while every test, built from the model, passes. Compare this list with the "HasOne ... HasForeignKey" lines in
-- src/SmartBank.Infrastructure/Data/SmartBankDbContext.cs.
select conrelid::regclass as table_name, conname as constraint_name, pg_get_constraintdef(oid) as definition
from pg_constraint
where contype = 'f' and connamespace = 'public'::regnamespace
order by 1, 2;

-- Third check: every index in the public schema. The code relies on UNIQUE indexes on Users (Username, Tckn, Email),
-- Accounts (AccountNumber, AccountCode), RefreshTokens (TokenHash), CreditCards (UserId) and CardNumberHash.
select tablename, indexname, indexdef
from pg_indexes
where schemaname = 'public'
order by 1, 2;
