# Changelog

## [1.2.0] - unreleased

### Security

- **Fixed a stored cross-site-scripting hole in the web app.** Text from other users (transfer descriptions, contact aliases,
  chat messages, support titles, statement rows) was inserted into the page as HTML, so a transfer description such as
  `<img onerror=...>` ran in the recipient's browser and could read the session token. All such values are now escaped, the
  pages carry a Content-Security-Policy without inline scripts, and tests guard both. Details: `docs/DEFENSE.md` (T10).
  Frontend only: publish it with `scripts/deploy-pages.ps1 -Push`; the API does not need a redeploy for this change.


## [1.1.0] - unreleased

A security and reliability release. Almost every item below was found by reading the v1.0 code and was then reproduced
with a test before it was fixed. The reasoning, alternatives and limits of each change are in
[`docs/DEFENSE.md`](docs/DEFENSE.md).

### Upgrade checklist (read this before deploying)

v1.1 needs configuration and a database change. Deploying it without them makes the API refuse to start (on purpose).

1. **Generate two new secrets.** The old JWT and AES keys were committed to the public repository, so they must be
   treated as compromised and replaced, not just removed. Either of these works:

   ```powershell
   $b = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)   # JwtSettings__Key
   $b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)   # Encryption__Key
   ```
   ```bash
   openssl rand -base64 48   # JwtSettings__Key
   openssl rand -base64 32   # Encryption__Key  (must decode to exactly 32 bytes)
   ```
2. **Set environment variables** on the host (Render): `JwtSettings__Key`, `Encryption__Key`, and keep
   `ConnectionStrings__DefaultConnection`. Optional: `SmtpSettings__*` (to e-mail one-time codes),
   `Demo__ExposeOtp=true` (demo only, see the README), `Cors__AllowedOrigins__0` (defaults to the GitHub Pages site).
3. **Run the database script** [`docs/deploy/v1.1-postgres-upgrade.sql`](docs/deploy/v1.1-postgres-upgrade.sql) once in the
   Supabase SQL editor *before* the new API goes live. Compare table and column names with the Table Editor first.
4. **Promote your support staff.** Accounts whose username merely contained "agent" are customers now. Run the
   `UPDATE ... SET "Role" = 1` statement from the script for each real agent.
5. **Merge `release/v1.1` into `main`** (Render deploys from `main`) and wait for the deploy to finish. Do not do this
   before steps 1-3: the new API refuses to start without them.
6. **Check** `GET /health` and `GET /health/ready`.
7. **Publish the frontend** with `./scripts/deploy-pages.ps1` (dry run, shows what changes) and then
   `./scripts/deploy-pages.ps1 -Push`. The new frontend needs the new API (role in the sign-in response, two-step password
   reset), so publish it after step 6. Then tag `v1.1.0`.
8. **Sign in again** (old tokens are invalid because the key changed). Card numbers created by v1.0 can no longer be
   decrypted (the SQL script explains the optional demo-data reset).

### Security

- **Support chat authorization.** "Support agent" used to mean "the username contains `agent`", so anyone could register
  as one. Roles are now stored in the database and in the token; only an administrator can grant `Agent`. Agent-only
  endpoints (`active-sessions`, `agent-metrics`, `suggest-response`, `transfer-session`) and SignalR methods now check it.
  Customers could previously join any conversation, write into it as "Agent", close it or subscribe to every new request.
- **Account takeover.** Password reset required only a T.C. number; it now needs a code e-mailed to the owner (same
  answer whether or not the number exists, with a cooldown).
- **2FA codes** were returned in login and transfer responses and printed to the console. They are not returned any more
  (unless the explicit demo flag is on) and are never logged outside it. Codes are single-use, expire after 5 minutes,
  die after 5 wrong guesses, are bound to a purpose and, for transfers, to the exact amount and recipient.
- **Brute force.** 5 wrong PINs lock the account for 15 minutes, a per-IP rate limit covers the auth endpoints, and
  unknown T.C. numbers get the same answer and similar timing as wrong PINs.
- **Secrets** (JWT key, AES key and IV) were committed in source and `appsettings.json`. They now come from user-secrets
  or environment variables and the API fails fast without them.
- **Card data.** AES-CBC with a fixed IV became AES-256-GCM with a random nonce (tampering is detected, keys are derived
  with HKDF). Duplicate cards are found with a keyed HMAC instead of comparing ciphertext. The CVV is no longer stored.
  Card numbers, CVVs, OTPs and account numbers come from a cryptographic random generator instead of `System.Random`.
- **CORS** accepted every origin together with credentials; it now uses an allow-list.
- **Error handling.** Production responses no longer contain exception messages; they carry a generic text and a
  `traceId`. The `/db-check` endpoint (which returned database errors) and the ASP.NET weather template are gone.
- Removed the unused developer endpoints `test-setup`, `test-ai`, `test-send-message` and `test-rag`.
- The audit trail records the caller's real IP instead of a hard-coded `127.0.0.1`.
- Vulnerable `Microsoft.OpenApi 2.0.0` replaced with 2.7.5.

### Reliability

- **Concurrent money movements** (transfer, deposit, exchange, card payment and charge, account closing) used to
  overwrite each other's balances; on a real database money was created out of thin air. Accounts and cards now carry a
  version number and operations retry on conflicts and deadlocks.
- **Standing-order worker.** A transient conflict no longer deactivates a customer's order, two worker instances cannot
  run the same order twice, half-applied changes are discarded on failure, and automatic card payments
  (`CreditCardAutoPay`) are recognised (they were deactivated as having an invalid amount).
- Added `/health` (liveness) and `/health/ready` (database).

### Data and database

- New migrations: `HardenCardData`, `AddOtpAndLockoutFields`, `AddConcurrencyVersions`, `SetInterestRatePrecision`,
  `AddUserRole`, with a tested PostgreSQL equivalent in `docs/deploy`.
- Fixed Turkish text that an editing tool had double-encoded in source files, including the default transfer category
  written to the database. A test now guards against it.

### Delivery

- GitHub Actions: build, tests with a PostgreSQL service, vulnerable-package audit, `System.Random` guard, failure on any
  skipped test. Dependabot for NuGet and Actions.
- Docker image runs as a non-root user.
- 187 tests: unit, real-database (SQL Server and PostgreSQL) and integration (whole app over HTTP and SignalR).

### Documentation

- README: honest feature descriptions, a security model and known limitations, an architecture diagram, setup for secrets,
  tests and support agents. `docs/V1.1-PLAN.md` and `docs/DEFENSE.md` explain what was done and why.

## [1.0.0]

Initial portfolio release.
