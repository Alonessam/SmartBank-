# Contributing

Thanks for looking at SmartBank. It is a personal portfolio project, but issues and pull requests are welcome.

## Set up

Requirements: the .NET 10 SDK (`global.json` pins the SDK band) and, optionally, Docker (a local PostgreSQL, the real-database
tests) and PowerShell (helper scripts; bash versions exist). SQL Server LocalDB is only needed if you want to run the app or
the tests against SQL Server (it comes with Visual Studio or the SQL Server Express LocalDB installer).

```bash
dotnet tool restore          # installs dotnet-ef from .config/dotnet-tools.json
dotnet build SmartBank.slnx
dotnet test SmartBank.slnx   # database tests are skipped unless you set SMARTBANK_TEST_POSTGRES / SMARTBANK_TEST_SQLSERVER
```

To run the database tests on PostgreSQL: `docker compose up -d db`, then set
`SMARTBANK_TEST_POSTGRES=Host=localhost;Username=postgres;Password=postgres` (see the README, "Run the tests"). On a fresh
Windows machine run the PowerShell scripts as `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev-secrets.ps1`:
the default execution policy blocks `./scripts/dev-secrets.ps1`.

The README explains how to run the API, the web app and the real-database tests (`docker compose up -d` starts a local
PostgreSQL for them).

## Pull requests

- Branch from `main`, keep a pull request to one topic.
- Start the commit message with a type and a colon, `type: summary` (`fix:`, `feat:`, `test:`, `docs:`, `ci:`, `chore:`; an area such as
  `auth:` or `banking:` is fine too), in the imperative, one line of at most about 72 characters, details in the body.
- Add or update tests. For a bug fix, the test should fail before the change and pass after it.
- Run `dotnet test SmartBank.slnx` and, for web changes, `node --check` on the edited script.
- Never commit secrets. Keys come from user-secrets (`scripts/dev-secrets.ps1`) or environment variables.
- Keep `.ps1`, `.sh` and `.bat` files ASCII-only (a test checks it): Windows PowerShell 5.1 reads a file without a byte order mark in the ANSI code page.
- If behaviour or configuration changes, update the README and `CHANGELOG.md`.

## Security issues

Do not open a public issue; follow [SECURITY.md](SECURITY.md).

## Conduct

Be respectful and constructive. Harassment of any kind is not acceptable.
