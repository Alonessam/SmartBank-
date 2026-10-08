# Changelog

All notable changes are listed here, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
with the sections Added, Changed, Fixed and Security in each release. "T10", "T11", ... refer to the sections of
[`docs/DEFENSE.md`](docs/DEFENSE.md), which holds the reasoning behind each change.

## [1.3.2] - 2026-10-07

A second full review (backend, frontend, docs, CI) and the fixes it found. Includes the 1.3.1 registration fix.

### Upgrade notes (v1.3.2)

1. **Run `docs/deploy/v1.3.2-postgres-upgrade.sql` first** in the Supabase SQL editor. It is idempotent: it drops `NOT NULL` on
   columns the code writes as NULL, drops old `CHECK` constraints on `ChatMessages` that mention `Sender`, and widens narrow
   `varchar` columns. `docs/deploy/schema-check.sql` now also lists the production foreign keys.
2. Deploy the API (Render Manual Deploy).
3. Tag the release (`git tag -a v1.3.2 -m "v1.3.2"` and `git push origin v1.3.2`) **before** running `scripts/deploy-pages.ps1 -Push`: the script takes the cache-buster for the scripts and styles from the latest tag, and a stale one would let browsers keep the old JavaScript.

### Changed

- API contract: every chat message carries `sessionId`; the hub has `LeaveSessionAsync(Guid)`; transactions expose
  `sourceCurrency`, `destinationCurrency`, `sourceAmount` and `destinationAmount`; `AuthResponseDto` no longer returns `tckn`;
  account numbers are trimmed and upper-cased; request bodies are limited to 64 KB.
- Chat has its own rate-limit policy (`RateLimiting__Chat__PermitLimit`, `__WindowSeconds`).
- Simulation endpoints answer 404 `SimulationDisabled` when switched off; the anonymous OTP endpoints always answer
  `InvalidOrExpiredCode`; the 2FA status call uses the banking policy.

### Fixed

- Registration and login session creation run in one explicit transaction.
- Race when two requests close the last account of a user (now guarded through `User.Version`).
- Rule B (daily limit) counts only same-currency transfers.
- CORS headers survive the global exception handler, so browsers show the real error instead of a CORS failure.
- Frontend: chat reconnects after an expired token and treats hub refusals as failures; chat history is filtered per session;
  history shows the amount on each side of an exchange; standing orders show their type and active state; the CVV is shown once;
  indicative (fallback) rates are badged and lock the exchange form; auth calls time out after 30 s with a "server waking up"
  hint; a resend-code button; contrast and mobile overflow fixes; the `esc()` helper and `APP_VERSION` were removed (DOM APIs
  are used instead).
- Docs, CI and deploy scripts: baseline schema test (`BaselineSchemaTests`), RUNBOOK and deploy notes corrected.

## [1.3.1] - 2026-10-07

### Fixed

- **Registration failed for everybody on the live site** (found by registering on production after the v1.3 release; the
  request returned 409 `ConcurrentModification` after 18 seconds). The v1.3 code saved the audit row together with the new user.
  An audit row has no navigation to its user, so EF may insert it first, and a foreign key from `AuditLogs.UserId` to `Users`
  (the hand-made production tables can have one; the tables the tests build from the model do not) rejected it. The user is now
  saved first and the audit row afterwards. New real-database tests add such a foreign key to the test database and fail without
  the fix.
- A foreign-key error is retried at most three times instead of ten, so a constraint the code does not expect now fails in about
  a second instead of after 18 seconds.

## [1.3.0] - 2026-10-06

A repository-wide audit pass: money-correctness fixes, security hardening, frontend fixes and a more reproducible,
better-guarded repository. It was found by reading the code and the running app, and every fix has a test (the suite grew
several times over, including real-database tests on PostgreSQL and SQL Server).

### Upgrade notes (v1.3)

Do these in this order. The first two matter: the new API needs the new columns, and the new web app needs the new API.

1. **Run the database script first:** `docs/deploy/v1.3-postgres-upgrade.sql` in the Supabase SQL editor (after the v1.1 and
   v1.2 scripts; it is safe to run twice). It adds `Users.Version`, merges duplicate credit cards (debts are summed into the
   oldest card) and duplicate saved recipients before it creates the unique indexes, and adds the query indexes. It does not
   shrink any existing column.
2. **Deploy the API, then publish the web app** (`scripts/deploy-pages.ps1 -Push` or `scripts/deploy-pages.sh --push`; tag the
   release first, the script takes its cache-buster from the latest tag and refuses a dirty tree or a branch other than `main`).
   Order matters because `POST /api/auth/toggle-2fa` now needs the PIN and the web app sends it.
3. New optional settings, all with defaults (see the README table): `Demo__EnableSimulationEndpoints`,
   `RateLimiting__Banking__PermitLimit`, `RateLimiting__Transfer__PermitLimit`, `RateLimiting__Market__PermitLimit`.
4. Existing sessions keep working. Locked accounts are no longer announced as such (see Security).

### Security

- **Lockout no longer logs the owner out, and no longer reveals which T.C. numbers exist.** Five wrong PINs still lock the
  account for 15 minutes, but failed sign-ins do not revoke sessions any more (anyone who knew a T.C. number could keep signing its
  owner out) and a locked account gets the same `InvalidCredentials` answer as a wrong PIN.
- **One-time codes and counters are safe under concurrency.** `User` has a version token; a code cannot approve two transfers and
  parallel wrong guesses cannot be lost. A public password-reset request no longer overwrites a pending login or transfer code.
- **Turning two-factor authentication on or off needs the PIN** (`POST /api/auth/toggle-2fa` takes `{enable, password}`), counts
  toward the lockout and is audited.
- **Rate limits beyond the auth endpoints:** per-user policies for the banking endpoints (60/min) and for money-moving calls
  (10/min), a per-IP policy for market rates; the demo-only credit-card helpers can be switched off with
  `Demo__EnableSimulationEndpoints=false`.
- **Hub:** invocations after the access token expired are refused (`Session expired`, the web app reconnects with a fresh token);
  ConfirmTransferFromChat works only for the conversation's owner; a customer cannot read or write a session that has no owner.
- **AI path:** stored system messages and free text never reach the model's system prompt; history is capped at 20 messages; at
  most 4 generations run at once with a 20-second deadline that really cancels the call; the FAQ index is built once instead of on
  every message; amounts from the model are parsed culture-independently; in demo mode a one-time code is no longer stored in
  the chat. The Gemini key is sent in a header, not in the URL; no `Console.WriteLine` is left.
- **Less personal data:** the `tckn` claim is gone from the token, the audit trail masks the T.C. number, `SaveToken` is off.
- **Browser:** third-party scripts are pinned with Subresource Integrity (Chart.js, which nothing used, is gone), each page has a
  CSP limited to what it loads (no `unsafe-inline` for styles either), the web app writes no HTML from data at all
  (`innerHTML` is not used), storage access is guarded, and signing out clears every key in every tab.

### Fixed

- **Money correctness** (found by reading the code; none could be caught before because those paths had no tests):
  - A standing order debited the source before it looked up the destination, so a missing destination destroyed the money; it
    also moved money between currencies one to one. Orders are validated when created, re-validated by the worker, which now
    checks first and debits afterwards, writes one ledger row per transfer, honours the maturity date, retries transient errors
    and deactivates an order only for permanent ones (with an audit entry).
  - Credit-card payments above the debt burned the excess (`PaymentExceedsDebt` now); payments are applied to the oldest open
    statements.
  - Exchange and the other money endpoints accepted more than two decimals, which the 2-decimal balance column then rounded
    (free money by repeating tiny trades). Amounts must have at most two decimals (`InvalidAmountScale`) and a computed value
    is rounded once, away from zero, and the same value goes into balances, ledger and response.
  - Exchange accepted any currency code, created an empty account before it had a rate, and opened a second wallet for `usd`.
    Currencies are an allow-list (USD, EUR, XAU, XAG; accounts also TRY), the rate is looked up first and case is normalised.
  - Market rates silently turned into invented prices when the feed failed. Stand-in prices are flagged (`isFallback`), cached
    for 30 seconds and refused for exchange and cross-currency account closing (`RateUnavailable`); implausible values are
    rejected.
  - Closing an account converted one to one when a rate was missing and would have failed on the foreign key. It is now one
    database transaction (closing row, credit, detaching old rows, standing orders switched off, recipients removed, audit) and
    never converts without a live rate.
  - The "unusually high transfer" fraud rule compared a transfer with the average of the user's deposits (not spending) and
    loaded every amount into memory; it now compares with the average of the user's outgoing transfers of the
    last 90 days, computed in SQL. Thresholds use the real currency.
  - A second tab or a parallel request could create a second credit card or duplicate recipients; unique indexes and clean
    errors now. Parallel registrations map to clear errors instead of a 500.
- **Registration with a taken e-mail address** (v1.2) could throw on a real database because the duplicate check was not
  translatable to SQL; caught by the new real-database tests. Names such as "O'Neil-Çelik" are accepted by the server like the web
  form accepts them.
- **Culture-dependent code** (the machine's Turkish culture had already caused one bug): card expiry `MM/yy` was stored as
  "10.31", upper/lower-casing and decimal parsing now use the invariant culture; tests run under tr-TR and de-DE.
- **Agent metrics were invented.** Average response time is now computed from real messages and the CSAT score is empty (there is
  no data behind it).
- **Web app:** double clicks could send a transfer, payment or deposit twice; money fields rejected decimals (`step`); a
  transient refresh failure signed the user out; after a reload or reconnect the chat no longer received live replies; the agent
  panel mixed messages of different sessions; replayed confirmation cards in the history were live; the agent page's role badge was
  invisible; accounts and cards tabs overflowed sideways on phones; about 700 lines of dead code (QR simulator, charts, statement
  modal) and the fake balance history are gone; thirty server error keys now have Turkish and English texts and a test fails when
  a new key has none.
- `deploy-pages.ps1` could publish uncommitted changes and a stale hard-coded version; it now checks both and also versions the
  stylesheet.

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
- **API:** `GET /api/banking/transactions/{accountId}?take=` (default 200, at most 500), bounded lists elsewhere (statements 24, chat messages
  500, active sessions 200), indexes for the common lookups, and one error contract: not found or not yours is 404, a
  concurrency conflict 409, everything else 400, always `{isSuccess, errorKey, message}`.

### Changed

- **Packages** are aligned to 10.0.12 (JwtBearer, EF Core, Hosting, OpenApi; the explicit `Microsoft.OpenApi` override is gone
  because the new OpenApi package brings the fixed version) and the pinned `dotnet-ef` tool matches. No known vulnerable packages.
- **README rewritten** for a quick read: pitch, live demo, try-the-demo steps, three-command quick start, what is new,
  prerequisites, and a complete environment-variable table. Corrected statements that did not match the code (card themes,
  the "Docker image never built" note, rates and time-deposit interest limitations, taken e-mail addresses being revealed).
- **Dockerfile** has named stages and copies only what the API build needs; the build context excludes tests, docs and host
  build output.
- A credit-card payment is recorded as a withdrawal (it used to be a transfer); the transfer success marker in the chat no longer
  carries the free-text description; the closing row of an account stores the credited amount; the OTP e-mail footer says
  "SmartBank (demo)" and a transfer code's e-mail names amount and recipient.
- `.gitignore` no longer names AI-tool folders and no longer ignores every directory called `Release`; the licence holder is the
  repository owner.
- `docs/DEFENSE.md` notes where a later release superseded a statement (the 7-day token, the Docker image that had not yet been
  built, test counts); the upgrade scripts' headers describe their contents and name the real EF migrations.

### Known limitations (unchanged by this release)

Time-deposit interest is displayed, not accrued; there is no idempotency key on money-moving calls, so a client retry after a
timeout can repeat a transfer; standing orders still skip the one-time-code step; the ledger keeps one amount per row rather than
a full double-entry record. See the README's "Known limitations".

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
  a SHA-256 hash, rotated on every use) renews the access token. Logging out or resetting the password
  revokes the sessions (locking the account does not, since 1.3.0), and presenting an already-used refresh token revokes the whole session family. The web app refreshes
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

## [1.0.0] - 2026-06-27

Initial portfolio release (first commit 2026-06-27).

[1.3.2]: https://github.com/Alonessam/SmartBank-/compare/v1.3.1...v1.3.2
[1.3.1]: https://github.com/Alonessam/SmartBank-/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/Alonessam/SmartBank-/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/Alonessam/SmartBank-/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/Alonessam/SmartBank-/releases/tag/v1.1.0
[1.0.0]: https://github.com/Alonessam/SmartBank-/releases/tag/v1.0.0
