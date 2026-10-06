# Contributing

Thanks for looking at SmartBank. It is a personal portfolio project, but issues and pull requests are welcome.

## Set up

Requirements: the .NET 10 SDK (`global.json` pins the SDK band) and, optionally, PowerShell and Docker.

```bash
dotnet tool restore          # installs dotnet-ef from .config/dotnet-tools.json
dotnet build SmartBank.slnx
dotnet test SmartBank.slnx   # database tests are skipped unless you set SMARTBANK_TEST_POSTGRES / SMARTBANK_TEST_SQLSERVER
```

The README explains how to run the API, the web app and the real-database tests (`docker compose up -d` starts a local
PostgreSQL for them).

## Pull requests

- Branch from `main`, keep a pull request to one topic.
- Use [Conventional Commits](https://www.conventionalcommits.org/) for messages (`fix:`, `feat:`, `test:`, `docs:`, `ci:`, `chore:`).
- Add or update tests. For a bug fix, the test should fail before the change and pass after it.
- Run `dotnet test SmartBank.slnx` and, for web changes, `node --check` on the edited script.
- Never commit secrets. Keys come from user-secrets (`scripts/dev-secrets.ps1`) or environment variables.
- If behaviour or configuration changes, update the README and `CHANGELOG.md`.

## Security issues

Do not open a public issue; follow [SECURITY.md](SECURITY.md).

## Conduct

Be respectful and constructive. Harassment of any kind is not acceptable.
