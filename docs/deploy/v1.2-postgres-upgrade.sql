-- SmartBank v1.2: refresh tokens (PostgreSQL / Supabase)
--
-- Run ONCE in the Supabase SQL editor BEFORE deploying the v1.2 API (the API reads this table at every sign-in).
-- This is the PostgreSQL equivalent of the EF migration 20261005191128_AddRefreshTokens (which targets SQL Server / LocalDB).
-- It is safe to run twice. The v1.1 script must already have been applied.

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

COMMIT;

-- Everyone has to sign in once after the upgrade: tokens issued by v1.1 (7 days, not revocable) keep working until
-- they expire, but they come with no refresh token, so the browser sends the user to the login page.
