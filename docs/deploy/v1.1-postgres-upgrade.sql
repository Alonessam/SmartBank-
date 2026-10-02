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

COMMIT;

-- 3) OPTIONAL, recommended for the demo database.
--    Existing card numbers were encrypted with the OLD key and the OLD format (AES-CBC, fixed IV), so v1.1
--    cannot decrypt them: the API returns an empty card number for those rows. All data in the demo
--    database is fake. For a clean start, delete it (this removes ALL users, accounts, cards and
--    transactions). Un-comment to run:
--
-- TRUNCATE TABLE "Users" CASCADE;
