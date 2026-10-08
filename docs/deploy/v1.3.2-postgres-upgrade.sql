-- SmartBank v1.3.2: make the hand-made production tables accept what the code writes (PostgreSQL / Supabase)
--
-- Run ONCE in the Supabase SQL editor BEFORE deploying the v1.3.2 API. Safe to run twice; it changes no data. The v1.1, v1.2 and
-- v1.3 scripts must already have been applied. It is the PostgreSQL side of a lesson from the v1.3.1 hotfix: the production tables
-- were written by hand, so they can differ from the EF model in ways that every test (built from the model) cannot see. v1.3.1 was a
-- foreign key AuditLogs.UserId -> Users that the model does not have. This script removes three more kinds of difference that the
-- code would trip over on the first request that needs them:
--   1. NOT NULL on a column the code deliberately leaves empty (the model says nullable): the account links of a card payment or of
--      a closed account's history ("Transactions"), the destination and amount of a card auto-pay order, the interest rate and
--      maturity date of a demand account, the hash of a legacy card, the owner of a chat after the user is gone, the pending one-time
--      code columns, the "used" and "revoked" times of a refresh token. The statement is skipped for a column that is already
--      nullable or that does not exist.
--   2. A CHECK constraint on "ChatMessages" that mentions "Sender": v1.3 writes the sender "System" (transfer results, hand-over
--      notes), which an old list of senders ('User', 'AI', 'Agent') would reject.
--   3. Text columns that are narrower than the model's limits (they are only ever WIDENED here, never shrunk).
-- Nothing here touches foreign keys: the code does not depend on ON DELETE behaviour (closing an account detaches its history
-- itself), and docs/deploy/schema-check.sql lists the production foreign keys for comparison with the model.

BEGIN;

-- 1. Columns the model leaves nullable -----------------------------------------------------------------------------------------
DO $$
DECLARE c record;
BEGIN
    FOR c IN
        SELECT i.table_name, i.column_name
        FROM information_schema.columns i
        JOIN (VALUES
            ('Transactions',   'SourceAccountId'),
            ('Transactions',   'DestinationAccountId'),
            ('StandingOrders', 'DestinationAccountNumber'),
            ('StandingOrders', 'Amount'),
            ('StandingOrders', 'CreditCardId'),
            ('Accounts',       'InterestRate'),
            ('Accounts',       'MaturityDate'),
            ('CreditCards',    'CardNumberHash'),
            ('ChatSessions',   'UserId'),
            ('Users',          'TwoFactorSecret'),
            ('Users',          'TwoFactorExpiry'),
            ('Users',          'PendingOtpPurpose'),
            ('Users',          'PendingOtpBinding'),
            ('Users',          'LockoutEnd'),
            ('RefreshTokens',  'UsedAt'),
            ('RefreshTokens',  'RevokedAt')
        ) AS w(table_name, column_name) ON i.table_name = w.table_name AND i.column_name = w.column_name
        WHERE i.table_schema = 'public' AND i.is_nullable = 'NO'
    LOOP
        EXECUTE format('ALTER TABLE %I ALTER COLUMN %I DROP NOT NULL', c.table_name, c.column_name);
    END LOOP;
END $$;

-- 2. A list of allowed chat senders that does not know "System" ----------------------------------------------------------------
DO $$
DECLARE k record;
BEGIN
    IF to_regclass('public."ChatMessages"') IS NOT NULL THEN
        FOR k IN
            SELECT conname
            FROM pg_constraint
            WHERE conrelid = 'public."ChatMessages"'::regclass
              AND contype = 'c'
              AND pg_get_constraintdef(oid) ILIKE '%Sender%'
        LOOP
            EXECUTE format('ALTER TABLE "ChatMessages" DROP CONSTRAINT %I', k.conname);
        END LOOP;
    END IF;
END $$;

-- 3. Text columns narrower than the model: widen them (never narrow) ------------------------------------------------------------
DO $$
DECLARE c record;
BEGIN
    FOR c IN
        SELECT i.table_name, i.column_name, w.max_length
        FROM information_schema.columns i
        JOIN (VALUES
            ('AuditLogs',    'Action',      50),
            ('AuditLogs',    'IpAddress',   64),
            ('ChatMessages', 'Sender',      20),
            ('ChatSessions', 'Title',      100),
            ('Transactions', 'Description', 200),
            ('Transactions', 'Category',    50),
            ('Accounts',     'AccountType', 30),
            ('Accounts',     'ExpiryDate',  10),
            ('CreditCards',  'ExpiryDate',  10),
            ('RefreshTokens','CreatedByIp', 64)
        ) AS w(table_name, column_name, max_length) ON i.table_name = w.table_name AND i.column_name = w.column_name
        WHERE i.table_schema = 'public'
          AND i.data_type = 'character varying'
          AND i.character_maximum_length < w.max_length
    LOOP
        EXECUTE format('ALTER TABLE %I ALTER COLUMN %I TYPE varchar(%s)', c.table_name, c.column_name, c.max_length);
    END LOOP;
END $$;

-- 4. User names are unique regardless of case ---------------------------------------------------------------------------------
-- The application compares user names without regard to case, but the unique index on "Username" is case-sensitive in PostgreSQL:
-- two registrations "Ali" and "ali" that arrive together both got in. This index closes that gap. If case-only duplicates already
-- exist the index cannot be built: the script then says so and leaves the data alone (rename one of each pair, run it again).
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM "Users" GROUP BY lower("Username") HAVING count(*) > 1) THEN
        RAISE NOTICE 'IX_Users_Username_Lower was NOT created: user names that differ only in case exist. Find them with: select lower("Username"), count(*) from "Users" group by 1 having count(*) > 1;';
    ELSE
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_Username_Lower" ON "Users" (lower("Username"));
    END IF;
END $$;

COMMIT;

-- Deploy the v1.3.2 API after this script. Rolling back to v1.3.1 afterwards is safe: it only relaxed constraints.
