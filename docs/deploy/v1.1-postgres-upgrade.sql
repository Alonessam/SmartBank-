-- SmartBank v1.1: card data hardening (PostgreSQL / Supabase)
--
-- Run ONCE in the Supabase SQL editor BEFORE deploying the v1.1 API.
-- The production tables were created by hand, so compare the table and column names below with the
-- Supabase Table Editor first. This is the PostgreSQL equivalent of the EF migration
-- 20261002140531_HardenCardData (which targets SQL Server / LocalDB).

BEGIN;

-- 1) The CVV is no longer stored anywhere.
ALTER TABLE "Accounts"    DROP COLUMN IF EXISTS "EncryptedCardCvv";
ALTER TABLE "CreditCards" DROP COLUMN IF EXISTS "EncryptedCardCvv";

-- 2) Keyed hash (HMAC-SHA256 hex, 64 chars) used to detect duplicate card numbers.
--    NULL for legacy rows. PostgreSQL allows many NULLs in a unique index.
ALTER TABLE "CreditCards" ADD COLUMN IF NOT EXISTS "CardNumberHash" varchar(64);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_CreditCards_CardNumberHash" ON "CreditCards" ("CardNumberHash");

-- 3) Brute-force protection and one-time-code hardening
--    (EF migration 20261002141913_AddOtpAndLockoutFields). Use the same timestamp type as "TwoFactorExpiry".
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "FailedLoginCount"  integer NOT NULL DEFAULT 0;
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "LockoutEnd"        timestamp with time zone;
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "OtpFailedCount"    integer NOT NULL DEFAULT 0;
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PendingOtpPurpose" integer;          -- 1 Login, 2 Transfer, 3 PasswordReset
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PendingOtpBinding" varchar(64);      -- hash of the transfer a code approves

-- 4) Optimistic-concurrency versions: protect balances and card debt against lost updates
--    (EF migration 20261002143708_AddConcurrencyVersions).
ALTER TABLE "Accounts"    ADD COLUMN IF NOT EXISTS "Version" integer NOT NULL DEFAULT 0;
ALTER TABLE "CreditCards" ADD COLUMN IF NOT EXISTS "Version" integer NOT NULL DEFAULT 0;

-- 5) Explicit precision for the interest rate (EF migration 20261002144644_SetInterestRatePrecision). An annual rate such as 52.50.
ALTER TABLE "Accounts" ALTER COLUMN "InterestRate" TYPE numeric(5,2);

-- 6) Roles (EF migration 20261002145306_AddUserRole). Support-agent access used to go to ANY account whose username contained
--    "agent", which anyone could get by registering such a name. It is now a real column (0 = Customer, 1 = Agent) that
--    nothing in the API can set, so existing accounts all become customers. Promote the support staff by hand:
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "Role" integer NOT NULL DEFAULT 0;
-- UPDATE "Users" SET "Role" = 1 WHERE "Username" = 'agent1';   -- run for each real support agent, then sign in again

COMMIT;

-- 7) OPTIONAL, recommended for the demo database.
--    Existing card numbers were encrypted with the OLD key and the OLD format (AES-CBC, fixed IV), so v1.1
--    cannot decrypt them: the API returns an empty card number for those rows. All data in the demo
--    database is fake. For a clean start, delete it (this removes ALL users, accounts, cards and
--    transactions). Un-comment to run:
--
-- TRUNCATE TABLE "Users" CASCADE;
