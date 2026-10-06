-- SmartBank v1.3: user version, unique indexes, query indexes (PostgreSQL / Supabase)
--
-- Run ONCE in the Supabase SQL editor BEFORE deploying the v1.3 API (the API reads "Users"."Version" at every sign-in).
-- This is the PostgreSQL equivalent of the EF migration 20261006155224_V13Hardening (which targets SQL Server / LocalDB).
-- It is safe to run twice, and safe on existing data: duplicate rows are removed (the OLDEST row is kept) before a unique
-- index is created. The v1.1 and v1.2 scripts must already have been applied.
--
-- What it does
--   1. "Users"."Version": optimistic-concurrency token, so one-time codes cannot be used twice and wrong-guess counters
--      cannot be lost when two requests collide.
--   2. One credit card per customer and one saved entry per recipient are now enforced by the database.
--      Duplicates (only possible through a race in v1.2 and earlier) are merged first:
--        - saved recipients: the newer duplicates are deleted;
--        - credit cards: the newer duplicate cards are folded into the oldest card of that customer. Their statements,
--          card transactions and standing orders are moved to it and their debt is added to its debt, then they are deleted.
--   3. Indexes for the queries the application runs most: account history by time, audit trail by user and time, the
--      standing-order worker's "due orders" scan, chat messages by session and time, and the foreign keys the hand-made
--      production tables never had indexes for.
--
-- What it deliberately does NOT do
--   The EF model (and the SQL Server migration) now limits some text columns: AuditLogs.Action 50 and IpAddress 64,
--   Accounts.AccountType 30 and ExpiryDate 10, Users.FirstName and LastName 50. This script does NOT shrink the existing
--   production columns: narrowing a column can fail or cut text that is already stored, and nothing is gained for the
--   application, which now validates the input lengths itself and never writes more than the limits. If you want the
--   columns to match the model exactly, check the longest stored values first and run the ALTER TABLE ... TYPE statements
--   by hand.

BEGIN;

-- 1. Version column of users ------------------------------------------------------------------------------------------
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "Version" integer NOT NULL DEFAULT 0;

-- 2a. One saved entry per recipient: delete newer duplicates (keep the oldest) ----------------------------------------
DELETE FROM "SavedContacts" sc
USING "SavedContacts" older
WHERE older."UserId" = sc."UserId"
  AND older."AccountNumber" = sc."AccountNumber"
  AND (older."CreatedAt" < sc."CreatedAt" OR (older."CreatedAt" = sc."CreatedAt" AND older."Id" < sc."Id"));

-- 2b. One credit card per customer: fold newer duplicates into the oldest card ----------------------------------------
DO $$
BEGIN
    CREATE TEMP TABLE _card_merge ON COMMIT DROP AS
    SELECT c."Id" AS removed_id,
           (SELECT k."Id" FROM "CreditCards" k WHERE k."UserId" = c."UserId" ORDER BY k."CreatedAt", k."Id" LIMIT 1) AS kept_id
    FROM "CreditCards" c
    WHERE EXISTS (SELECT 1 FROM "CreditCards" o
                  WHERE o."UserId" = c."UserId"
                    AND (o."CreatedAt" < c."CreatedAt" OR (o."CreatedAt" = c."CreatedAt" AND o."Id" < c."Id")));

    UPDATE "CreditCardStatements" s SET "CreditCardId" = m.kept_id FROM _card_merge m WHERE s."CreditCardId" = m.removed_id;
    UPDATE "CreditCardTransactions" t SET "CreditCardId" = m.kept_id FROM _card_merge m WHERE t."CreditCardId" = m.removed_id;
    UPDATE "StandingOrders" o SET "CreditCardId" = m.kept_id FROM _card_merge m WHERE o."CreditCardId" = m.removed_id;

    UPDATE "CreditCards" k
    SET "CurrentDebt" = k."CurrentDebt" + COALESCE((SELECT SUM(r."CurrentDebt")
                                                   FROM "CreditCards" r JOIN _card_merge m ON m.removed_id = r."Id"
                                                   WHERE m.kept_id = k."Id"), 0),
        "Version" = k."Version" + 1
    WHERE k."Id" IN (SELECT kept_id FROM _card_merge);

    DELETE FROM "CreditCards" WHERE "Id" IN (SELECT removed_id FROM _card_merge);
END $$;

-- 3. Unique indexes ----------------------------------------------------------------------------------------------------
-- "IX_CreditCards_UserId" may already exist as a plain (non-unique) index: replace it by the unique one.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'IX_CreditCards_UserId' AND indexdef NOT LIKE 'CREATE UNIQUE%') THEN
        DROP INDEX "IX_CreditCards_UserId";
    END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS "IX_CreditCards_UserId" ON "CreditCards" ("UserId");

CREATE UNIQUE INDEX IF NOT EXISTS "IX_SavedContacts_UserId_AccountNumber" ON "SavedContacts" ("UserId", "AccountNumber");
DROP INDEX IF EXISTS "IX_SavedContacts_UserId"; -- the unique index above starts with the same column

-- 4. Indexes for the common lookups ------------------------------------------------------------------------------------
-- The two-column indexes replace the single-column foreign-key indexes (their first column serves the same lookups).
CREATE INDEX IF NOT EXISTS "IX_Transactions_SourceAccountId_CreatedAt"      ON "Transactions" ("SourceAccountId", "CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_Transactions_DestinationAccountId_CreatedAt" ON "Transactions" ("DestinationAccountId", "CreatedAt");
DROP INDEX IF EXISTS "IX_Transactions_SourceAccountId";
DROP INDEX IF EXISTS "IX_Transactions_DestinationAccountId";

CREATE INDEX IF NOT EXISTS "IX_AuditLogs_UserId_CreatedAt" ON "AuditLogs" ("UserId", "CreatedAt");

CREATE INDEX IF NOT EXISTS "IX_StandingOrders_IsActive_NextExecutionDate" ON "StandingOrders" ("IsActive", "NextExecutionDate");

CREATE INDEX IF NOT EXISTS "IX_ChatMessages_SessionId_CreatedAt" ON "ChatMessages" ("SessionId", "CreatedAt");
DROP INDEX IF EXISTS "IX_ChatMessages_SessionId";

-- Foreign-key indexes the model has and the hand-made production tables may lack.
CREATE INDEX IF NOT EXISTS "IX_Accounts_UserId"                        ON "Accounts" ("UserId");
CREATE INDEX IF NOT EXISTS "IX_ChatSessions_UserId"                    ON "ChatSessions" ("UserId");
CREATE INDEX IF NOT EXISTS "IX_CreditCardStatements_CreditCardId"      ON "CreditCardStatements" ("CreditCardId");
CREATE INDEX IF NOT EXISTS "IX_CreditCardTransactions_CreditCardId"    ON "CreditCardTransactions" ("CreditCardId");
CREATE INDEX IF NOT EXISTS "IX_StandingOrders_UserId"                  ON "StandingOrders" ("UserId");
CREATE INDEX IF NOT EXISTS "IX_StandingOrders_CreditCardId"            ON "StandingOrders" ("CreditCardId");

COMMIT;

-- Deploy the v1.3 API after this script. Rolling back the API to v1.2 afterwards is safe: v1.2 ignores the new column and
-- the extra indexes (it would only fail on the unique indexes if it created a second card or a duplicate recipient).
