# Changelog

All notable changes are listed here, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
with the sections Added, Changed, Fixed and Security in each release. "T10", "T11", ... refer to the sections of
[`docs/DEFENSE.md`](docs/DEFENSE.md), which holds the reasoning behind each change.

## [1.3.0] - unreleased

A repository-wide audit pass: money-correctness fixes, security hardening, frontend fixes and a more reproducible,
better-guarded repository. The detailed commit history is the reference for individual items.

### Upgrade notes (v1.3)

1. **Run the database script first:** `docs/deploy/v1.3-postgres-upgrade.sql` in the Supabase SQL editor (after the v1.1 and
   v1.2 scripts), then deploy the API. See "Upgrading a PostgreSQL deployment" in the README.
2. New optional settings (all have defaults, see the README table): `Demo__EnableSimulationEndpoints`,
   `RateLimiting__Banking__PermitLimit`, `RateLimiting__Transfer__PermitLimit`, `RateLimiting__Market__PermitLimit`.
3. Publish the frontend with `scripts/deploy-pages.ps1 -Push` (or `scripts/deploy-pages.sh --push`). It now refuses to
   run from a dirty working tree or from a branch other than `main`, and takes the cache-buster from the latest git tag, so tag
   the release first.

### Added

- **Fresh-install schema.** `docs/deploy/00-baseline-postgres.sql` creates an empty PostgreSQL database equal to the EF model
  (generated with `dotnet ef dbcontext script`; regenerate it whenever the model changes). The production database no longer
  has to be reverse-engineered from the upgrade scripts.
- **Local PostgreSQL.** `docker-compose.yml` starts a PostgreSQL 16 container (and, with `--profile api`, the API image) for
  development and for the real-database tests (`SMARTBANK_TEST_POSTGRES`).
- **CI.** Real-database tests on SQL Server 2022 in addition to PostgreSQL 16; `dotnet ef migrations has-pending-model-changes`
  (no model change without a migration); `node --check` on the frontend scripts; a Docker image build with a fail-fast startup
  check; a coverage summary in the job summary; CodeQL for C# and JavaScript; a weekly scheduled run and manual trigger; per-job
  least-privilege permissions. Dependabot groups the Microsoft packages and the test tools and also watches Docker,
  docker-compose and GitHub Actions.
- **Repository files.** `.gitattributes`, `.editorconfig`, `global.json`, `Directory.Build.props` (shared `Nullable`,
  `ImplicitUsings`, NuGet audit), a `dotnet-ef` tool manifest (`dotnet tool restore`), `.dockerignore`, `SECURITY.md`,
  `CONTRIBUTING.md`, issue and pull request templates, `CODEOWNERS`.
- **Documentation.** `docs/ARCHITECTURE.md` (layers, request pipeline, authentication flow, deployment topology), `docs/README.md`
  (index), `README.tr.md` (the Turkish README, now a separate file), an English summary at the top of `docs/DEFENSE.md`,
  Bash equivalents of the helper scripts (`scripts/dev-secrets.sh`, `scripts/deploy-pages.sh`), authentication examples in
  `SmartBank.API.http`.

### Changed

- **README rewritten** for a quick read: pitch, live demo, try-the-demo steps, three-command quick start, what is new,
  prerequisites, and a complete environment-variable table. Corrected statements that did not match the code (card themes,
  the "Docker image never built" note, rates and time-deposit interest limitations, taken e-mail addresses being revealed).
- **Dockerfile** has named stages and copies only what the API build needs; the build context excludes tests, docs and host
  build output.
- `.gitignore` no longer names AI-tool folders and no longer ignores every directory called `Release`; the licence holder is the
  repository owner.
- `docs/DEFENSE.md` notes where a later release superseded a statement (the 7-day token, the Docker image that had not yet been
  built, test counts); the upgrade scripts' headers describe their contents and name the real EF migrations.
- Money-correctness, security and frontend changes made in the v1.3 audit pass are described in the commit history of the
  `v1.3/backend` and `v1.3/frontend` branches and summarised in the README sections "Security model" and "Known limitations".

### Fixed

- `deploy-pages.ps1` could publish uncommitted changes and a stale hard-coded version; it now checks both.
- The README listed card themes that do not exist and claimed the Docker image could not be built.

### Security

- Pages deployment, CI and the container build now run with explicit least-privilege settings, and a weekly scheduled audit
  catches advisories that appear without a commit.

## [1.2.0] - 2026-10-06

### Upgrade notes (v1.2, historical)

These were the steps for the v1.2 release; they are kept for reference.

1. **Run the database script first:** [`docs/deploy/v1.2-postgres-upgrade.sql`](docs/deploy/v1.2-postgres-upgrade.sql) in the Supabase SQL
   editor. It creates the `RefreshTokens` table. The new API reads that table at every sign-in, so deploying the API before
   the script makes sign-in fail with a 500.
2. Set `Brevo__ApiKey` and `Brevo__SenderEmail` on Render (see Added below), then merge to `main` (Render redeploys).
3. Publish the frontend with `scripts/deploy-pages.ps1 -Push`. Everybody has to sign in once more.

### Added

- **One-time codes can be e-mailed through Brevo's HTTPS API** (`Brevo__ApiKey`, `Brevo__SenderEmail`, `Brevo__SenderName`).
  The old SMTP path only worked where outgoing SMTP ports are open; Render's free tier blocks them, so password reset and 2FA
  e-mails never arrived in production. SMTP is still used when no Brevo key is set. Details: `docs/DEFENSE.md` (T11).
- `POST /api/auth/refresh` and `POST /api/auth/logout` (see Security, access tokens).

### Changed

- SQL commands are no longer written to the log at the default level (`Microsoft.EntityFrameworkCore.Database.Command` is
  `Warning`): the standing-order worker's query every 30 seconds had filled the production log. Details: `docs/DEFENSE.md` (T15).

### Fixed

- **Starting a support chat did nothing in production.** The hand-made `ChatSessions` table in Supabase had no `IsActive`
  column, so creating a session failed on the server. `docs/deploy/v1.2-postgres-upgrade.sql` now adds it.
- **Production schema fixes** (found by comparing every production column with the model; `docs/deploy/schema-check.sql`):
  creating a credit-card auto-pay standing order failed (`StandingOrders.Amount` was NOT NULL but the code stores NULL), and
  all time columns were `timestamp without time zone`, so the API returned times without a `Z` and the browser showed them
  three hours early in Turkey. The upgrade script converts them to `timestamptz` (existing values are UTC) and is safe to
  run twice. Details: `docs/DEFENSE.md` (T14).
- **Registering with an e-mail address that is already taken returned a 500** ("Registration failed" in the UI) because only the
  database unique index caught it. It is now a clean 400 `EmailAlreadyExists` with a localized message (case-insensitive).

### Security

- **API security headers.** Every API response now carries `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
  `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` and `Referrer-Policy: no-referrer`; `/api` and `/hubs`
  responses are `Cache-Control: no-store`. HSTS (180 days) is sent over HTTPS in production. Details: `docs/DEFENSE.md` (T15).
- **T.C. Kimlik Numarası check digits are validated at registration** (server and form). This catches typos and most random
  numbers; it is not identity verification. Fake numbers such as `11111111111` can no longer be used to register (existing
  accounts are unaffected); `11111111110` and `10000000146` are valid made-up examples for demos. Details: `docs/DEFENSE.md` (T15).
- **Support chat hardening.** The chat is now limited per user: messages are at most 1000 characters, 10 a minute and 100 an
  hour (agents get three times the per-minute allowance), 10 new chats an hour, 5 chat transfers a minute; the hub's message
  size is capped at 16 KB. Hub transfers are validated like the REST endpoint (amount range, description length). Machine
  markers (`[CONFIRM_TRANSFER:`, `[TRANSFER_SUCCESS:`, ...) typed by a customer, an agent or produced by the AI model are made
  inert on the server, and the web app only turns a marker into a card when it comes from the right sender, so nobody can put
  a fake "confirm this transfer" card into someone's chat. Limits are configurable under `Chat:*`. Details: `docs/DEFENSE.md` (T13).
- **Access tokens now last 15 minutes instead of 7 days, and sessions can be ended.** A single-use refresh token (stored only as
  a SHA-256 hash, rotated on every use) renews the access token. Logging out, resetting the password or locking the account
  revokes the sessions, and presenting an already-used refresh token revokes the whole session family. The web app refreshes
  silently and signs the user out when the refresh token is refused. Config: `JwtSettings__AccessTokenMinutes`,
  `JwtSettings__RefreshTokenDays`. Details: `docs/DEFENSE.md` (T12).
- **Fixed a stored cross-site-scripting hole in the web app.** Text from other users (transfer descriptions, contact aliases,
  chat messages, support titles, statement rows) was inserted into the page as HTML, so a transfer description such as
  `<img onerror=...>` ran in the recipient's browser and could read the session token. All such values are now escaped, the
  pages carry a Content-Security-Policy without inline scripts, and tests guard both. Details: `docs/DEFENSE.md` (T10).
  Frontend only: publish it with `scripts/deploy-pages.ps1 -Push`; the API does not need a redeploy for this change.

## [1.1.0] - 2026-10-05

A security and reliability release. Almost every item below was found by reading the v1.0 code and was then reproduced
with a test before it was fixed. The reasoning, alternatives and limits of each change are in
[`docs/DEFENSE.md`](docs/DEFENSE.md) (T1-T9).

### Upgrade notes (v1.1, historical)

v1.1 needed configuration and a database change. Deploying it without them made the API refuse to start (on purpose).

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

### Added

- `/health` (liveness) and `/health/ready` (database) endpoints.
- GitHub Actions: build, tests with a PostgreSQL service, vulnerable-package audit, `System.Random` guard, failure on any
  skipped test. Dependabot for NuGet and Actions.
- 187 tests at the time of the release (unit, real-database on SQL Server and PostgreSQL, and integration tests of the whole
  app over HTTP and SignalR); the suite has grown since.
- New EF migrations: `HardenCardData`, `AddOtpAndLockoutFields`, `AddConcurrencyVersions`, `SetInterestRatePrecision`,
  `AddUserRole`, with a tested PostgreSQL equivalent in `docs/deploy`.
- README: honest feature descriptions, a security model and known limitations, an architecture diagram, setup for secrets,
  tests and support agents. `docs/DEFENSE.md` explains what was done and why.

### Changed

- The Docker image runs as a non-root user.
- The audit trail records the caller's real IP instead of a hard-coded `127.0.0.1`.
- Vulnerable `Microsoft.OpenApi 2.0.0` replaced with 2.7.5.
- Production error responses no longer contain exception messages; they carry a generic text and a `traceId`.
- **Standing-order worker.** A transient conflict no longer deactivates a customer's order, two worker instances cannot
  run the same order twice, half-applied changes are discarded on failure, and automatic card payments
  (`CreditCardAutoPay`) are recognised (they were deactivated as having an invalid amount).

### Removed

- The `/db-check` endpoint (which returned database errors), the ASP.NET weather template, and the unused developer
  endpoints `test-setup`, `test-ai`, `test-send-message` and `test-rag`.

### Fixed

- **Concurrent money movements** (transfer, deposit, exchange, card payment and charge, account closing) used to
  overwrite each other's balances; on a real database money was created out of thin air. Accounts and cards now carry a
  version number and operations retry on conflicts and deadlocks.
- Turkish text that an editing tool had double-encoded in source files, including the default transfer category written to
  the database. A test now guards against it.

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

## [1.0.0]

Initial portfolio release.

[1.3.0]: https://github.com/Alonessam/SmartBank-/compare/v1.2.0...main
[1.2.0]: https://github.com/Alonessam/SmartBank-/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/Alonessam/SmartBank-/releases/tag/v1.1.0
