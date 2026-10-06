-- SmartBank v1.2: refresh tokens + schema fixes (PostgreSQL / Supabase)
--
-- Run ONCE in the Supabase SQL editor BEFORE deploying the v1.2 API (the API reads the new table at every sign-in).
-- It is safe to run twice. The v1.1 script must already have been applied. Contents:
--   1. the "RefreshTokens" table, the PostgreSQL equivalent of the EF migration 20261005191128_AddRefreshTokens
--      (the EF migrations target SQL Server / LocalDB);
--   2. "ChatSessions"."IsActive", missing from the hand-made production table ("Start Session" did nothing);
--   3. "StandingOrders"."Amount" becomes nullable (credit-card auto-pay orders have no fixed amount);
--   4. every "timestamp without time zone" column becomes "timestamp with time zone" (existing values are read as UTC).
-- Compare with docs/deploy/schema-check.sql afterwards.

BEGIN;

-- Access tokens now live for 15 minutes. A refresh token (stored only as a SHA-256 hash) renews them and is
-- replaced on every use; all tokens of one sign-in share a "FamilyId" so a replayed token can end the whole family.
CREATE TABLE IF NOT EXISTS "RefreshTokens" (
    "Id"          uuid                     NOT NULL,
    "UserId"      uuid                     NOT NULL,
    "TokenHash"   varchar(64)              NOT NULL,
    "FamilyId"    uuid                     NOT NULL,
    "CreatedAt"   timestamp with time zone NOT NULL,
    "ExpiresAt"   timestamp with time zone NOT NULL,
    "UsedAt"      timestamp with time zone,
    "RevokedAt"   timestamp with time zone,
    "CreatedByIp" varchar(64)              NOT NULL,
    "Version"     integer                  NOT NULL,
    CONSTRAINT "PK_RefreshTokens" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_RefreshTokens_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_RefreshTokens_TokenHash" ON "RefreshTokens" ("TokenHash");
CREATE INDEX IF NOT EXISTS "IX_RefreshTokens_UserId"   ON "RefreshTokens" ("UserId");
CREATE INDEX IF NOT EXISTS "IX_RefreshTokens_FamilyId" ON "RefreshTokens" ("FamilyId");

-- The production "ChatSessions" table was created by hand without the "IsActive" column the code writes, so every
-- attempt to start a support chat failed on the server ("Start Session" did nothing). Existing sessions stay open.
ALTER TABLE "ChatSessions" ADD COLUMN IF NOT EXISTS "IsActive" boolean NOT NULL DEFAULT true;

-- A credit-card auto-pay standing order has no fixed amount (it pays the whole statement), so the code stores NULL.
-- The hand-made column was NOT NULL, so creating such an order failed.
ALTER TABLE "StandingOrders" ALTER COLUMN "Amount" DROP NOT NULL;

-- Time zones. The hand-made tables used "timestamp without time zone". The API then sends times such as
-- 2026-10-06T10:27:18 without a trailing "Z", and browsers read that as LOCAL time: in Turkey every time shown was three
-- hours early. The model uses "timestamp with time zone" (like "RefreshTokens" and "Users"."LockoutEnd" already do),
-- which is returned with the "Z". The stored values are UTC, so they are interpreted as UTC. Safe to run twice: only
-- columns that are still "without time zone" are touched. The tables are small, so the rewrite takes moments.
DO $$
DECLARE col record;
BEGIN
    FOR col IN
        SELECT table_name, column_name
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND data_type = 'timestamp without time zone'
          AND table_name IN ('Accounts', 'AuditLogs', 'ChatMessages', 'ChatSessions', 'CreditCards', 'CreditCardStatements',
                             'CreditCardTransactions', 'MarketRates', 'SavedContacts', 'StandingOrders', 'Transactions', 'Users')
    LOOP
        EXECUTE format('ALTER TABLE %I ALTER COLUMN %I TYPE timestamp with time zone USING %I AT TIME ZONE ''UTC''',
                       col.table_name, col.column_name, col.column_name);
    END LOOP;
END $$;

COMMIT;

-- Everyone has to sign in once after the upgrade: tokens issued by v1.1 (7 days, not revocable) keep working until
-- they expire, but they come with no refresh token, so the browser sends the user to the login page.
