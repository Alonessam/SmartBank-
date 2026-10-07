## What and why

<!-- One or two sentences: the problem and the change. Link an issue if there is one. -->

## How it was tested

- [ ] `dotnet test SmartBank.slnx` passes
- [ ] New or changed behaviour has a test (a bug fix has a test that failed before the fix)
- [ ] Web changes: `node --check` on the changed scripts, tried in a browser

## Checklist

- [ ] No secrets or personal data in the diff
- [ ] README / CHANGELOG / docs updated if behaviour or configuration changed
- [ ] Database change? The EF migration and the PostgreSQL script in `docs/deploy` both exist
- [ ] Model change? `docs/deploy/00-baseline-postgres.sql` is regenerated (the command is in its header; `BaselineSchemaTests` fails otherwise)
